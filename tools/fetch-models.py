#!/usr/bin/env python3
"""
Downloads the model files listed in models/manifest.json (published as release assets, not committed) into models/,
each checked against its SHA-256; files already there and correct are kept. Python 3 alone (MIKU's Mac build needs it
anyway). MIKU's Mac packaging (miku/pack-mac.sh) runs it when models/basic/ is empty; the Windows module's MikuDeploy
does the same in MSBuild (src/FetchModels.targets).

    python3 tools/fetch-models.py [basic] [clap]      (basic by default: the module downloads clap itself when the
                                                       listener turns the advanced AI on)
"""
import hashlib
import json
import os
import sys
import urllib.request

MODELS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'models')


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def main(keys):
    with open(os.path.join(MODELS, 'manifest.json'), encoding='utf8') as f:
        manifest = json.load(f)
    for key in keys:
        m = manifest['models'].get(key)
        if m is None:
            raise SystemExit(f'fetch-models: no model "{key}" in models/manifest.json')
        if not m.get('url'):
            raise SystemExit(f'fetch-models: models/manifest.json: "{key}" has no download address yet')
        for spec in m['files']:
            out = os.path.join(MODELS, *spec['path'].split('/'))
            if os.path.isfile(out) and os.path.getsize(out) == spec['bytes'] and sha256(out) == spec['sha256']:
                print(f'ok       {spec["path"]}')
                continue
            os.makedirs(os.path.dirname(out), exist_ok=True)
            url = m['url'].rstrip('/') + '/' + os.path.basename(spec['path'])
            part = out + '.part'
            req = urllib.request.Request(url, headers={'User-Agent': 'MoodWave'})
            with urllib.request.urlopen(req, timeout=60) as r, open(part, 'wb') as f:
                for block in iter(lambda: r.read(1 << 20), b''):
                    f.write(block)
            got = sha256(part)
            if os.path.getsize(part) != spec['bytes'] or got != spec['sha256']:
                os.remove(part)
                raise SystemExit(f'fetch-models: {spec["path"]}: SHA-256 {got}, expected {spec["sha256"]}')
            os.replace(part, out)
            print(f'fetched  {spec["path"]} ({spec["bytes"] / 1e6:.1f} MB)')


if __name__ == '__main__':
    main(sys.argv[1:] or ['basic'])
