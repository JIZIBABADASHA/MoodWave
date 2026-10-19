#!/bin/bash
# Builds moodwave-helper (src/MoodChannels.Helper) and moodwave-clap (src/MoodChannels.ClapWorker, the advanced AI's
# CLAP process, which the helper starts) for the Mac into miku/bin/<rid>/: self-contained, one file each.
# Needs the .NET 8 SDK; works on Windows (Git Bash), macOS and Linux.
#
#   miku/build-helper.sh [osx-x64] [osx-arm64]     (both by default)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RIDS=("$@"); [ ${#RIDS[@]} -gt 0 ] || RIDS=(osx-x64 osx-arm64)
for rid in "${RIDS[@]}"; do
  dotnet publish "$ROOT/src/MoodChannels.Helper/MoodChannels.Helper.csproj" -c Release -r "$rid" -o "$ROOT/miku/bin/$rid" \
    --configfile "$ROOT/nuget.config" -nologo -v q
  dotnet publish "$ROOT/src/MoodChannels.ClapWorker/MoodChannels.ClapWorker.csproj" -c Release -r "$rid" -o "$ROOT/miku/bin/$rid" \
    --configfile "$ROOT/nuget.config" -nologo -v q
  # ONNX Runtime's package copies its Windows DLLs whatever the target; the Mac's library is inside moodwave-clap
  rm -f "$ROOT/miku/bin/$rid/"*.pdb "$ROOT/miku/bin/$rid/"*.dll
  echo "moodwave: built miku/bin/$rid/moodwave-helper, moodwave-clap"
done
