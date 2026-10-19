'use strict';
// Two runs of helper.test.js (--out) on different systems, over the same files: the same loudness, peaks and channels.
//
//   node miku/test/compare.js results-a.json results-b.json
const fs = require('fs');
const T = require('./lib');
const { check, info } = T;

const [fa, fb] = process.argv.slice(2);
if (!fa || !fb) { console.error('usage: compare.js <results-a.json> <results-b.json>'); process.exit(2); }
const a = JSON.parse(fs.readFileSync(fa, 'utf8')), b = JSON.parse(fs.readFileSync(fb, 'utf8'));
T.title(`${a.platform} vs ${b.platform}`);
const names = Object.keys(a.files).filter(n => n in b.files);
const only = Object.keys(a.files).concat(Object.keys(b.files)).filter(n => !(n in a.files && n in b.files));
if (only.length) info('in one run only (not compared): ' + [...new Set(only)].join(', '));
check('files in both runs', names.length > 0, names.length);

const loud = [], peak = [], chan = [];
for (const n of names) {
  const x = a.files[n], y = b.files[n];
  const lx = x.loudness, ly = y.loudness;
  if ((lx == null) !== (ly == null) || (lx && ((lx[0] == null) !== (ly[0] == null)))) loud.push([n, lx, ly]);
  else if (lx && lx[0] != null) {
    if (Math.abs(lx[0] - ly[0]) > 0.05) loud.push([n, lx[0], ly[0]]);
    if (Math.abs(lx[1] - ly[1]) > 0.01 * Math.max(lx[1], ly[1])) peak.push([n, lx[1], ly[1]]);
  }
  if (JSON.stringify(x.channels || []) !== JSON.stringify(y.channels || [])) chan.push([n, x.channels, y.channels]);
}
check('loudness the same (±0.05 LU)', loud.length === 0, loud);
check('true peaks the same (±1 %)', peak.length === 0, peak);
check('channels the same', chan.length === 0, chan);
T.summary();
