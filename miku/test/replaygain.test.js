'use strict';
// MIKU macOS's ReplayGain with MoodWave's loudness, read from the MIKU checkout itself (not a copy of its rule):
//   mac/app/main/web-audio.js  WebAudioEngine.prototype.rgDb   the gain of a track (off / track / album / source)
//   mac/app/main/main.js       WebAudioEngine.loudness          a module's loudness, the album's from its tracks
// Checked against values worked out here: -18 LUFS reference, held by the true peak, preamp, ±24 dB, tags where
// nothing is measured, "source" only for the module's queue, album = BS.1770 energy mean over its tracks.
//
//   node miku/test/replaygain.test.js --miku <MIKU checkout> [--results results.json]
//
// --results: helper.test.js's results, to level its real measurements as MIKU would.
const fs = require('fs');
const path = require('path');
const Module = require('module');
const T = require('./lib');
const { check, info, group } = T;

const o = T.options({ miku: path.join(require('os').homedir(), 'daphne-test', 'miku') });
const main = path.join(path.resolve(o.miku), 'mac', 'app', 'main');
if (!fs.existsSync(path.join(main, 'web-audio.js'))) { console.error(`no MIKU checkout at ${o.miku} (--miku)`); process.exit(2); }

// web-audio.js needs Electron and MIKU's other parts only for playing; rgDb uses none of them
const stubs = {
  electron: { BrowserWindow: function () { }, ipcMain: { on() { }, handle() { }, removeListener() { } } },
  './common': { Log: { info() { }, error() { }, warn() { } } },
  './library': { isDsd: () => false, isLossy: () => false },
  './ffmpeg': { Ffmpeg: {} },
};
const load0 = Module._load;
Module._load = function (req, parent, ...rest) {
  if (parent && parent.filename && parent.filename.startsWith(main) && stubs[req]) return stubs[req];
  return load0.call(this, req, parent, ...rest);
};
const { WebAudioEngine } = require(path.join(main, 'web-audio.js'));
Module._load = load0;

const rg = (s, t) => WebAudioEngine.prototype.rgDb.call({ s }, t);
const near = (a, b) => a != null && b != null && Math.abs(a - b) < 1e-6;
const top = peak => -20 * Math.log10(peak);
const show = v => v == null ? v : +v.toFixed(4);

/** main.js's WebAudioEngine.loudness, taken out of the source and run with stand-ins for the module and the library. */
function loudnessOfMain(lp, lib) {
  const src = fs.readFileSync(path.join(main, 'main.js'), 'utf8');
  const at = src.indexOf('WebAudioEngine.loudness = {');
  if (at < 0) throw new Error('main.js: WebAudioEngine.loudness not found');
  // the object literal: up to its matching brace
  let depth = 0, end = -1;
  for (let i = src.indexOf('{', at); i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) { end = i + 1; break; }
  }
  const code = src.slice(at, end) + ';';
  const W = {};
  new Function('WebAudioEngine', 'lp', 'lib', 'Log', code)(W, lp, lib, { error() { } });
  return W.loudness;
}

