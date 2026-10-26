'use strict';
// The advanced AI in moodwave-helper over its protocol: off at first (no radio, nothing to go on with), turned on
// (the model copied from models/clap/ and checked, every analysed file embedded by moodwave-clap, pause and resume),
// the radio (a track's, an album's from its most played track, an artist's; only songs in a channel, one version of
// a song, an album's tracks apart), plays ('played'), what comes after a list ('continue': the Smart Playlist and a
// radio going on), radio votes kept per start, a second start on the same data (nothing embedded again), forgetting
// and deleting.
//
//   node miku/test/ai.test.js --music <folder> [--helper <moodwave-helper>] [--ffmpeg <ffmpeg>] [--work <dir>]
//
// --music: real songs from that folder (only read; 24 by default, --music-count): a radio plays only tracks in a
// channel, and synthetic audio is in none, so without them the radio checks are left out. Needs the model in
// models/clap/ of this checkout (or MOODWAVE_MODEL_SOURCE) and moodwave-clap next to the helper (miku/build-helper.sh).
const fs = require('fs');
const os = require('os');
const path = require('path');
const T = require('./lib');
const { check, info, group, until, sleep } = T;

const o = T.options({});
const work = T.workDir(o);
const ffmpeg = T.findFfmpeg(o);
const exe = T.findHelper(o);
const audio = path.join(work, 'audio');
const names = T.makeFixtures(audio, ffmpeg);
const music = T.findMusic(o.music, Number(o['music-count'] || 24));
// titles and artists: four artists in turn (the broken file is Artist B's), and one song in two versions (two songs
// titled as such, by different artists)
const ARTISTS = ['Artist A', 'Artist B', 'Artist C', 'Artist D'];
const tracks = T.library(audio, names, music).map((t, i) => ({ ...t, artist: ARTISTS[i % ARTISTS.length],
  title: t.id === 'm2' ? 'Moonlight' : t.id === 'm3' ? 'Moonlight (Live)' : path.basename(t.name).replace(/\.\w+$/, '') }));
const byId = Object.fromEntries(tracks.map(t => [t.id, t]));
const songs = tracks.filter(t => t.music);
const synthetic = new Set(tracks.filter(t => !t.music).map(t => t.id));
// an album: the folder with the most of the songs found (its tracks share album and folder)
const folders = {};
for (const t of songs) (folders[path.dirname(t.path)] = folders[path.dirname(t.path)] || []).push(t);
const album = Object.values(folders).sort((a, b) => b.length - a.length)[0] || [];
const version = JSON.parse(fs.readFileSync(path.join(T.ROOT, 'models', 'manifest.json'), 'utf8')).models.clap.version;
const dataDir = T.freshDir(work, 'ai-data');
const start = settings => ({ op: 'start', dataDir, moduleDir: T.ROOT, ffmpeg, settings });
const sendTracks = h => h.send({ op: 'tracks', tracks: tracks.map(t => ({ id: t.id, path: t.path, album: t.album, duration: t.duration,
  size: t.size, mtime: t.mtime, title: t.title, artist: t.artist })) });

/** 'continue' as extension.js asks it: the ids to add (an error counts as none). */
function cont(h, played, source, chosen) {
  const id = h.nextId++;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => { h.waiting.delete(id); reject(new Error('continue: no answer in 30 s')); }, 30000);
    h.waiting.set(id, m => { clearTimeout(timer); resolve(Array.isArray(m.result) ? m.result : []); });
    h.send({ op: 'continue', id, played, source, chosen });
  });
}
const unique = a => new Set(a).size === a.length;
const show = ids => ids.map(id => byId[id] ? byId[id].name : id);
// a radio's picks: no file that failed, no silence, nothing in no channel (the synthetic audio), none twice
const clean = (ids, seed) => unique(ids) && ids.every(id => id === seed || !synthetic.has(id));

console.log(`helper  ${exe}\nmodel   ${version}\nwork    ${work}\n${os.platform()} ${os.arch()}, ${os.cpus().length} cpus, ` +
  `${names.length} test files + ${songs.length} songs (album of ${album.length})`);

