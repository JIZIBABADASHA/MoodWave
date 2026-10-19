'use strict';
// miku/extension.js inside a stand-in for MIKU's host (mac/app/main/main.js loadExtensions): the helper started with
// the saved settings, the library passed on (live and path-less tracks left out), the page's calls and events,
// settings saved through MIKU, loudness for ReplayGain (lookup), what is about to play first, plays and the
// continuation (MIKU's onTrackPlayed and 「佇列播完後」), a library change, a crashed helper started again, stop, and a
// module without its helper.
//
//   node miku/test/extension.test.js [--helper <moodwave-helper>] [--ffmpeg <ffmpeg>] [--work <dir>]
//
// Without --helper the module finds its helper itself (miku/bin/<arch>/), as inside MIKU.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');
const T = require('./lib');
const { check, info, group, until, sleep } = T;

const o = T.options({});
const work = T.workDir(o);
const ffmpeg = T.findFfmpeg(o);
if (o.helper) process.env.MOODWAVE_HELPER = path.resolve(o.helper);
const audio = path.join(work, 'audio');
const names = T.makeFixtures(audio, ffmpeg);
const tracks = T.library(audio, names);
const byName = Object.fromEntries(tracks.map(t => [t.name, t]));
const album = tracks.filter(t => t.name.startsWith('album/'));
const EXT = path.join(T.ROOT, 'miku', 'extension.js');

/** A fresh copy of the module (it keeps its state in the module scope, as one module per MIKU). */
function load(file = EXT) {
  delete require.cache[require.resolve(file)];
  return require(file);
}

/** What MIKU gives a module, recording what the module does with it. */
function makeHost(dataDir, settings = {}) {
  const h = {
    dataDir, extensionDir: path.dirname(EXT), ffmpegPath: ffmpeg,
    // a live stream and a track without a file: never sent to the helper
    tracks: [...tracks, { id: 'yt-live', path: 'https://example.invalid/live', album: '' }, { id: 'nofile', path: '', album: '' }],
    queue: [],
    posts: [], logs: [], saved: [], settings: { ...settings },
    libraryFns: [], playbackFns: [], playedFns: [],
    upcoming(n) { return this.queue.slice(0, n); },
    onTrackPlayed(fn) { this.playedFns.push(fn); },
    onLibraryChanged(fn) { this.libraryFns.push(fn); },
    onPlaybackChanged(fn) { this.playbackFns.push(fn); },
    post(ev, data) { this.posts.push({ ev, data }); },
    getSetting(k) { return this.settings[k] ?? null; },
    setSetting(k, v) { this.saved.push([k, v]); this.settings[k] = v; },
    log(m) { this.logs.push(m); },
  };
  h.libraryChanged = () => h.libraryFns.forEach(f => f());
  h.playbackChanged = () => h.playbackFns.forEach(f => f());
  return h;
}

/** The helpers this process started (pid list); posix only. */
function helperPids() {
  if (process.platform === 'win32') return null;
  const ps = execFileSync('ps', ['-A', '-o', 'pid=,ppid=,command='], { encoding: 'utf8' });
  return ps.split('\n').map(l => l.trim().match(/^(\d+)\s+(\d+)\s+(.*)$/)).filter(m => m && +m[2] === process.pid && /moodwave-helper/.test(m[3])).map(m => +m[1]);
}

