<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/logo-dark.svg">
    <img src="docs/logo-light.svg" width="160" height="160" alt="MoodWave">
  </picture>
</p>

<h1 align="center">MoodWave</h1>

<p align="center"><em>Your library, in the right mood.</em></p>

Code name MoodChannels (the projects and namespaces).

Sorts a local music library into **13 listening channels** — five times of day and eight moods — and estimates each
track's BPM and key. Everything runs locally from the audio itself: no online lookup, no metadata needed.

| Kind | Channels | Per track |
|---|---|---|
| Time of day | Morning, Daytime, Evening, Night, Midnight | exactly one |
| Mood | Energetic, Relax, Mellow, Upbeat, Emotional, Lounge, Dance, Extreme | usually one, sometimes two |
| — | unclassifiable (too short, silence, speech …) | no channel |

Two parts, each switched on and off on its own:

- **Basic analysis**: classic signal processing (no neural network) followed by small gradient-boosted classifiers,
  about 2 s of CPU time per track. Gives the channels, BPM, key and loudness (for MIKU's ReplayGain).
- **Advanced AI** (optional, off by default): CLAP audio embeddings (a 285 MB model, downloaded when it is turned on)
  for **Song Radio** (a track's, an album's or an artist's radio) and the **Smart Playlist** (when the queue runs
  out, MoodWave goes on with what fits what was actually heard). 👍 / 👎 on radio tracks steer later radios from
  the same start.

## How it works

```
audio file ─ ffmpeg ─> 16 kHz mono ─> DSP descriptors ─> channel models (trees) ─> channels, BPM, key
```

- **DSP descriptors** (`src/MoodChannels.Core/Dsp/`), all implemented here from the literature:
  spectral statistics, onset envelope, tempo (weighted autocorrelation) and dynamic-programming beat tracking,
  tempo-salience of the usual metrical confusions, detrended fluctuation analysis, key (Krumhansl–Kessler profiles
  on a peak chromagram), loudness (ITU-R BS.1770 K-weighting, loudness range, dynamic complexity).
- **Channel models** (`models/basic/`): gradient-boosted trees (XGBoost) — unclassifiable, time of day (5-way), one
  per mood, and a 7-way metrical-level correction for the BPM — stored as compact `.trees` files and evaluated by
  `TreeModel.cs` (no ONNX Runtime: loading takes a fraction of a second). `model.json` holds the feature list and
  the decision rules (see `ChannelModel.cs`).

## Build and run

Requires the .NET 8 SDK and `ffmpeg` on `PATH` (or its path in the `FFMPEG` environment variable).

```
dotnet build -c Release
src/MoodChannels.Cli/bin/Release/net8.0/moodchannels models/basic song.flac
src/MoodChannels.Cli/bin/Release/net8.0/moodchannels models/basic --workers 8 --list tracks.tsv   # "<id>\t<path>" lines
```

One JSON line per track: `channels`, `bpm`, `key`, per-channel `probabilities`, `model` (model version).

Unit tests (no ffmpeg, models or MIKU needed; CI runs them on Windows, macOS and Linux): `dotnet test tests/MoodChannels.Tests`.

## Library use

```csharp
using var analyzer = new MoodChannels.Analyzer("models/basic");   // thread-safe
var a = analyzer.Analyze(@"D:\Music\song.flac");
// a.Channels: native channel indices (MoodChannels.Channels.Names), a.Bpm, a.Key, a.ModelVersion
```

## MIKU module

Projects: `MoodChannels.Core` (the analysis), `MoodChannels.Service` (the module's logic: queue, store, votes, tuning,
loudness, the page's calls; reaches the player through `IMoodHost`), `MoodChannels.Extension` (MIKU on Windows),
`MoodChannels.Helper` + `miku/` (MIKU on macOS: a helper process and its `extension.js`, see `docs/macos.md`),
`MoodChannels.Clap` + `MoodChannels.ClapWorker` (the advanced AI: CLAP audio embeddings with ONNX Runtime, in a
low-priority process of their own, `moodwave-clap`, which the service starts when the advanced AI is on),
`MoodChannels.Cli`.

`src/MoodChannels.Extension/` is a module for the MIKU player (MIKU repo: `MIKU.Extensibility/README.md`): background
analysis of the library with a read-ahead queue for hard disks, a SQLite store
(`%LOCALAPPDATA%\MIKU\ext\moodchannels\library.db`: per file the descriptors and the channels they gave, written as
each track is done; a new channel model only needs the channels worked out again, in seconds), a page with the
13 channels (`web/`) and a settings block (on/off, threads, progress, failed tracks).

The advanced AI (in `MoodChannels.Service`): `ClapModel` downloads and checks the model (`models/manifest.json`,
SHA-256, resumable) into the data folder; `ClapQueue` embeds each analysed file in `moodwave-clap` (Windows:
DirectML on the GPU, else the CPU; macOS: the CPU), the track playing and the next ones first, the rest only while
the basic analysis is idle, and stops the process after a minute with nothing to do. Embeddings are kept in
`library.db` with the model's version. `Radio` scores the library against a radio's seeds (cosine similarity, less
for tracks played lately, skipped often or like a 👎 one), keeps an artist 3 and an album 6 tracks apart and one
version of a song in 20, then picks for a smooth tempo and key change. What was heard comes from MIKU
(`IExtensionHost.TrackPlayed`, the `plays` table); the Smart Playlist is MIKU's 「佇列播完後」 handed to the module
(`IQueueContinuation`).

On Windows the module needs a MIKU whose `MIKU.Extensibility` has those members (an older one does not load it). On
macOS `extension.js` also runs in an older MIKU, without plays (so radios start from their seeds only) and without
the Smart Playlist.

- In a MIKU checkout this repository sits at `Extensions/<name>/`, e.g.
  `git submodule add https://github.com/cychang-ykc/MoodWave.git Extensions/MoodWave`. MIKU's own builds then build
  and bundle the module; the model files are fetched from the releases by the first build:
  - Windows (MIKU's build / publish): needs only the .NET 8 SDK; the module lands in `ext\moodchannels\`.
  - macOS (`mac/build/build.sh`, on a Mac): runs `miku/pack-mac.sh`, which builds `moodwave-helper` and
    `moodwave-clap` for arm64 and x64 (about 4 minutes the first time) with the .NET 8 SDK on the PATH or, without
    one, the SDK it downloads into `.tools/`; macOS 11+ with the Xcode command line tools (`python3`) needs nothing else.
- While this folder is only *linked* there (a junction), MSBuild sees its real path: put the MIKU repository's path in
  `miku.local.props` (not committed) as `<MikuRoot>…\</MikuRoot>`.
- Deploy without rebuilding MIKU: `dotnet msbuild src/MoodChannels.Extension -t:MikuDeploy -p:Configuration=Release -p:MikuOutDir=<MIKU program folder>\`

## Model files

Model files are not committed; they are published as release assets (releases `basic-<version>` and
`clap-<version>`) and listed with their SHA-256 in `models/manifest.json`.

- Basic model: `python3 tools/fetch-models.py` downloads and checks it into `models/basic/`. MIKU's packaging does it
  by itself when `models/basic/` is empty (Windows: MikuDeploy, in MSBuild; macOS: `miku/pack-mac.sh`), and bundles
  the model with the module.
- Advanced AI's model (285 MB): the module downloads it itself, into its data folder, when the listener turns the
  advanced AI on (checked, resumed after an interruption). `MOODWAVE_MODEL_SOURCE` (a folder) or a model with no
  download address in the manifest makes it copy the file from there or from `models/clap/` of a checkout instead.

`models/basic/tuning.json` is committed: the built-in channel tuning, small logistic models of "this track does not
belong to the channel" on the standardised descriptors, learnt from a listener's 👍 / 👎 (weights only, no tracks).
The module uses it for channels with too few votes of the listener's own and as the starting point for those with
enough; it can be switched off in the settings. Export a new one from the module with the `tuning.export` RPC.

## Tests

- macOS: `miku/test/run.sh` on a Mac with a MIKU checkout (see `docs/macos.md` §7): the helper, `extension.js`, the
  advanced AI (model, embeddings, radio, Smart Playlist, radio votes), MIKU's queue with the continuation, and
  ReplayGain.

## Licence

MoodWave (the code, `web/`, the channel art and the basic channel models) is source-available under the
[PolyForm Noncommercial License 1.0.0](LICENSE): free to use, study, change and share for any noncommercial purpose,
including personal use. Commercial use needs a separate licence from the author (cychang-ykc). The MIKU player
project has an additional permission to ship MoodWave as part of MIKU (see the end of [LICENSE](LICENSE)).

Contributions: pull requests are not accepted for now (the author keeps the right to license the project in other
ways); issues and suggestions are welcome.

Third-party components keep their own licences:

| Component | Used for | Licence |
|---|---|---|
| LAION-CLAP music checkpoint (audio encoder, exported to ONNX) | the advanced AI's model (downloaded, not in the repository) | CC0-1.0 |
| ONNX Runtime 1.19.2 (`Microsoft.ML.OnnxRuntime`) | running the CLAP model | MIT |
| DirectML 1.15.1 (`Microsoft.AI.DirectML`, Windows) | the CLAP model on the GPU | Microsoft DirectML license (redistributable) |
| Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite | `library.db` | MIT, Apache-2.0, public domain |
| FFmpeg | decoding audio (run as a separate program, not included) | LGPL / GPL (the build used) |