(async () => {
  let h = new T.Helper(exe);
  let embedded = 0;           // files the AI embedded
  let trackRadio = null;      // a radio started from one song, for the checks after it

  await group('AI off', async () => {
    h.send(start({ threads: 2, ai: false }));
    await h.waitFor(m => m.ready, 120000, 'ready');
    sendTracks(h);
    const s = await until(async () => { const s = await h.rpc('status'); return s.total === tracks.length && s; }, 20000, 'the library counted');
    check('status.ai there, off', s.ai && s.ai.enabled === false, s.ai && { enabled: s.ai.enabled });
    check('the worker (moodwave-clap) found', s.ai.worker === true, s.ai.worker);
    check('the model: a local copy (no download address yet), not there yet', s.ai.download && s.ai.download.local === true && s.ai.download.ready === false,
      s.ai.download);
    check('no GPU choice on this system', s.ai.gpuCapable === (process.platform === 'win32'), s.ai.gpuCapable);
    const r = await h.rpc('radio.ready');
    check('radio not ready', r.ready === false, r);
    const st = await h.rpc('radio.start', { kind: 'track', ids: [tracks[0].id] });
    check('radio.start: refused, the AI is off', st.ok === false && st.reason === 'ai', st);
    const c = await cont(h, [tracks[0].id], null, true);
    check('continue: nothing (MIKU adds random songs instead)', c.length === 0, c);
  });

  await group('the basic analysis', async () => {
    await until(async () => { const s = await h.rpc('status'); return s.pending === 0 && s.running === 0; }, 30 * 60000, 'the analysis', 2000);
    const s = await h.rpc('status');
    info(`analysed ${s.analysed}, failed ${s.failed}`);
    check('analysed, the broken file failed', s.analysed >= tracks.length - 2 && s.failed >= 1, { analysed: s.analysed, failed: s.failed });
    const ch = await h.rpc('channels', { sample: 0 });
    const inOne = id => ch.channels.some(c => Array.isArray(c) ? c.includes(id) : c && Array.isArray(c.ids) && c.ids.includes(id));
    info(`songs in a channel: ${songs.filter(t => inOne(t.id)).length} of ${songs.length}`);
  });

  await group('AI on: the model', async () => {
    const from = h.messages.length;
    await h.rpc('set', { ai: true, aiSegments: 0 });
    // the AI's queue is set up off the call's thread: the status follows a moment later
    const s = await until(async () => { const s = await h.rpc('status'); return s.ai.enabled && s; }, 10000, 'the AI on');
    check('set ai: the status says on', s.ai.enabled === true, s.ai.enabled);
    check('saved as a setting', h.settings(from).some(m => m.setting === 'ai' && m.value === true), h.settings(from));
    const d = await until(async () => { const s = await h.rpc('status'); return s.ai.download.ready && s.ai.download; }, 10 * 60000, 'the model copied', 1000);
    check('the model ready, all of it', d.done === d.bytes && d.bytes > 100e6 && !d.error, d);
    check('"CLAP model … ready" logged', h.logs(from).some(l => l.startsWith(`CLAP model ${version} ready`)), h.logs(from).find(l => /CLAP model/.test(l)));
    const file = path.join(dataDir, 'models', 'clap', 'clap_audio.onnx');
    check('in the data folder, at its size', fs.existsSync(file) && fs.statSync(file).size === d.bytes, file);
    check('no part file left', !fs.readdirSync(path.dirname(file)).some(n => n.endsWith('.part')), fs.readdirSync(path.dirname(file)));
  });

  await group('AI on: every analysed file embedded', async () => {
    const t0 = Date.now();
    const s = await until(async () => { const s = await h.rpc('status'); return s.ai.total > 0 && s.ai.pending === 0 && s.ai.done === s.ai.total && s; },
      60 * 60000, 'the embeddings', 2000);
    embedded = s.ai.done;
    info(`${embedded} files in ${((Date.now() - t0) / 1000).toFixed(0)} s on ${s.ai.device}, ${s.ai.segments} segments, ${s.ai.threads} threads`);
    check('every file the basic analysis read', s.ai.total === s.analysed, { ai: s.ai.total, analysed: s.analysed });
    check('the model\'s version', s.ai.model === version, s.ai.model);
    check('"process started on …" logged', h.logs().some(l => /^CLAP: process started on /.test(l)), h.logs().find(l => /process started/.test(l)));
    check('no file failed', !h.logs().some(l => /^CLAP failed/.test(l)), h.logs().filter(l => /^CLAP failed/.test(l)));
    let p = await h.rpc('ai.pause');
    check('ai.pause (the basic analysis not paused)', p.ai.paused === true && p.paused !== true, { ai: p.ai.paused, basic: p.paused });
    p = await h.rpc('ai.resume');
    check('ai.resume', p.ai.paused === false, p.ai.paused);
  });

  await group('radio', async () => {
    const r = await until(async () => { const r = await h.rpc('radio.ready'); return r.ready && r; }, 30000, 'radio ready');
    // silence has no direction: its embedding is kept but not in the radio
    check('radio ready with the embeddings', r.count === embedded || r.count === embedded - 1, { radio: r.count, embedded });
    if (songs.length < 8) { info('fewer than 8 songs (--music): the radio checks are left out'); return; }
    const seed = songs[0];
    const t = await h.rpc('radio.start', { kind: 'track', ids: [seed.id], name: seed.title });
    trackRadio = t;
    info('track radio: ' + show(t.ids || []).join(', '));
    check('a track\'s radio starts with it', t.ok && t.ids[0] === seed.id, t.ids && t.ids[0]);
    check('the track and one pick (one at a time)', t.ids.length === 2, t.ids.length);
    check('only songs, none twice', clean(t.ids, seed.id), show(t.ids));
    check('its source: the radio and its seeds', t.source && t.source.radio && t.source.radio.kind === 'track' && t.source.radio.seeds[0] === seed.id, t.source);
    check('one version of a song', !(t.ids.includes('m2') && t.ids.includes('m3')), show(t.ids));
    if (album.length >= 3) {
      let a = await h.rpc('radio.start', { kind: 'album', ids: album.map(x => x.id), name: 'Test Album' });
      info('album radio: ' + show(a.ids).join(', '));
      check('an album\'s radio, nothing played: its first track', a.ok && a.ids[0] === album[0].id, show(a.ids.slice(0, 1)));
      // one pick at a time: the radio going on, as MIKU asks while the last track plays
      const list = a.ids.slice(), asrc = { ...a.source, ext: 'moodchannels' };
      for (let k = 0; k < 12; k++) {
        const c = await cont(h, list, asrc, false);
        if (!c.length) break;
        check(`going on: one pick (${k + 1})`, c.length === 1, show(c));
        list.push(...c);
      }
      info('album radio on: ' + show(list).join(', '));
      const at = list.map((id, i) => album.some(x => x.id === id) ? i : -1).filter(i => i >= 0);
      check('its tracks at least 6 apart', at.every((v, k) => k === 0 || v - at[k - 1] >= 6), at);
      // a play of its last track, then the radio again: it starts there
      const last = album[album.length - 1];
      h.send({ op: 'played', id: last.id, seconds: 200, duration: 240, skipped: false, stopped: false, source: null });
      await sleep(800);
      a = await h.rpc('radio.start', { kind: 'album', ids: album.map(x => x.id), name: 'Test Album' });
      check('after a play: its most played track', a.ids[0] === last.id, show(a.ids.slice(0, 1)));
    } else info('no folder with 3 of the songs: the album checks are left out');
    const ar = await h.rpc('radio.start', { kind: 'artist', artist: 'Artist B', name: 'Artist B' });
    check('an artist\'s radio starts with one of theirs', ar.ok && byId[ar.ids[0]].artist === 'Artist B', ar.ids && show(ar.ids.slice(0, 1)));
    check('its seeds: their songs (not the broken file, not silence)', ar.source.radio.seeds.length > 0 &&
      ar.source.radio.seeds.every(id => byId[id].artist === 'Artist B' && !synthetic.has(id)), show(ar.source.radio.seeds));
    const none = await h.rpc('radio.start', { kind: 'track', ids: ['no-such-id'] });
    check('no seed: refused', none.ok === false && none.reason === 'seeds', none);
  });

  await group('continue: the Smart Playlist and a radio going on', async () => {
    if (!trackRadio) { info('no radio (--music): left out'); return; }
    const list = [songs[4].id, songs[5].id, songs[6].id];
    let c = await cont(h, list, null, false);
    check('a list nobody asked to go on with: nothing', c.length === 0, c);
    c = await cont(h, list, { channel: 13, ext: 'moodchannels' }, false);
    check('a channel goes on by itself (not chosen): one of it, not queued', c.length === 1 && clean(c) && !list.includes(c[0]), show(c));
    c = await cont(h, list, null, true);
    info('smart playlist: ' + show(c).join(', '));
    check('chosen (the Smart Playlist): one, new, only songs', c.length === 1 && clean(c) && !c.some(id => list.includes(id)), show(c));
    if (album.length >= 3) check('none heard lately (the play above)', !c.includes(album[album.length - 1].id), show(c));
    const src = { ...trackRadio.source, ext: 'moodchannels' };
    c = await cont(h, trackRadio.ids, src, false);
    info('radio on: ' + show(c).join(', '));
    check('a radio goes on by itself: new songs', c.length > 0 && clean(c) && !c.some(id => trackRadio.ids.includes(id)), show(c));
  });

  await group('radio votes', async () => {
    if (!trackRadio) { info('no radio (--music): left out'); return; }
    const src = { ...trackRadio.source, ext: 'moodchannels' };
    const id = trackRadio.ids[1];
    let v = await h.rpc('radio.vote', { id, source: src, vote: 1 });
    check('👍 in a radio', v.vote === 1 && v.known === true, v);
    const again = await h.rpc('radio.start', { kind: 'track', ids: [trackRadio.ids[0]] });
    v = await h.rpc('radio.vote.get', { id, source: { ...again.source, ext: 'moodchannels' } });
    check('kept for another radio from the same start', v.vote === 1, v);
    const other = await h.rpc('radio.start', { kind: 'track', ids: [songs[7].id] });
    v = await h.rpc('radio.vote.get', { id, source: { ...other.source, ext: 'moodchannels' } });
    check('not for a radio from elsewhere', v.vote === 0 && v.known === true, v);
    v = await h.rpc('radio.vote', { id, source: { smart: true, ext: 'moodchannels' }, vote: -1 });
    check('👎 in the Smart Playlist', v.vote === -1, v);
    v = await h.rpc('radio.vote.get', { id, source: src });
    check('the radio\'s vote stays its own', v.vote === 1, v);
    v = await h.rpc('radio.vote.get', { id, source: null });
    check('no radio playing: nothing to vote on', v.known === false, v);
    v = await h.rpc('radio.vote', { id, source: { smart: true }, vote: 0 });
    check('a vote taken back', v.vote === 0, v);
    // the queue drawer's rows: the votes of many tracks at once, and which of them the radio picked
    let m = await h.rpc('radio.vote.many', { ids: trackRadio.ids, source: src });
    check('radio.vote.many: each row\'s vote', m.known === true && m.votes[id] === 1 && m.votes[trackRadio.ids[0]] === 0, m);
    check('… and the radio\'s picks (not its own track)', m.picked.includes(trackRadio.ids[1]) && !m.picked.includes(trackRadio.ids[0]), m.picked);
    m = await h.rpc('radio.vote.many', { ids: trackRadio.ids, source: null });
    check('radio.vote.many, no radio: not known', m.known === false, m);
    m = await h.rpc('vote.many', { ids: songs.slice(0, 3).map(s => s.id), channel: 2 });
    check('vote.many in a channel', m.known === true && Object.values(m.votes).every(x => x === 0), m);
    m = await h.rpc('vote.many', { ids: [songs[0].id], channel: 99 });
    check('vote.many, no such channel: not known', m.known === false, m);
    // a pick queued next taken again after a vote: one new track, a pick too
    const n = await h.rpc('radio.next', { played: [trackRadio.ids[0]], source: src });
    check('radio.next: one pick', n.ids.length === 1 && clean(n.ids) && n.ids[0] !== trackRadio.ids[0], show(n.ids));
    m = await h.rpc('radio.vote.many', { ids: n.ids, source: src });
    check('… known as a pick', m.picked.includes(n.ids[0]), m.picked);
    // a radio started from a track a radio picked before: that track is the listener's own now, not a pick
    const again = await h.rpc('radio.start', { kind: 'track', ids: [n.ids[0]], name: 'again' });
    m = await h.rpc('radio.vote.many', { ids: again.ids, source: { ...again.source, ext: 'moodchannels' } });
    check('a radio from an earlier pick: its own track no longer a pick', again.ok && !m.picked.includes(n.ids[0]), m.picked);
    // a channel: one track of it at random, not one played (helper.test.js checks it is the channel's)
    const ch = await h.rpc('radio.next', { played: [trackRadio.ids[0]], source: { channel: 2, ext: 'moodchannels' } });
    check('radio.next for a channel: at most one, not one played', ch.ids.length <= 1 && !ch.ids.includes(trackRadio.ids[0]), ch);
  });

  await group('second start on the same data', async () => {
    await h.stop();
    h = new T.Helper(exe);
    h.send(start({ threads: 2, ai: true }));
    await h.waitFor(m => m.ready, 120000, 'ready');
    sendTracks(h);
    const s = await until(async () => { const s = await h.rpc('status'); return s.total === tracks.length && s.ai.total > 0 && s; }, 60000, 'the library counted');
    check('every embedding kept', s.ai.done === embedded && s.ai.pending === 0, { done: s.ai.done, pending: s.ai.pending });
    await sleep(3000);
    check('the model not copied again', !h.logs().some(l => /CLAP model .* ready/.test(l)), h.logs().filter(l => /CLAP/.test(l)));
    check('nothing embedded again (no process started)', !h.logs().some(l => /CLAP: process started/.test(l)));
    const r = await until(async () => { const r = await h.rpc('radio.ready'); return r.ready && r; }, 30000, 'radio ready');
    check('radio ready at once', r.count === embedded || r.count === embedded - 1, r);
    if (trackRadio) {
      const v = await h.rpc('radio.vote.get', { id: trackRadio.ids[1], source: { ...trackRadio.source, ext: 'moodchannels' } });
      check('radio votes kept', v.vote === 1, v);
    }
  });

  await group('forget and delete', async () => {
    await h.rpc('ai.pause');
    const f = await h.rpc('ai.forget');
    check('ai.forget: every embedding', f.forgotten === embedded, f);
    const s = await h.rpc('ai.delete');
    check('ai.delete: the AI off', s.ai.enabled === false, s.ai.enabled);
    check('the model gone', s.ai.download.ready === false && !fs.existsSync(path.join(dataDir, 'models', 'clap', 'clap_audio.onnx')), s.ai.download);
    check('"AI data deleted" logged', h.logs().some(l => /^AI data deleted/.test(l)));
    const r = await h.rpc('radio.ready');
    check('no radio without it', r.ready === false, r);
    const st = await h.rpc('status');
    check('the basic analysis untouched', st.analysed >= tracks.length - 2, st.analysed);
  });

  await group('stop', async () => {
    const r = await h.stop();
    check('exits by itself, code 0', r && r.code === 0, r);
    check('every line JSON', h.notJson.length === 0, h.notJson.slice(0, 3));
  });

  T.summary();
})();
