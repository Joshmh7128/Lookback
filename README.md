# Lookback

A local game recorder built around **automatic clipping**. Lookback watches
frames, audio and input from any game, finds the moments worth keeping,
scores them, checks each candidate, and learns from how clips are rated.

Windows, C# on .NET 10, with a small native present hook for D3D11/D3D12 and
an Electron shell. See [docs/architecture.md](docs/architecture.md).

## Quickstart

The desktop app (`app/`, Electron) runs the recorder for you; the command-line
programs run footage through the same pipeline. With the .NET 10 SDK, Node.js
24 and ffmpeg on `PATH`:

```powershell
git clone https://github.com/Joshmh7128/Lookback.git; cd Lookback
dotnet build Lookback.slnx -c Release
dotnet run -c Release --project src/Lookback.Testbench -- analyze my-clip.mp4 --out out --profile generic
# open out\<session>.html in a browser
dotnet run -c Release --project src/Lookback.Testbench -- live my-clip.mp4 --profile overwatch
# open the printed http://127.0.0.1:…/profiler#token=… link
cd app; npm ci; npm start
# the desktop app: library, timeline, ratings, and "Open viewer" for official-player calibration
```

Once it is cloned, `launch.bat` in the root updates `main`, builds, installs
the app's packages and starts the desktop app in one step (`launch.bat --help`
for its options).

The full walkthrough, with expected output and troubleshooting, is
[docs/wiki/Quickstart.md](docs/wiki/Quickstart.md).

To demo every component (recorder stream, signals, profiles, detection,
clipper, live capture, encode, audio, governor, watchdog, ratings and
training, the shell) on generated test footage, one command each:

```powershell
./scripts/showcase.ps1 list
```

What each demo shows and what you should see is in
[docs/wiki/Showcase.md](docs/wiki/Showcase.md).

## Status

- **M0 (done):** event schema, in-process bus, named pipes, WebSocket
  bridge, session log with retention enforcement, recorder host.
- **M1 (done):** footage decoding, game-agnostic video and audio signals,
  and the testbench with an interactive timeline viewer.
- **Game profiles:** per-game data (HUD regions and event rules) on top of
  the game-agnostic signals, starting with Overwatch; a calibration view and
  per-game dataset scoring. Profiles form a tree (game, sub-game such as a
  Roblox experience, mode), detected automatically at each level, with
  fingerprints learned from labelled footage and clip candidates built from
  the events.

- **Desktop shell (M2):** Electron app that manages the recorder and shows
  the clip library, a live timeline and ratings, and the official-player
  viewer that streams YouTube or Twitch playback to calibration, in memory
  only. See [docs/wiki/Desktop-Shell.md](docs/wiki/Desktop-Shell.md).
- **Clipper v1 (M2):** per-game robust normalisation of the signals, an
  excitement curve with weights as data, peaks with hysteresis, fusion with
  the profile's events into one candidate set, and QA gates on every
  candidate (`src/Lookback.Clipper`). See [docs/wiki/Clipper.md](docs/wiki/Clipper.md).

The remaining live-capture milestones come next.

## Build and test

Requires the .NET 10 SDK, and ffmpeg on PATH for footage.

```
dotnet build Lookback.slnx
dotnet test --solution Lookback.slnx
dotnet run --project src/Lookback.Recorder
dotnet run --project src/Lookback.Testbench -- analyze my-clip.mp4 --out out
dotnet run --project src/Lookback.Testbench -- analyze my-clip.mp4 --profile overwatch
dotnet run --project src/Lookback.Testbench -- dataset datasets/overwatch/manifest.json
dotnet run --project src/Lookback.Testbench -- live my-clip.mp4 --profile overwatch
dotnet run --project src/Lookback.Testbench -- fingerprint datasets/roblox/manifest.json
dotnet run --project src/Lookback.Testbench -- profiles
```

The recorder prints one `LOOKBACK_READY {"pipe":…,"ws":…,"token":…}` line on
stdout; logs go to stderr.

## Layout

```
proto/                    event schema (protobuf)
src/Lookback.Contracts/   generated schema, topics, retention rules
src/Lookback.Core/        clock, lock-free ring, event bus
src/Lookback.Stream/      pipes, WebSocket bridge, session log
src/Lookback.Media/       analysis format, ffmpeg FileSource
src/Lookback.Signals/     video and audio signals
src/Lookback.Profiles/    game profiles: regions, rules, event detector
src/Lookback.Clipper/     excitement curve, peaks, candidates, QA gates
profiles/                 game profiles as JSON
datasets/                 per-game dataset manifests (no media)
src/Lookback.Recorder/    service host
src/Lookback.Testbench/   footage CLI and timeline viewer
src/Lookback.Profiler/    live event profiler page, for demos
src/Lookback.Calibration/ official-player calibration (/ingest, /calibrate)
app/                      Electron desktop shell (TypeScript)
tests/                    xUnit v3 tests
docs/                     architecture
```

## Data and footage

Lookback stores data only when it may. Official embedded players (YouTube,
Twitch) are analysed live and nothing is kept. Third-party footage needs a
recorded permission or licence before anything is written. See
[Data retention](docs/architecture.md#data-retention).
