'use strict';
// moodwave-helper over its protocol, as miku/extension.js drives it: start-up, what is about to play first (also
// while paused), analysis and its failures, loudness and its push, channels, channel start, votes, settings, the
// built-in tuning, a library change, stop, and a second start on the same data (nothing analysed again, votes kept,
// a changed file analysed again).
//
//   node miku/test/helper.test.js [--music <folder>] [--helper <moodwave-helper>] [--ffmpeg <ffmpeg>] [--work <dir>]
//                                 [--out results.json]
//
// --music: a few real songs from that folder (only read) join the test files; synthetic audio is in no channel, so
// without them the checks of channel start are left out.
// Results (loudness and channels of each file) go to --out, for compare.js against another system's run.
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
const music = T.findMusic(o.music, Number(o['music-count'] || 8));
const tracks = T.library(audio, names, music);
const byName = Object.fromEntries(tracks.map(t => [t.name, t]));
const album = tracks.filter(t => t.name.startsWith('album/'));
const dataDir = T.freshDir(work, 'helper-data');
// mtimes as a Windows library has them (.NET ticks, ~6e17): a double would lose the last digits
const big = Object.fromEntries(tracks.map((t, i) => [t.id, (638650000000000000n + BigInt(i) * 1234567n + 1n).toString()]));
const start = settings => ({ op: 'start', dataDir, moduleDir: T.ROOT, ffmpeg, settings });

console.log(`helper  ${exe}\nffmpeg  ${ffmpeg}\nwork    ${work}\n${os.platform()} ${os.arch()}, ${os.cpus().length} cpus, ${names.length} test files + ${music.length} songs`);

const out = { platform: `${os.platform()}-${os.arch()}`, helper: exe, files: {} };
let first = null;       // what the first run ended with, for the second
let failedSongs = [];   // real songs that could not be analysed (reported; not the module's failure)
let vote = null;        // the track and channel voted on

