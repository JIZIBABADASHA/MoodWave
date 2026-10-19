#!/usr/bin/env node
'use strict';
// Downloads the model files listed in models/manifest.json (published as release assets, not committed) into
// models/, each checked against its SHA-256; files already there and correct are kept. Node 18 or later, nothing
// else. MIKU's packaging runs it when models/basic/ is empty (MikuDeploy, miku/pack-mac.sh).
//
//   node tools/fetch-models.js [basic] [clap]      (basic by default: the module downloads clap itself when the
//                                                   listener turns the advanced AI on)
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const MODELS = path.resolve(__dirname, '..', 'models');
const manifest = JSON.parse(fs.readFileSync(path.join(MODELS, 'manifest.json'), 'utf8'));
const which = process.argv.slice(2).length ? process.argv.slice(2) : ['basic'];

const sha256 = file => new Promise((resolve, reject) => {
  const h = crypto.createHash('sha256');
  fs.createReadStream(file).on('data', d => h.update(d)).on('error', reject).on('end', () => resolve(h.digest('hex')));
});

async function fetchFile(url, out) {
  const r = await fetch(url, { redirect: 'follow' });
  if (!r.ok) throw new Error(`${url}: HTTP ${r.status}`);
  const part = out + '.part';
  const w = fs.createWriteStream(part);
  for await (const chunk of r.body) if (!w.write(chunk)) await new Promise(res => w.once('drain', res));
  await new Promise((res, rej) => w.end(err => err ? rej(err) : res()));
  return part;
}

(async () => {
  for (const key of which) {
    const m = manifest.models[key];
    if (!m) throw new Error(`no model "${key}" in models/manifest.json`);
    if (!m.url) throw new Error(`models/manifest.json: "${key}" has no download address yet`);
    for (const f of m.files) {
      const out = path.join(MODELS, ...f.path.split('/'));
      if (fs.existsSync(out) && fs.statSync(out).size === f.bytes && await sha256(out) === f.sha256) { console.log(`ok       ${f.path}`); continue; }
      fs.mkdirSync(path.dirname(out), { recursive: true });
      const part = await fetchFile(m.url.replace(/\/$/, '') + '/' + path.basename(f.path), out);
      const got = await sha256(part);
      if (fs.statSync(part).size !== f.bytes || got !== f.sha256) { fs.rmSync(part, { force: true }); throw new Error(`${f.path}: SHA-256 ${got}, expected ${f.sha256}`); }
      fs.renameSync(part, out);
      console.log(`fetched  ${f.path} (${(f.bytes / 1e6).toFixed(1)} MB)`);
    }
  }
})().catch(e => { console.error('fetch-models: ' + e.message); process.exit(1); });