(async () => {
  console.log(`module  ${EXT}\nhelper  ${process.env.MOODWAVE_HELPER || '(the module\'s own bin/)'}\n${os.platform()} ${os.arch()}`);
  const ext = load();
  const host = makeHost(T.freshDir(work, 'ext-data'), { threads: 2, enabled: true });

  await group('module shape', async () => {
    check('id moodchannels', ext.id === 'moodchannels');
    check('start / stop / handleRpc', ['start', 'stop', 'handleRpc'].every(k => typeof ext[k] === 'function'));
    check('a loudness provider named MoodWave', ext.loudness && ext.loudness.name === 'MoodWave' && typeof ext.loudness.lookup === 'function');
    check('nothing known before start', ext.loudness.lookup(byName['noise-a.wav'].path) === null);
    check('a continuation named MoodWave Smart Playlist', ext.continuation && ext.continuation.name === 'MoodWave Smart Playlist' &&
      typeof ext.continuation.continue === 'function');
  });

  await group('start', async () => {
    ext.start(host);
    check('library, playback and plays listened to', host.libraryFns.length === 1 && host.playbackFns.length === 1 && host.playedFns.length === 1);
    const s = await until(async () => { const s = await ext.handleRpc('status'); return s && s.engine && s; }, 120000, 'the queue up');
    check('saved settings reached the helper (threads 2)', s.threads === Math.min(2, os.cpus().length), s.threads);
    check('live and path-less tracks left out', s.total === tracks.length, s.total);
    check('helper log lines go to MIKU\'s log', host.logs.some(l => /^model /.test(l)), host.logs.slice(0, 4));
  });

  await group('what is about to play first', async () => {
    await ext.handleRpc('pause');
    await sleep(2000);
    host.queue = [album[1].id, byName['noise-b.wav'].id, byName['f1-white.wav'].id];
    const n = host.logs.length;
    host.playbackChanged();
    await until(() => host.logs.slice(n).some(l => /files about to play put first/.test(l)), 10000, 'the "put first" log');
    check('the track playing, the next ones and its album put first', true, host.logs.slice(n).find(l => /put first/.test(l)));
    await until(() => [album[1], byName['noise-b.wav']].every(t => ext.loudness.lookup(t.path)), 180000, 'them measured');
    check('their loudness is there for ReplayGain', true);
    await ext.handleRpc('resume');
  });

  await group('analysis, events, loudness for ReplayGain', async () => {
    await until(async () => { const s = await ext.handleRpc('status'); return s.pending === 0 && s.running === 0 && s.loudness.pending === 0; }, 20 * 60000, 'the analysis', 2000);
    await sleep(1500);                                     // loudness goes out in batches, once a second
    check('progress events posted to the page', host.posts.some(p => p.ev === 'progress' && p.data && p.data.total === tracks.length));
    const a = ext.loudness.lookup(byName['noise-a.wav'].path);
    check('lookup: lufs and peak', a && isFinite(a.lufs) && a.peak > 0, a);
    const sil = ext.loudness.lookup(byName['silence.wav'].path);
    check('lookup of silence: -Infinity, nothing to level', sil && sil.lufs === -Infinity && sil.peak === 0, sil && String(sil.lufs));
    check('lookup of the broken file: null', ext.loudness.lookup(byName[T.BROKEN].path) === null);
    check('lookup of an unknown file: null', ext.loudness.lookup('/no/such/file.flac') === null);
    const missing = tracks.filter(t => t.name !== T.BROKEN && !ext.loudness.lookup(t.path));
    check('every measured file can be looked up', missing.length === 0, missing.map(t => t.name));
  });

  await group('the page\'s calls and settings', async () => {
    const n = host.saved.length;
    const s = await ext.handleRpc('set', { threads: 1 });
    check('set answers with the status', s && s.threads === 1, s && s.threads);
    check('settings saved through MIKU', host.saved.slice(n).some(([k, v]) => k === 'threads' && v === 1), host.saved.slice(n));
    const c = await ext.handleRpc('channels', { sample: 3 });
    check('channels', c && c.total === tracks.length && Array.isArray(c.channels), c && c.total);
    const v = await ext.handleRpc('vote', { id: album[0].id, channel: 0, vote: 1 });
    check('vote', v && v.vote === 1, v);
    check('a call without args', (await ext.handleRpc('tuning')).builtin === true);
    check('an unknown call: null', (await ext.handleRpc('nonsense')) === null);
  });

  await group('plays and the continuation (the AI off)', async () => {
    // a play as MIKU reports it: the helper takes it without a word back
    const n = host.logs.length;
    host.playedFns.forEach(f => f({ id: album[0].id, seconds: 20, duration: 20, skipped: false, stopped: false, source: { ext: 'moodchannels', channel: 1 } }));
    host.playedFns.forEach(f => f({ id: 'not-in-library', seconds: 1, duration: 0 }));
    await sleep(500);
    check('plays taken quietly', !host.logs.slice(n).some(l => /error|not recorded/i.test(l)), host.logs.slice(n));
    check('the helper still answers', !!(await ext.handleRpc('status')));
    const t0 = Date.now();
    const c = await ext.continuation.continue({ played: [album[0].id], source: null, chosen: true });
    check('continue without the AI: no picks (MIKU adds random songs), at once', Array.isArray(c) && c.length === 0 && Date.now() - t0 < 5000, { c, ms: Date.now() - t0 });
    const c2 = await ext.continuation.continue({});
    check('continue with nothing given: no picks', Array.isArray(c2) && c2.length === 0, c2);
  });

  await group('library change', async () => {
    const keep = host.tracks;
    host.tracks = keep.filter(t => t.id !== byName['f2-brown.wav'].id);
    for (let i = 0; i < 5; i++) host.libraryChanged();     // a scan: many changes, sent once things settle
    await until(async () => (await ext.handleRpc('status')).total === tracks.length - 1, 20000, 'the smaller library', 1000);
    check('sent after the changes settle; total follows', true);
    host.tracks = keep;
    host.libraryChanged();
    await until(async () => (await ext.handleRpc('status')).total === tracks.length, 20000, 'the library back', 1000);
    check('and back', true);
  });

  await group('a crashed helper starts again', async () => {
    const pids = helperPids();
    if (!pids) { info('left out on Windows (needs ps)'); return; }
    check('one helper running', pids.length === 1, pids);
    const n = host.logs.length;
    process.kill(pids[0], 'SIGKILL');
    await until(() => host.logs.slice(n).some(l => /moodwave-helper stopped/.test(l)), 5000, 'the stop noticed');
    check('the crash logged', true, host.logs.slice(n).find(l => /stopped/.test(l)));
    check('a call meanwhile answers null at once', (await ext.handleRpc('status')) === null);
    const s = await until(async () => { const s = await ext.handleRpc('status'); return s && s.engine && s; }, 30000, 'the helper back');
    check('started again with the library', s.total === tracks.length && s.pending === 0, { total: s.total, pending: s.pending });
    const again = helperPids();
    check('a new process', again.length === 1 && again[0] !== pids[0], again);
    check('loudness still there', !!ext.loudness.lookup(byName['noise-a.wav'].path));
  });

  await group('stop', async () => {
    const pids = helperPids();
    ext.stop();
    if (pids) {
      await until(() => helperPids().length === 0, 8000, 'the helper gone');
      check('the helper exits', true);
      const n = host.logs.length;
      await sleep(3000);
      check('not started again after stop', helperPids().length === 0 && !host.logs.slice(n).some(l => /stopped/.test(l)));
    }
    check('calls answer null after stop', (await ext.handleRpc('status')) === null);
    const c = await ext.continuation.continue({ played: [album[0].id], chosen: true });
    check('continue after stop: no picks', Array.isArray(c) && c.length === 0, c);
  });

  await group('module without its helper', async () => {
    // the module alone in a folder (no bin/, no models/): MoodWave stays off, MIKU goes on
    const dir = T.freshDir(work, 'bare-module');
    fs.copyFileSync(EXT, path.join(dir, 'extension.js'));
    const saved = process.env.MOODWAVE_HELPER;
    delete process.env.MOODWAVE_HELPER;
    const bare = load(path.join(dir, 'extension.js'));
    const h = makeHost(T.freshDir(work, 'bare-data'));
    bare.start(h);
    check('"not found" logged', h.logs.some(l => /moodwave-helper not found/.test(l)), h.logs);
    check('calls answer null', (await bare.handleRpc('status')) === null);
    check('lookup answers null', bare.loudness.lookup(byName['noise-a.wav'].path) === null);
    bare.stop();
    if (saved) process.env.MOODWAVE_HELPER = saved;
  });

  T.summary(o.report);
  process.exit();
})().catch(e => { console.error(e); process.exit(2); });
