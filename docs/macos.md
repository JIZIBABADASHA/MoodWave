# MoodWave on macOS — port plan

Status: implemented and tested on the test Mac (2026-10-19); see §6.

## 1. Goal

The same MoodWave as on Windows inside the macOS MIKU (Electron): the channel page, the player bar band, the
now-playing wheel, 👍 / 👎 with personalisation and the built-in tuning, background analysis, loudness measuring and
ReplayGain from it ("track", "album", "source" = 「在播放 MoodWave 時開啟」), what is about to play measured first.
Same results as on Windows for the same file: one code base for the analysis, not a second implementation.

## 2. How the two MIKUs load modules

| | Windows MIKU | macOS MIKU |
|---|---|---|
| Host | C# (WinForms + WebView2) | Electron (Node.js main process) |
| Module | `ext\<id>\*.Extension.dll`, `IMikuExtension` | `<repo>/Extensions/<name>/miku/extension.js` (checkout) or `MIKU.app/Contents/Resources/ext/<id>/` (installed), CommonJS: `{ id, start(host), stop(), handleRpc(method, args) }` |
| Page parts | `web/manifest.json` | the same `web/manifest.json` |

The macOS host cannot load a .NET DLL. Daphne solved the same problem with a helper process: a thin `extension.js`
that starts a native program and talks JSON to it. MoodWave does the same.

## 3. Architecture

```
            Windows                                         macOS
 ┌──────────────────────────────┐        ┌──────────────────────────────────────────────┐
 │ MIKU.exe                     │        │ MIKU (Electron main)                          │
 │  └ MoodChannels.Extension.dll│        │  └ miku/extension.js ── stdin/stdout JSON ──┐ │
 │      (IMikuExtension adapter)│        │                                             │ │
 └──────────────┬───────────────┘        └─────────────────────────────────────────────┼─┘
                │                                                                      │
                ▼                                            moodwave-helper (.NET 8, self-contained)
       MoodChannels.Service  ◄─────────────── the same library ──────────────►  MoodChannels.Service
       (queue, store, votes, tuning,                                             (unchanged)
        loudness, RPCs; IMoodHost)
                │
                ▼
       MoodChannels.Core (DSP, tree models, loudness via ffmpeg)
```

### 3.1 Projects (MoodWave repo)

| Project | What | Change |
|---|---|---|
| `MoodChannels.Core` | analysis | none |
| `MoodChannels.Service` (new, class library) | today's `MoodChannelsExtension`, `AnalysisQueue`, `AnalysisStore`, `Personalization`, moved out of the extension; talks to MIKU only through `IMoodHost` | moved, no behaviour change |
| `MoodChannels.Extension` | Windows: `IMikuExtension` + `ILoudnessProvider` → `IMoodHost` adapter | becomes thin |
| `MoodChannels.Helper` (new, console) | macOS: JSON-lines adapter → `IMoodHost`; published self-contained single file for `osx-x64` and `osx-arm64` | new |
| `miku/` (new) | macOS module: `extension.js`, `helper-client.js`, `web` → the shared `web/` | new |

`IMoodHost` (what the service needs from MIKU): data dir, module dir, ffmpeg path, library tracks, `Upcoming(n)`,
get/set setting, log, post event to the page, events LibraryChanged / PlaybackChanged.

### 3.2 Protocol (helper ⇄ extension.js), one JSON object per line

| Direction | Message | Purpose |
|---|---|---|
| JS → helper | `{"op":"start", dataDir, moduleDir, ffmpeg, settings}` | start-up |
| JS → helper | `{"op":"tracks", tracks:[{id,path,title,artist,album,albumArtist,genre,duration,sampleRate,bits,channels,codec,size,mtime}]}` | library snapshot, at start and after a change |
| JS → helper | `{"op":"upcoming", ids:[…]}` | on track change (replaces `Upcoming(n)` polling) |
| JS → helper | `{"id":n, "op":"rpc", method, args}` | the page's `ext.moodchannels.*` calls |
| helper → JS | `{"id":n, result}` / `{"id":n, error}` | RPC answers |
| helper → JS | `{"ev":name, data}` | events for the page (`progress` …) |
| helper → JS | `{"setting":key, value}` / `{"log":text}` | settings and log through MIKU |
| helper → JS | `{"loudness":[[path,lufs,peak],…]}` | measured loudness, in batches |

Loudness is pushed, not asked for: MIKU decides ReplayGain synchronously as a track loads, so `extension.js` keeps a
`Map` (path → lufs, peak; about 26k entries at most) and answers from it at once.