(async () => {
  const h = new T.Helper(exe);

  await group('start-up', async () => {
    h.send(start({ threads: 1 }));
    await h.waitFor(m => m.ready, 120000, 'ready');
    const logs = h.logs();
    check('model loaded', logs.some(l => /^model /.test(l)), logs.find(l => /^model /.test(l)));
    check('store opened', logs.some(l => /^library\.db: 0 files/.test(l)), logs.find(l => /^library\.db/.test(l)));
    check('built-in tuning read', logs.some(l => /^built-in tuning: /.test(l)), logs.find(l => /built-in tuning/.test(l)));
    // paused before the library comes: nothing is analysed but what is asked for below
    const p = await h.rpc('pause');
    check('paused', p.paused === true, p.paused);
    h.sendTracks(tracks, big);
    const s = await until(async () => { const s = await h.rpc('status'); return s.total === tracks.length && s; }, 20000, 'the library counted');
    check('status: engine and every file counted', !!s.engine, { engine: s.engine, total: s.total });
    check('threads from the start settings', s.threads === 1, s.threads);
    await sleep(2000);
    const s2 = await h.rpc('status');
    check('paused: nothing analysed yet', s2.pending === tracks.length && s2.analysed === 0 && h.loudness.size === 0, { pending: s2.pending, analysed: s2.analysed });
    const tu = await h.rpc('tuning');
    check('tuning: built-in on, Relax tuned', tu.builtin === true && tu.channels.includes('Relax'), tu);
  });

  await group('what is about to play goes first, also while paused', async () => {
    const from = h.messages.length;
    // the album's first track plays, noise-a next: those two and the rest of the album (MoodService.AlbumAhead)
    const first4 = [...album, byName['noise-a.wav']];
    h.send({ op: 'upcoming', ids: [album[0].id, byName['noise-a.wav'].id] });
    const put = await h.waitFor(m => m.log && /files about to play put first/.test(m.log), 10000, 'the "put first" log', from);
    check('"4 files … put first" logged', /^4 files/.test(put.log), put.log);
    await until(() => first4.every(t => h.loudness.has(t.path)), 180000, 'them measured while paused');
    check('they are analysed and measured while paused', true, first4.map(t => t.name));
    await sleep(1500);
    const others = [...h.loudness.keys()].filter(p => !first4.some(t => t.path === p));
    check('nothing else is', others.length === 0, others.map(p => path.basename(p)));
    const s = await h.rpc('status');
    check('status while paused', s.paused && s.analysed === 4 && s.pending === tracks.length - 4, { analysed: s.analysed, pending: s.pending });
    // the same list again: nothing to move
    const n = h.messages.length;
    h.send({ op: 'upcoming', ids: [album[0].id, byName['noise-a.wav'].id] });
    await sleep(1500);
    check('the same list again moves nothing', !h.logs(n).some(l => /put first/.test(l)));
  });

  await group('analysis', async () => {
    await h.rpc('resume');
    const t0 = Date.now();
    const s = await until(async () => { const s = await h.rpc('status'); return s.pending === 0 && s.running === 0 && s.loudness.pending === 0 && s; },
      20 * 60000, 'the analysis to finish', 2000);
    await sleep(1500);                                     // loudness goes out in batches, once a second
    info(`finished ${((Date.now() - t0) / 1000).toFixed(0)} s after resume (1 thread)`);
    check('status: analysed + failed = all', s.analysed + s.failed === tracks.length, { analysed: s.analysed, failed: s.failed, total: s.total });
    check('progress events posted', h.messages.some(m => m.ev === 'progress'));
    const failed = await h.rpc('failed');
    const broken = failed.find(f => f.path === byName[T.BROKEN].path);
    check('the broken file failed, with the reason in words', broken && /^FFmpeg 讀不到音訊/.test(broken.reason), broken && broken.reason);
    const others = failed.filter(f => f !== broken);
    failedSongs = others.filter(f => music.includes(f.path));
    for (const f of failedSongs) info(`song not analysed: ${path.basename(f.path)}: ${f.reason}`);
    check('no test file failed but the broken one', others.length === failedSongs.length, others.map(f => `${path.basename(f.path)}: ${f.reason}`));
  });

  await group('loudness', async () => {
    const L = name => h.loudness.get(byName[name].path);
    const measurable = tracks.filter(t => t.name !== T.BROKEN && t.name !== 'silence.wav' && !failedSongs.some(f => f.path === t.path));
    const missing = measurable.filter(t => !h.loudness.has(t.path) || typeof h.loudness.get(t.path)[0] !== 'number');
    check('every measurable file pushed with a loudness', missing.length === 0, missing.map(t => t.name));
    check('silence pushed as silent (null)', h.loudness.has(byName['silence.wav'].path) && L('silence.wav')[0] === null, L('silence.wav'));
    check('the broken file has none', !h.loudness.has(byName[T.BROKEN].path));
    const [a, pa] = L('noise-a.wav'), [b, pb] = L('noise-b.wav');
    check('twice the level reads 6.02 LU louder', Math.abs(b - a - 6.0206) < 0.1, { a, b, diff: +(b - a).toFixed(3) });
    check('and a true peak twice as high', Math.abs(pb / pa - 2) < 0.04, { pa: +pa.toFixed(5), pb: +pb.toFixed(5) });
    const bad = measurable.filter(t => { const [l, p] = h.loudness.get(t.path) || []; return !(l > -70 && l < 0 && p > 0 && p < 2); });
    check('loudness and peaks in range', bad.length === 0, bad.map(t => [t.name, h.loudness.get(t.path)]));
    const [pk, ppk] = L('peaky.wav');
    // quiet with one loud click: the gain to -18 LUFS would push the click past full scale, so ReplayGain stops at the peak
    check('peaky: the gain to -18 LUFS is more than its peak allows', -18 - pk > -20 * Math.log10(ppk) + 3,
      { lufs: pk, peak: +ppk.toFixed(3), wanted: +(-18 - pk).toFixed(2), allowed: +(-20 * Math.log10(ppk)).toFixed(2) });
    const s = await h.rpc('status');
    check('status: loudness measured for the analysed files', s.loudness.measured >= measurable.length && s.loudness.pending === 0, s.loudness);
    for (const p of music) info(`${path.basename(p)}: ${JSON.stringify(h.loudness.get(p))}`);
    for (const t of tracks) out.files[t.name] = { loudness: h.loudness.get(t.path) || null };
  });

  await group('channels and channel start', async () => {
    const c = await h.rpc('channels');
    const all = c.channels[c.channels.length - 1];
    const analysed = tracks.length - 1 - failedSongs.length;
    check('totals', c.total === tracks.length && c.analysed === analysed, { total: c.total, analysed: c.analysed });
    check('last list is All', all.name === 'All' && all.index === c.channels.length - 1, all.name);
    const inAll = new Set(all.ids);
    check('the short file has no channel', !inAll.has(byName['short.wav'].id));
    const stray = c.channels.slice(0, -1).flatMap(ch => ch.ids.filter(id => !inAll.has(id)));
    check('every channel\'s tracks are in All', stray.length === 0, stray);
    check('counts match the lists', c.channels.every(ch => ch.count === ch.ids.length));
    info('channels: ' + (c.channels.filter(ch => ch.count).map(ch => `${ch.name} ${ch.count}`).join(', ') || 'none'));
    const synthetic = tracks.filter(t => !t.music && inAll.has(t.id)).map(t => t.name);
    if (synthetic.length) info('test files with channels: ' + synthetic.join(', '));
    const sampled = await h.rpc('channels', { sample: 2 });
    check('sample: at most 2 ids each, same counts', sampled.channels.every((ch, i) => ch.ids.length <= 2 && ch.count === c.channels[i].count));
    for (const t of tracks) out.files[t.name].channels = c.channels.slice(0, -1).filter(ch => ch.ids.includes(t.id)).map(ch => ch.name);
    first = { channels: c.channels.map(ch => ch.count), analysed };

    if (all.count === 0) {
      check('channel start', !music.length, music.length ? 'no song got a channel' : 'left out: no music given (--music)');
    } else {
      const pathOf = Object.fromEntries(tracks.map(t => [t.id, t.path]));
      let good = 0;
      for (let i = 0; i < 6; i++) {
        const st = await h.rpc('start', { channel: all.index });
        if (st.ids.length === Math.min(2, all.count) && st.count === all.count && st.start === 0 && st.at === 0 && new Set(st.ids).size === st.ids.length &&
            st.ids.every(id => inAll.has(id)) && typeof (h.loudness.get(pathOf[st.ids[0]]) || [])[0] === 'number') good++;
      }
      check('start: a track with measured loudness and the next one, one at a time (6 tries)', good === 6, good);
      const one = c.channels.slice(0, -1).find(ch => ch.count > 0);
      const st = await h.rpc('start', { channel: one.index });
      check(`start of ${one.name}: two of its tracks`, st.ids.length === Math.min(2, one.count) && st.ids.every(id => one.ids.includes(id)), st.ids);
      // the channel going on (as MIKU's Continue, or the page after a vote): one more of it, not one already queued
      const src = { channel: all.index, ext: 'moodchannels' };
      const n = await h.rpc('radio.next', { played: st.ids, source: src });
      check('next of a channel: one of it, not queued yet', n.ids.length === 1 && inAll.has(n.ids[0]) && (all.count <= 2 || !st.ids.includes(n.ids[0])), n.ids);
      const p = await h.rpc('vote.many', { ids: n.ids, channel: one.index });
      check('… known as a pick', p.known === true && p.picked.includes(n.ids[0]), p);
    }
    const none = await h.rpc('start', { channel: 999 });
    check('start of an unknown channel: nothing', none.start === -1 && none.ids.length === 0, none);
    // a track in one of its channels, else any analysed track in the first channel (a vote needs no membership)
    const used = c.channels.slice(0, -1).find(ch => ch.count > 0);
    vote = used ? { id: used.ids[0], channel: used.index, name: used.name } : { id: album[0].id, channel: 0, name: c.channels[0].name };
  });

  await group('votes', async () => {
    const { id, channel } = vote;
    let r = await h.rpc('vote.get', { id, channel });
    check('no vote yet, track known', r.vote === 0 && r.known === true && !!r.channel, r);
    const from = h.messages.length;
    r = await h.rpc('vote', { id, channel, vote: 1 });
    check('👍 stored', r.vote === 1, r);
    check('vote logged', h.logs(from).some(l => l.startsWith(`vote +1 on ${vote.name}`)), h.logs(from).find(l => l.startsWith('vote')));
    check('vote.get gives it back', (await h.rpc('vote.get', { id, channel })).vote === 1);
    check('👎 replaces it', (await h.rpc('vote', { id, channel, vote: -1 })).vote === -1);
    // the channel's next pick comes from the channel as the vote leaves it: never the 👎'd track
    const picks = [];
    for (let k = 0; k < 20; k++) picks.push(...(await h.rpc('radio.next', { played: [], source: { channel, ext: 'moodchannels' } })).ids);
    check('👎: not the channel\'s next pick (20 tries)', !picks.includes(id), picks.length);
    check('0 clears it', (await h.rpc('vote', { id, channel, vote: 0 })).vote === 0);
    check('unknown track: not known', (await h.rpc('vote', { id: 'nope', channel, vote: 1 })).known === false);
    check('bad channel: not known', (await h.rpc('vote.get', { id, channel: 99 })).known === false);
    await h.rpc('vote', { id, channel, vote: 1 });   // kept for the second start
  });

  await group('settings', async () => {
    let from = h.messages.length;
    let s = await h.rpc('set', { threads: 2 });
    check('threads → 2', s.threads === Math.min(2, os.cpus().length), s.threads);
    const sent = h.settings(from);
    check('settings sent to MIKU to save', sent.some(m => m.setting === 'threads' && m.value === s.threads) && sent.some(m => m.setting === 'enabled' && m.value === true), sent);
    s = await h.rpc('set', { threads: 999 });
    check('threads held to the processors', s.threads === os.cpus().length, s.threads);
    s = await h.rpc('set', { enabled: false });
    check('analysis off', s.enabled === false, s.enabled);
    s = await h.rpc('set', { enabled: true, threads: 1 });
    check('analysis on again', s.enabled === true && s.threads === 1, { enabled: s.enabled, threads: s.threads });
    from = h.messages.length;
    await h.rpc('set', { builtin: false });
    check('built-in tuning off: saved', h.settings(from).some(m => m.setting === 'builtin' && m.value === false));
    check('tuning shows it off', (await h.rpc('tuning')).builtin === false);
    await h.rpc('set', { builtin: true });
    check('and on again', (await h.rpc('tuning')).builtin === true);
    const ex = await h.rpc('tuning.export');
    check('tuning.export writes tuning.json', ex.ok === true && fs.existsSync(ex.file) && path.dirname(ex.file) === dataDir, { ok: ex.ok, channels: ex.channels });
    check('unknown call: null', (await h.rpc('nonsense')) === null);
    h.sendRaw('this is not json');
    check('a bad line is skipped', (await h.rpc('status')).total === tracks.length);
  });

  await group('failed files again, library change', async () => {
    const r = await h.rpc('reanalyze');
    check('reanalyze forgets the failed files', r.forgotten === 1 + failedSongs.length, r);
    await until(async () => { const s = await h.rpc('status'); return s.pending === 0 && s.running === 0 && s.failed === 1 + failedSongs.length; },
      300000, 'them to fail again', 1000);
    check('analysed again and failed again', true);
    const fewer = tracks.filter(t => t.name !== 'f8-beat.wav');
    h.sendTracks(fewer, big);
    await until(async () => (await h.rpc('status')).total === fewer.length, 20000, 'the smaller library', 1000);
    check('a file left the library: total follows', true, fewer.length);
    check('its channels follow', (await h.rpc('channels')).total === fewer.length);
    h.sendTracks(tracks, big);
    await until(async () => (await h.rpc('status')).total === tracks.length, 20000, 'the library back', 1000);
    const s = await h.rpc('status');
    check('back: nothing to analyse (it was kept)', s.pending === 0, s.pending);
  });

  await group('stop', async () => {
    const r = await h.stop();
    check('exits by itself, code 0', r && r.code === 0, r);
    check('stdout carried only JSON', h.notJson.length === 0, h.notJson.slice(0, 3));
    if (h.stderr.trim()) info('stderr: ' + h.stderr.trim().slice(0, 300));
  });

  // ── the same data again: MIKU restarted
  const h2 = new T.Helper(exe);
  await group('second start on the same data', async () => {
    h2.send(start({ threads: 1 }));
    h2.sendTracks(tracks, big);
    await h2.waitFor(m => m.ready, 120000, 'ready');
    const readyAt = h2.messages.findIndex(m => m.ready);
    check('store has every file and the vote', h2.logs().some(l => l.startsWith(`library.db: ${tracks.length} files, 1 votes`)), h2.logs().find(l => l.startsWith('library.db')));
    const early = tracks.filter(t => h2.loudAt.has(t.path) && h2.loudAt.get(t.path) < readyAt);
    check('all loudness pushed before ready', early.length === first.analysed, `${early.length} of ${first.analysed}`);
    const s = await h2.rpc('status');
    check('nothing analysed again (mtimes read exactly)', s.pending === 0 && s.analysed === first.analysed, { pending: s.pending, analysed: s.analysed });
    const c = await h2.rpc('channels');
    check('same channel counts', JSON.stringify(c.channels.map(ch => ch.count)) === JSON.stringify(first.channels));
    const v = await h2.rpc('vote.get', { id: vote.id, channel: vote.channel });
    check('the vote kept', v.vote === 1, v);
    // a file changed (new mtime): analysed again
    const n = h2.messages.length;
    const changed = byName['noise-a.wav'];
    h2.sendTracks(tracks, { ...big, [changed.id]: (BigInt(big[changed.id]) + 10000000n).toString() });
    await until(() => h2.loudAt.get(changed.path) >= n, 120000, 'the changed file measured again');
    const s2 = await until(async () => { const s = await h2.rpc('status'); return s.pending === 0 && s.running === 0 && s; }, 60000, 'quiet', 1000);
    check('a changed file is analysed again, only it', s2.analysed === first.analysed, s2.analysed);
    check('its loudness the same', Math.abs(h2.loudness.get(changed.path)[0] - out.files['noise-a.wav'].loudness[0]) < 1e-9);
    const r = await h2.stop();
    check('exits, code 0', r && r.code === 0, r);
  });

  if (o.out) { fs.writeFileSync(o.out, JSON.stringify(out, null, 1)); info('results → ' + o.out); }
  T.summary(o.report);
  process.exit();
})().catch(e => { console.error(e); process.exit(2); });