(async () => {
  console.log(`MIKU    ${main}`);

  await group('tags only (no module)', async () => {
    WebAudioEngine.loudness = null;
    const t = { rgTrack: -5, rgTrackPeak: 0.9, rgAlbum: -7, rgAlbumPeak: 0.95 };
    check('off: nothing', rg({ replayGain: 'off' }, t) === null);
    check('track: its tag', near(rg({ replayGain: 'track' }, t), -5));
    check('album: the album tag', near(rg({ replayGain: 'album' }, t), -7));
    check('preamp added', near(rg({ replayGain: 'track', replayGainPreamp: 2 }, t), -3));
    check('raised only up to the peak', near(rg({ replayGain: 'track' }, { rgTrack: 10, rgTrackPeak: 0.8 }), top(0.8)));
    check('no peak tag: only lowered', near(rg({ replayGain: 'track' }, { rgTrack: 3 }), 0));
    check('no tag: nothing', rg({ replayGain: 'track' }, {}) === null);
    check('held to -24 dB', near(rg({ replayGain: 'track' }, { rgTrack: -40, rgTrackPeak: 1 }), -24));
    check('source without a module: nothing', rg({ replayGain: 'source', queueSource: { ext: 'moodchannels' } }, t) === null);
  });

  // a module's loudness, as main.js sets it up from the module's lookup
  const measured = {
    '/m/loud.flac': { lufs: -10, peak: 1.0 },       // gain -8
    '/m/quiet.flac': { lufs: -30, peak: 0.5 },      // wants +12, held at +6.02
    '/m/mid.flac': { lufs: -20, peak: 0.25 },       // +2
    '/m/silent.flac': { lufs: -Infinity, peak: 0 },
  };
  const lp = { id: 'moodchannels', lookup: p => measured[p] || null };
  const albums = {
    a1: { tracks: [{ path: '/m/loud.flac', duration: 60 }, { path: '/m/mid.flac', duration: 180 }] },
    a2: { tracks: [{ path: '/m/loud.flac', duration: 100 }, { path: '/m/unmeasured.flac', duration: 100 }] },
    a3: { tracks: [{ path: '/m/mid.flac', duration: 100 }, { path: '/m/silent.flac', duration: 100 }] },
    a4: { tracks: [{ path: '/m/silent.flac', duration: 100 }] },
    loose: { loose: true, tracks: [{ path: '/m/loud.flac', duration: 60 }, { path: '/m/mid.flac', duration: 60 }] },
  };
  const lib = { getAlbum: id => albums[id] || null };
  let L;

  await group('main.js: the module\'s loudness and the album\'s', async () => {
    L = loudnessOfMain(lp, lib);
    check('source is the module\'s id', L.source === 'moodchannels');
    check('track: the module\'s lookup', L.track({ path: '/m/loud.flac' }).lufs === -10);
    check('track without a file: nothing', L.track({ path: '' }) === null && L.track(null) === null);
    const a1 = L.album({ path: '/m/loud.flac', albumId: 'a1' });
    // energy mean weighted by time: 10·log10((60·10^-1 + 180·10^-2) / 240)
    const want = 10 * Math.log10((60 * 0.1 + 180 * 0.01) / 240);
    check('album: energy mean over its tracks, weighted by duration', a1 && near(a1.lufs, want), { got: show(a1 && a1.lufs), want: show(want) });
    check('album: the highest peak', a1 && a1.peak === 1.0);
    check('album not fully measured: nothing yet', L.album({ path: '/m/loud.flac', albumId: 'a2' }) === null);
    const a3 = L.album({ path: '/m/mid.flac', albumId: 'a3' });
    check('a silent track is left out of the album', a3 && near(a3.lufs, -20), a3);
    check('an album of silence: nothing', L.album({ path: '/m/silent.flac', albumId: 'a4' }) === null);
    check('a loose folder is no album: the track\'s own', L.album({ path: '/m/mid.flac', albumId: 'loose' }).lufs === -20);
    check('no album: the track\'s own', L.album({ path: '/m/mid.flac', albumId: 'none' }).lufs === -20);
  });

  await group('rgDb with MoodWave\'s loudness', async () => {
    WebAudioEngine.loudness = L;
    const tr = { replayGain: 'track' };
    check('measured: -18 LUFS − loudness', near(rg(tr, { path: '/m/loud.flac' }), -8), rg(tr, { path: '/m/loud.flac' }));
    check('raised only up to the true peak', near(rg(tr, { path: '/m/quiet.flac' }), top(0.5)), show(rg(tr, { path: '/m/quiet.flac' })));
    check('measurement before tags', near(rg(tr, { path: '/m/loud.flac', rgTrack: 3, rgTrackPeak: 0.5 }), -8));
    check('preamp added', near(rg({ replayGain: 'track', replayGainPreamp: 1.5 }, { path: '/m/mid.flac' }), 2 + 1.5));
    check('silent: the tag', near(rg(tr, { path: '/m/silent.flac', rgTrack: -4, rgTrackPeak: 1 }), -4));
    check('silent, no tag: nothing', rg(tr, { path: '/m/silent.flac' }) === null);
    check('not measured: the tag', near(rg(tr, { path: '/m/new.flac', rgTrack: -6, rgTrackPeak: 1 }), -6));
    check('not measured, no tag: nothing', rg(tr, { path: '/m/new.flac' }) === null);
    const al = rg({ replayGain: 'album' }, { path: '/m/mid.flac', albumId: 'a1' });
    const alWant = Math.min(-18 - 10 * Math.log10((60 * 0.1 + 180 * 0.01) / 240), top(1.0));
    check('album: from the album\'s loudness', near(al, alWant), { got: show(al), want: show(alWant) });
    check('album not fully measured: its tag', near(rg({ replayGain: 'album' }, { path: '/m/loud.flac', albumId: 'a2', rgAlbum: -9, rgAlbumPeak: 1 }), -9));
    const src = q => rg({ replayGain: 'source', queueSource: q }, { path: '/m/loud.flac', rgTrack: -2, rgTrackPeak: 1 });
    check('source: MoodWave\'s queue is levelled', near(src({ ext: 'moodchannels', channel: 3 }), -8), src({ ext: 'moodchannels' }));
    check('source: another queue is not (tags neither)', src({ ext: 'other' }) === null && src(null) === null && src(undefined) === null);
  });

  if (o.results) {
    await group('real measurements from helper.test.js', async () => {
      const r = JSON.parse(fs.readFileSync(o.results, 'utf8'));
      const real = {};
      for (const [name, f] of Object.entries(r.files)) if (f.loudness) real['/r/' + name] = f.loudness[0] == null ? { lufs: -Infinity, peak: 0 } : { lufs: f.loudness[0], peak: f.loudness[1] };
      WebAudioEngine.loudness = loudnessOfMain({ id: 'moodchannels', lookup: p => real[p] || null }, { getAlbum: () => null });
      let n = 0, bad = [];
      for (const [p, m] of Object.entries(real)) {
        const g = rg({ replayGain: 'track' }, { path: p });
        const want = isFinite(m.lufs) ? Math.max(-24, Math.min(24, Math.min(-18 - m.lufs, top(m.peak)))) : null;
        if (!(want === null ? g === null : near(g, want))) bad.push([p, g, want]);
        n++;
        if (/peaky|noise-a|music/.test(p)) info(`${path.basename(p)}: ${m.lufs} LUFS, peak ${m.peak.toFixed(3)} → ${show(g)} dB`);
      }
      check(`${n} measured files levelled as worked out`, bad.length === 0, bad);
      const pk = real['/r/peaky.wav'];
      check('peaky held at its peak, not raised to -18 LUFS', pk && near(rg({ replayGain: 'track' }, { path: '/r/peaky.wav' }), top(pk.peak)));
    });
  }

  T.summary(o.report);
})().catch(e => { console.error(e); process.exit(2); });