### 3.3 MIKU macOS changes (MIKU repo, `mac/app/main`)

| File | Change |
|---|---|
| `main.js` (host object of `loadExtensions`) | `upcoming(n)`, `onLibraryChanged(fn)`, `onPlaybackChanged(fn)`; `init.loudness` (the provider's name) |
| `web-audio.js` `rgDb` (also used by the native path and by Daphne through `host.replayGainDb`) | the Windows rule: measured loudness first for "track" / "source", album loudness from the tracks for "album", "source" only while the queue's source is the module; tags otherwise |
| `extensions.js` | `loudness()`: the first module with `loudness.lookup(path)` |
| `wwwroot` (synced from Windows by `mac/build/sync_wwwroot.py`) | `settings.js` 「在播放 MoodWave 時開啟」, `core.js` `App.loudness` — already in the Windows pages |
| signal path text | "依量測的響度" as on Windows |

### 3.4 Packaging

- `dotnet publish MoodChannels.Helper -r osx-x64|osx-arm64 --self-contained -p:PublishSingleFile=true` (built on
  Windows; no .NET needed on the Mac).
- Module folder on macOS: `miku/extension.js`, `miku/helper-client.js`, `miku/bin/<arch>/moodwave-helper`,
  `models/basic/*`, `web/*`. The installer (`make_pkg.py`) copies it like Daphne's into `Resources/ext/moodchannels/`.
- Size: about 30–40 MB per architecture (the .NET runtime inside the helper).
- The advanced AI's CLAP process, `moodwave-clap` (src/MoodChannels.ClapWorker, ONNX Runtime 1.19.2, CPU), is built
  and packed next to the helper (`bin/moodwave-clap`, about 45 MB): the helper starts it as a child of its own when
  the advanced AI is on and there is work, the same as the Windows module does from MIKU (`ClapQueue`).

## 4. Risks and checks

1. **.NET 8 on macOS 11:** officially .NET 8 supports macOS 12+; the test Mac is 11.7. Checked first (step 0); if
   it does not run, the helper targets .NET 6 / 7 for the test build only, or a newer macOS is needed for testing.
2. **Same results on both systems:** a different ffmpeg build resamples to 16 kHz slightly differently. Checked on
   30 files analysed on both (channels, BPM, loudness).
3. **Paths:** macOS paths are `/`-separated and can differ only in case on case-sensitive volumes; the store
   compares paths case-insensitively (as on Windows). Network volumes (`/Volumes/…`): the reader's sequential
   read-ahead applies the same way.

## 5. Steps

0. Check: a .NET 8 self-contained `osx-x64` program and SQLite run on the test Mac.
1. Split `MoodChannels.Service` out of the extension behind `IMoodHost`; the Windows extension becomes an adapter.
   Verify on Windows with the test host: same channels, votes, loudness, priority as now.
2. `MoodChannels.Helper` and its protocol; a test that drives it like `extension.js` would (on Windows first).
3. `miku/extension.js` + `helper-client.js` (Daphne's client as the model).
4. MIKU macOS host changes (§3.3).
5. On the Mac: link `Extensions/MoodChannels` in `~/daphne-test/miku`, run MIKU with Electron 37, check the page,
   the bar, the wheel, votes, analysis, loudness and ReplayGain; compare 30 files with Windows.
6. Packaging into the installer.

Code changes to the MIKU repo stay uncommitted (as on Windows); nothing on the Mac outside `~/daphne-test/` changes.

## 6. Results (2026-10-19, test Mac: MacBook Pro 2014, Intel, macOS 11.7, MIKU run from a checkout with Electron 37)

| Check | Result |
|---|---|
| .NET 8 self-contained on macOS 11.7 | runs (8.0.31), SQLite 3.41, below-normal priority |
| Windows module after the split (same database, test host) | identical output: channel counts, votes, export, loudness, priority, channel start |
| Helper over its protocol (Windows, then the Mac) | analysis, loudness push, settings, events, log, stop |
| Same 20 files analysed on Windows and on the Mac | channels 20/20, all 41 descriptors bit-identical, loudness identical |
| MoodWave inside MIKU on the Mac | page, cards, player bar band with 👍 / 👎, now-playing wheel, votes (👎 skips and leaves the queue) |
| ReplayGain on the Mac (Daphne core) | track −1.00 dB, album −6.95 dB (from the tracks), "source" only for a MoodWave queue; as computed independently |
| Advanced AI, radio, Smart Playlist (2026-10-20, `run.sh --music`, 24 songs) | all 243 checks pass: helper 68, extension 42, ai 57, player 43, replaygain 33; 40 files embedded in 182 s (CPU, 6 segments) |
| Packed module (`miku/pack-mac.sh`), installed layout | loads from `<ext>/moodchannels/`, helper signed ad hoc and runs; ~90 MB per architecture |

Found on the way:
- A Windows mtime (.NET ticks, ~6e17) read as a double loses its last digits: the helper reads whole numbers as
  integers (the store would otherwise analyse every file again).
- The bar band did not widen when the vote buttons appeared during its push-in; it is fitted again when the push
  ends. In a narrow window (bar's left column too small) the bar's buttons are left out (the wheel keeps them).
- MIKU on the test Mac seemed to hang at start-up in `fs.watch` of the library folder on its WebDAV volume. Cause:
  started with `open -a Electron.app` (as the tests did, over ssh), the unsigned Electron.app is its own process
  responsible for privacy checks, and its first access to the network volume blocks on one. Started the usual way
  (`Run MIKU (Electron 37).command`, inside Terminal, which has access) it does not happen. Not this module.

## 7. How to

- Build the helper (any system with the .NET 8 SDK): `miku/build-helper.sh` → `miku/bin/osx-x64|osx-arm64/`
  (`moodwave-helper` and `moodwave-clap`).
- Run from a MIKU checkout on a Mac: link this repository at `<MIKU>/Extensions/MoodChannels` and create the link
  `miku/web → ../web` (the loader reads `miku/web/`).
- Installer: MIKU's `mac/build/build.sh` runs `miku/pack-mac.sh`, which copies `extension.js`, `LICENSE`, `web/`,
  `models/basic/`, `bin/moodwave-helper` and `bin/moodwave-clap` per architecture. From a fresh clone it fetches the
  basic model (`tools/fetch-models.py`, Python 3) and builds both programs (.NET 8 SDK on the Mac); checked on the
  test Mac (macOS 11.7, SDK 8.0.426): both architectures packed in 207 s, the packed helper starts.
- Tests (on the Mac, from this repository inside a MIKU checkout): `miku/test/run.sh [--miku <checkout>]
  [--music <folder>]`. Test audio is made with ffmpeg; `--music` adds 8 real songs from that folder (only read),
  without which nothing has a channel and channel start is left out. Each test file also runs alone with Node:
  - `helper.test.js`: the helper over its protocol: start-up, what is about to play first (also while paused),
    analysis and failures, loudness (6.02 LU for twice the level, silence, true peak), channels, channel start,
    votes, settings, built-in tuning, library change, stop, and a second start (nothing analysed again, Windows
    mtimes read exactly, votes kept, a changed file analysed again). `--out` writes each file's loudness and channels.
  - `extension.test.js`: `extension.js` in a stand-in for MIKU's host: saved settings, live tracks left out, calls,
    events, settings saved through MIKU, `loudness.lookup`, upcoming, library change, a killed helper started again,
    plays (`onTrackPlayed`) and the continuation, stop, a module without its helper.
  - `ai.test.js`: the advanced AI in the helper: off at first (no radio, nothing to go on with), the model copied
    and checked, every analysed file embedded (pause, resume), a track's, an album's (from its most played track,
    its tracks 6 apart) and an artist's radio, one version of a song per radio, plays, `continue` (the Smart
    Playlist and a radio going on), radio votes per start, a second start (nothing embedded again), forget and
    delete. Needs the model in `models/clap/` (or `MOODWAVE_MODEL_SOURCE`).
  - `player.test.js`: the checkout's own `player.js` against a stand-in engine and library: random songs / albums
    when the queue runs out, the module asked (「佇列播完後」 handed to it, or its own radio), its answer dropped when
    the queue changed meanwhile, where the Smart Playlist's part starts through add / remove / move / clear / a
    library change / a restart, and the `played` reports (skipped, stopped, the source of their time).
  - `replaygain.test.js`: the checkout's own `rgDb` (web-audio.js) and `WebAudioEngine.loudness` (main.js) against
    values worked out in the test: -18 LUFS, true-peak limit, preamp, ±24 dB, tags as fallback, "source" only for
    MoodWave's queue, album loudness as the duration-weighted energy mean.
  - `compare.js a.json b.json`: two systems' `--out` over the same files: loudness ±0.05 LU, peaks ±1 %, channels.
