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
# when it is missing it is built here, with the .NET SDK on the PATH or, without one, the .NET 8 SDK downloaded into
# .tools/ (macOS 11 or later with the Xcode command line tools needs nothing else).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${1:?usage: miku/pack-mac.sh <out-dir> [arm64] [x86_64]}"; shift
ARCHS=("$@"); [ ${#ARCHS[@]} -gt 0 ] || ARCHS=(arm64 x86_64)
mkdir -p "$OUT"; OUT="$(cd "$OUT" && pwd)"
fail() { echo "moodwave: $1" >&2; exit 1; }

# the .NET 8 SDK for this Mac (builds both architectures), checked against Microsoft's SHA-512 (releases.json)
SDK_VER=8.0.426
dotnet_sdk() {
  command -v dotnet >/dev/null && return
  local host sha dir tgz
  case "$(uname -m)" in
    arm64) host=osx-arm64; sha=17cd408c92eb6ceedc06d41fa726c783b9be020c6ae5f3624ad3a0b2c26a78aa7d09f38338003163a72bb14d437731debf533c0451a5c6a05de3b1e37a3aa50a ;;
    *) host=osx-x64; sha=44f5356cd8c96a937540547df6b71995636afe35827bc380478543cc7b7b3ca089679f3bee0498db3f6399bd9ee6b5829f3b452bcfc85d3c899714712ea3c5e3 ;;
  esac
  dir="$ROOT/.tools/dotnet-sdk-$SDK_VER-$host"
  if [ ! -x "$dir/dotnet" ]; then
    echo "moodwave: the .NET SDK is not installed; downloading .NET SDK $SDK_VER into .tools/ (about 200 MB, once)"
    mkdir -p "$ROOT/.tools"; tgz="$dir.tar.gz"
    curl -fL -o "$tgz" "https://builds.dotnet.microsoft.com/dotnet/Sdk/$SDK_VER/dotnet-sdk-$SDK_VER-$host.tar.gz" ||
      fail ".NET SDK $SDK_VER could not be downloaded"
    echo "$sha  $tgz" | shasum -a 512 -c - >/dev/null || { rm -f "$tgz"; fail "dotnet-sdk-$SDK_VER-$host.tar.gz does not match its SHA-512"; }
    rm -rf "$dir"; mkdir -p "$dir"; tar xzf "$tgz" -C "$dir"; rm -f "$tgz"
    xattr -dr com.apple.quarantine "$dir" 2>/dev/null || true
  fi
  export PATH="$dir:$PATH" DOTNET_ROOT="$dir" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
}

# the channel models are release assets (models/manifest.json): fetched and checked, every time (files already there
# and checked are kept; a download cut short before has some files, not all)
python3 "$ROOT/tools/fetch-models.py" basic || fail "models/basic/ is not complete and could not be fetched (tools/fetch-models.py)"
for arch in "${ARCHS[@]}"; do
  case "$arch" in
    arm64) tag=arm64; rid=osx-arm64 ;;
    x86_64|x64) tag=x64; rid=osx-x64 ;;          # MIKU's installer names the Intel build x64
    *) fail "unknown architecture $arch (arm64 or x86_64)" ;;
  esac
  helper="$ROOT/miku/bin/$rid/moodwave-helper"
  if [ ! -x "$helper" ] || [ ! -x "${helper%/*}/moodwave-clap" ]; then
    dotnet_sdk
    bash "$ROOT/miku/build-helper.sh" "$rid" || fail "moodwave-helper ($rid) did not build (miku/build-helper.sh)"
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
