'use strict';
// Shared parts of the macOS module's tests (miku/test/*.test.js): options, checks and their report, test audio made
// with ffmpeg, and a driver for moodwave-helper's JSON-lines protocol (docs/macos.md §3.2).
const fs = require('fs');
const os = require('os');
const path = require('path');
const readline = require('readline');
const { spawn, execFileSync } = require('child_process');

const ROOT = path.resolve(__dirname, '..', '..');

/** --name value options (and --flag), with the defaults given. */
function options(defaults) {
  const o = { ...defaults };
  const a = process.argv.slice(2);
  for (let i = 0; i < a.length; i++) {
    if (!a[i].startsWith('--')) continue;
    const k = a[i].slice(2);
    o[k] = a[i + 1] && !a[i + 1].startsWith('--') ? a[++i] : true;
  }
  return o;
}

// ───────────── checks ─────────────
const results = [];
let section = '';

function title(name) {
  section = name;
  console.log(`\n── ${name}`);
}

/** One check: ok or not, with what was found. */
function check(name, ok, found) {
  results.push({ section, name, ok: !!ok, found });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${found !== undefined ? '  — ' + (typeof found === 'string' ? found : JSON.stringify(found)) : ''}`);
  return !!ok;
}

function info(text) { console.log(`  ·    ${text}`); }

/** Runs a group of checks; an exception counts as a failed check (and the group stops). */
async function group(name, fn) {
  title(name);
  try { await fn(); }
  catch (e) { check('finished without an error', false, e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : String(e)); }
}

/** Prints the totals and sets the exit code: 0 when every check passed. */
function summary(file) {
  const bad = results.filter(r => !r.ok);
  console.log(`\n${results.length - bad.length}/${results.length} checks passed${bad.length ? ', failed:' : ''}`);
  for (const r of bad) console.log(`  - [${r.section}] ${r.name}${r.found !== undefined ? ': ' + JSON.stringify(r.found) : ''}`);
  if (file) fs.writeFileSync(file, JSON.stringify(results, null, 1));
  process.exitCode = bad.length ? 1 : 0;
  return bad.length === 0;
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Waits until fn() gives something truthy (checked every `every` ms); throws after ms. */
async function until(fn, ms, what, every = 500) {
  const end = Date.now() + ms;
  for (;;) {
    const v = await fn();
    if (v) return v;
    if (Date.now() > end) throw new Error(`timed out after ${ms / 1000} s waiting for ${what}`);
    await sleep(every);
  }
}

// ───────────── ffmpeg and the test audio ─────────────
/** ffmpeg: --ffmpeg, $FFMPEG, the MIKU checkout's bin/ffmpeg, then the PATH. */
function findFfmpeg(o) {
  const exe = process.platform === 'win32' ? 'ffmpeg.exe' : 'ffmpeg';
  const cands = [o.ffmpeg, process.env.FFMPEG];
  if (o.miku) cands.push(path.join(o.miku, 'mac', 'bin', exe), path.join(o.miku, 'bin', exe));
  for (const c of cands) if (c && fs.existsSync(c)) return c;
  try {
    const w = execFileSync(process.platform === 'win32' ? 'where' : 'which', ['ffmpeg'], { encoding: 'utf8' }).split(/\r?\n/)[0].trim();
    if (w) return w;
  } catch { }
  throw new Error('ffmpeg not found: pass --ffmpeg <path>');
}

const SECONDS = 20;
// name → ffmpeg input filter (44.1 kHz stereo 16-bit WAV, SECONDS long unless the filter says otherwise)
const FIXTURES = {
  'noise-a.wav': `anoisesrc=color=pink:seed=11:amplitude=0.1:d=${SECONDS}`,
  'noise-b.wav': `anoisesrc=color=pink:seed=11:amplitude=0.1:d=${SECONDS},volume=2`,       // noise-a, 6.02 dB louder
  'silence.wav': `anullsrc=r=44100:cl=stereo:d=${SECONDS}`,
  'short.wav': `sine=f=440:d=3,volume=0.3`,                                                  // below MinSeconds: no channels
  'peaky.wav': `aevalsrc='0.01*sin(2*PI*440*t)+if(between(t\\,10\\,10.003)\\,0.95\\,0)':s=44100:d=${SECONDS}`,  // quiet, one loud click
  'f1-white.wav': `anoisesrc=color=white:seed=1:amplitude=0.05:d=${SECONDS}`,
  'f2-brown.wav': `anoisesrc=color=brown:seed=2:amplitude=0.3:d=${SECONDS}`,
  'f3-blue.wav': `anoisesrc=color=blue:seed=3:amplitude=0.05:d=${SECONDS}`,
  'f4-velvet.wav': `anoisesrc=color=velvet:seed=4:amplitude=0.2:d=${SECONDS}`,
  'f5-chord.wav': `aevalsrc='0.2*(sin(2*PI*261.6*t)+sin(2*PI*329.6*t)+sin(2*PI*392*t))*(0.6+0.4*sin(2*PI*2*t))':s=44100:d=${SECONDS}`,
  'f6-pulse.wav': `aevalsrc='0.5*sin(2*PI*55*t)*exp(-8*mod(t\\,0.5))+0.05*sin(2*PI*880*t)':s=44100:d=${SECONDS}`,
  'f7-sweep.wav': `aevalsrc='0.3*sin(2*PI*(100+400*t)*t)':s=44100:d=${SECONDS}`,
  'f8-beat.wav': `aevalsrc='0.6*sin(2*PI*60*t)*exp(-20*mod(t\\,0.4))+0.1*sin(2*PI*523*t)*lt(mod(t\\,0.8)\\,0.4)':s=44100:d=${SECONDS}`,
  // the album last in the track list: not read ahead before the priority check
  'album/01.wav': `aevalsrc='0.25*sin(2*PI*220*t)*(0.7+0.3*sin(2*PI*0.5*t))':s=44100:d=${SECONDS}`,
  'album/02.wav': `aevalsrc='0.25*sin(2*PI*247*t)+0.1*sin(2*PI*494*t)':s=44100:d=${SECONDS}`,
  'album/03.wav': `aevalsrc='0.25*sin(2*PI*196*t)*exp(-2*mod(t\\,1))':s=44100:d=${SECONDS}`,
};
const BROKEN = 'broken.flac';                                                               // not audio: analysis fails

/** Makes the test audio in dir (once; existing files are kept, so other runs analyse the very same files). */
function makeFixtures(dir, ffmpeg) {
  fs.mkdirSync(path.join(dir, 'album'), { recursive: true });
  for (const [name, filter] of Object.entries(FIXTURES)) {
    const out = path.join(dir, name);
    if (fs.existsSync(out)) continue;
    try {
      execFileSync(ffmpeg, ['-v', 'error', '-y', '-f', 'lavfi', '-i', filter, '-ac', '2', '-ar', '44100', '-c:a', 'pcm_s16le', '-bitexact', out],
        { stdio: ['ignore', 'ignore', 'pipe'] });
    } catch (e) { throw new Error(`ffmpeg could not make ${name}: ${String(e.stderr || e.message).trim().split('\n')[0]}`); }
  }
  const broken = path.join(dir, BROKEN);
  if (!fs.existsSync(broken)) {
    let x = 12345; const b = Buffer.alloc(200000);
    for (let i = 0; i < b.length; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; b[i] = x >> 16; }
    fs.writeFileSync(broken, b);
  }
  return [...Object.keys(FIXTURES).filter(n => !n.startsWith('album/')), BROKEN, ...Object.keys(FIXTURES).filter(n => n.startsWith('album/'))];
}

/** The library as MIKU would describe it: {id, name, path, album, duration, size, mtime}; music: real files (read only). */
function library(dir, names, music = []) {
  const test = names.map((name, i) => {
    const p = path.join(dir, name);
    const st = fs.statSync(p);
    return { id: 't' + (i + 1), name, path: p, album: name.startsWith('album/') ? 'Test Album' : 'Single ' + (i + 1),
      duration: name === 'short.wav' ? 3 : SECONDS, size: st.size, mtime: Math.floor(st.mtimeMs) };
  });
  return test.concat(music.map((p, i) => {
    const st = fs.statSync(p);
    return { id: 'm' + (i + 1), name: 'music/' + path.basename(p), path: p, album: path.basename(path.dirname(p)), music: true,
      duration: 240, size: st.size, mtime: Math.floor(st.mtimeMs) };
  }));
}

const AUDIO = /\.(flac|mp3|m4a|wav|aiff?|ogg|opus|ape|wv|dsf)$/i;

/**
 * Up to count real songs under dir (spread over its folders, the same ones each run): synthetic test audio is in
 * no channel, so channels, channel start and votes need music. The files are only read.
 */
function findMusic(dir, count) {
  if (!dir) return [];
  const found = [];
  const walk = (d, depth) => {
    let list;
    try { list = fs.readdirSync(d, { withFileTypes: true }); } catch { return; }
    list.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0);
    for (const e of list) {
      if (found.length >= count * 40) return;
      const p = path.join(d, e.name);
      if (e.isDirectory() && depth < 6 && !e.name.startsWith('.')) walk(p, depth + 1);
      else if (e.isFile() && AUDIO.test(e.name) && !e.name.startsWith('.')) found.push(p);
    }
  };
  walk(path.resolve(dir), 0);
  if (found.length <= count) return found;
  return Array.from({ length: count }, (_, k) => found[Math.floor(k * found.length / count)]);
}

// ───────────── the helper ─────────────
/** moodwave-helper for this system: --helper, $MOODWAVE_HELPER, miku/bin/<rid>/. */
function findHelper(o) {
  const rid = process.platform === 'win32' ? 'win-x64' : process.arch === 'arm64' ? 'osx-arm64' : 'osx-x64';
  const exe = process.platform === 'win32' ? 'moodwave-helper.exe' : 'moodwave-helper';
  for (const c of [o.helper, process.env.MOODWAVE_HELPER, path.join(ROOT, 'miku', 'bin', rid, exe)])
    if (c && fs.existsSync(c)) return c;
  throw new Error('moodwave-helper not found: run miku/build-helper.sh or pass --helper <path>');
}

/** One running helper, spoken to as miku/extension.js does; keeps every message it sent. */
class Helper {
  constructor(exe) {
    this.exe = exe;
    this.messages = [];
    this.notJson = [];
    this.stderr = '';
    this.loudness = new Map();                       // path → [lufs | null, peak], as extension.js keeps it
    this.loudAt = new Map();                         // path → index of the message that last brought it
    this.nextId = 1;
    this.waiting = new Map();
    this.exited = null;
    this.p = spawn(exe, [], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.p.stdin.on('error', () => { });
    this.p.stderr.on('data', d => { this.stderr += d; });
    this.exitedP = new Promise(r => this.p.on('exit', (code, signal) => { this.exited = { code, signal }; r(this.exited); }));
    readline.createInterface({ input: this.p.stdout }).on('line', line => this.onLine(line));
  }

  onLine(line) {
    let m;
    try { m = JSON.parse(line); } catch { this.notJson.push(line); return; }
    const i = this.messages.push(m) - 1;
    if (m.loudness) for (const [p, lufs, peak] of m.loudness) { this.loudness.set(p, [lufs, peak]); this.loudAt.set(p, i); }
    if (m.id != null && this.waiting.has(m.id)) { this.waiting.get(m.id)(m); this.waiting.delete(m.id); }
  }

  send(m) { this.p.stdin.write(JSON.stringify(m) + '\n'); }
  sendRaw(line) { this.p.stdin.write(line + '\n'); }

  /** The tracks message; mtimes written as given in bigMtimes (strings of digits: larger than a double holds exactly). */
  sendTracks(tracks, bigMtimes) {
    const list = tracks.map(t => ({ id: t.id, path: t.path, album: t.album, duration: t.duration, size: t.size,
      mtime: bigMtimes ? `@@${t.id}@@` : t.mtime }));
    let line = JSON.stringify({ op: 'tracks', tracks: list });
    if (bigMtimes) line = line.replace(/"@@(\w+)@@"/g, (_, id) => bigMtimes[id]);
    this.sendRaw(line);
  }

  rpc(method, args = {}, ms = 120000) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.waiting.delete(id); reject(new Error(`rpc ${method}: no answer in ${ms / 1000} s`)); }, ms);
      this.waiting.set(id, m => { clearTimeout(timer); m.error != null ? reject(new Error(`rpc ${method}: ${m.error}`)) : resolve(m.result); });
      this.send({ op: 'rpc', id, method, args });
    });
  }

  /** Waits for a message (from index `from` on) that pred accepts. */
  waitFor(pred, ms, what, from = 0) {
    return until(() => this.messages.slice(from).find(pred), ms, what, 100);
  }

  logs(from = 0) { return this.messages.slice(from).filter(m => m.log != null).map(m => m.log); }
  settings(from = 0) { return this.messages.slice(from).filter(m => m.setting != null); }

  async stop(ms = 15000) {
    this.send({ op: 'stop' });
    this.p.stdin.end();
    const r = await Promise.race([this.exitedP, sleep(ms).then(() => null)]);
    if (!r) { try { this.p.kill('SIGKILL'); } catch { } }
    return r;
  }
}

/** A fresh, empty folder under the work folder. */
function freshDir(parent, name) {
  const d = path.join(parent, name);
  fs.rmSync(d, { recursive: true, force: true });
  fs.mkdirSync(d, { recursive: true });
  return d;
}

function workDir(o) {
  const d = path.resolve(o.work || path.join(os.tmpdir(), 'moodwave-test'));
  fs.mkdirSync(d, { recursive: true });
  return d;
}

module.exports = { ROOT, options, title, check, info, group, summary, sleep, until, findFfmpeg, makeFixtures, library, findMusic,
  findHelper, Helper, freshDir, workDir, SECONDS, BROKEN };
