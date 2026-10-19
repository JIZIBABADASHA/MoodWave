#!/bin/bash
# The macOS module's tests, on a Mac with a MIKU checkout (docs/macos.md §7):
#   helper.test.js       moodwave-helper over its protocol
#   extension.test.js    miku/extension.js in a stand-in for MIKU's host
#   ai.test.js           the advanced AI in the helper: the model, embeddings, radio, Smart Playlist, radio votes
#   player.test.js       MIKU's queue with the module's continuation and plays (the checkout's own player.js)
#   replaygain.test.js   MIKU's ReplayGain with MoodWave's loudness (the checkout's own code)
#
#   miku/test/run.sh [--miku <MIKU checkout>] [--music <folder>] [--ffmpeg <ffmpeg>] [--work <dir>]
#
# Node: $NODE_BIN, `node` on the PATH, else an Electron run as Node (ELECTRON_RUN_AS_NODE). Results of the helper
# test go to <work>/results-<system>.json (compare.js compares two of them).
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MIKU="$HOME/daphne-test/miku"; MUSIC=""; FFMPEG=""; WORK="${TMPDIR:-/tmp}/moodwave-test"
while [ $# -gt 0 ]; do
  case "$1" in
    --miku) MIKU="$2"; shift 2 ;;
    --music) MUSIC="$2"; shift 2 ;;
    --ffmpeg) FFMPEG="$2"; shift 2 ;;
    --work) WORK="$2"; shift 2 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$WORK"

# Node: --node, the PATH, a Node next to the checkout (<checkout>/../tools/node-*/), else an Electron run as Node
N="${NODE_BIN:-}"
[ -n "$N" ] || N="$(command -v node || ls -d "$MIKU"/../tools/node-*/bin/node 2>/dev/null | tail -1)"
if [ -n "$N" ]; then NODE=("$N")
else
  E="$(ls -d "$MIKU"/mac/app/node_modules/electron/dist/Electron.app/Contents/MacOS/Electron "$MIKU"/../tools/electron*/Electron.app/Contents/MacOS/Electron 2>/dev/null | head -1)"
  [ -n "$E" ] || { echo "no node and no Electron near $MIKU: install Node or set NODE_BIN" >&2; exit 2; }
  export ELECTRON_RUN_AS_NODE=1; NODE=("$E")
fi
echo "node: ${NODE[*]} ($("${NODE[@]}" --version))"
COMMON=(--miku "$MIKU" --work "$WORK"); [ -n "$FFMPEG" ] && COMMON+=(--ffmpeg "$FFMPEG")
RESULTS="$WORK/results-$(uname -s | tr A-Z a-z)-$(uname -m).json"

fail=0
echo "═══ helper";      "${NODE[@]}" "$HERE/helper.test.js" "${COMMON[@]}" ${MUSIC:+--music "$MUSIC"} --out "$RESULTS" || fail=1
echo; echo "═══ extension"; "${NODE[@]}" "$HERE/extension.test.js" "${COMMON[@]}" || fail=1
echo; echo "═══ ai";        "${NODE[@]}" "$HERE/ai.test.js" "${COMMON[@]}" ${MUSIC:+--music "$MUSIC"} || fail=1
echo; echo "═══ player";    "${NODE[@]}" "$HERE/player.test.js" --miku "$MIKU" || fail=1
echo; echo "═══ replaygain"; "${NODE[@]}" "$HERE/replaygain.test.js" --miku "$MIKU" $([ -f "$RESULTS" ] && echo --results "$RESULTS") || fail=1
echo; [ $fail -eq 0 ] && echo "all test files passed" || echo "some checks failed (see above)"
exit $fail
