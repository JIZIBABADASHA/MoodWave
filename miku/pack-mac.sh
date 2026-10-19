#!/bin/bash
# Packs MoodWave for MIKU's Mac installer: <out>/<arm64|x64>/moodchannels/ holds what MIKU's extension loader reads
# from MIKU.app/Contents/Resources/ext/moodchannels/ (mac/app/main/extensions.js):
#   extension.js                the shell (passes messages between MIKU and the helper)
#   web/                        the page parts, the same files as the Windows module
#   models/basic/               the channel models and the built-in tuning
#   bin/moodwave-helper         the work (src/MoodChannels.Helper, .NET 8, self-contained; MIKU's installer signs
#                               every file in bin/)
#   bin/moodwave-clap           the advanced AI's CLAP process, started by the helper (src/MoodChannels.ClapWorker)
# MIKU's mac/build/build.sh runs Extensions/<Name>/miku/pack-mac.sh for every module that has one.
#
#   miku/pack-mac.sh <out-dir> [arm64] [x86_64]     (both architectures by default)
#
# The helper is built beforehand with miku/build-helper.sh (on any system with the .NET 8 SDK, Windows included);
# when it is missing and the .NET SDK is on this Mac, it is built here.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?usage: miku/pack-mac.sh <out-dir> [arm64] [x86_64]}"; shift
ARCHS=("$@"); [ ${#ARCHS[@]} -gt 0 ] || ARCHS=(arm64 x86_64)
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
fail() { echo "moodwave: $1" >&2; exit 1; }

# the channel models are release assets (models/manifest.json): fetched and checked when models/basic/ has none
if ! ls "$ROOT"/models/basic/*.trees >/dev/null 2>&1; then
  python3 "$ROOT/tools/fetch-models.py" basic || fail "models/basic/ has no model files and they could not be fetched (tools/fetch-models.py)"
fi
for arch in "${ARCHS[@]}"; do
  case "$arch" in
    arm64) tag=arm64; rid=osx-arm64 ;;
    x86_64|x64) tag=x64; rid=osx-x64 ;;          # MIKU's installer names the Intel build x64
    *) fail "unknown architecture $arch (arm64 or x86_64)" ;;
  esac
  helper="$ROOT/miku/bin/$rid/moodwave-helper"
  if [ ! -x "$helper" ] || [ ! -x "${helper%/*}/moodwave-clap" ]; then
    command -v dotnet >/dev/null || fail "miku/bin/$rid/moodwave-helper is missing: run miku/build-helper.sh (needs the .NET 8 SDK)"
    bash "$ROOT/miku/build-helper.sh" "$rid"
  fi
  D="$OUT/$tag/moodchannels"
  rm -rf "$D"; mkdir -p "$D/bin" "$D/models/basic"
  cp "$ROOT/miku/extension.js" "$D/"
  cp "$ROOT/LICENSE" "$D/"                     # the licence and its notices go with the module
  cp -R "$ROOT/web" "$D/web"                    # files, not the checkout's miku/web link
  cp "$ROOT"/models/basic/* "$D/models/basic/"
  cp "$ROOT/models/manifest.json" "$D/models/"   # the advanced AI's model is downloaded by it
  cp "$helper" "$D/bin/moodwave-helper"; chmod 755 "$D/bin/moodwave-helper"
  cp "${helper%/*}/moodwave-clap" "$D/bin/moodwave-clap"; chmod 755 "$D/bin/moodwave-clap"
  echo "moodwave: packed $tag ($(du -sh "$D" | cut -f1))"
done
