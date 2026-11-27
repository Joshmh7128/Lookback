# Lookback

A local game recorder built around **automatic clipping**. Lookback watches
frames, audio and input from any game, finds the moments worth keeping,
scores them, checks each candidate, and learns from how clips are rated.

Windows, C# on .NET 10, with a small native present hook for D3D11/D3D12 and
an Electron shell. See [docs/architecture.md](docs/architecture.md).

## Status

- **M0 (done):** event schema, in-process bus, named pipes, WebSocket
  bridge, session log with retention enforcement, recorder host.
- **M1 (done):** footage decoding, game-agnostic video and audio signals,
  and the testbench with an interactive timeline viewer.
- **Game profiles:** per-game data (HUD regions and event rules) on top of
  the game-agnostic signals, starting with Overwatch; a calibration view and
  per-game dataset scoring.

Live capture, the clipper and the Electron shell come in later milestones.

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
profiles/                 game profiles as JSON
datasets/                 per-game dataset manifests (no media)
src/Lookback.Recorder/    service host
src/Lookback.Testbench/   footage CLI and timeline viewer
src/Lookback.Profiler/    live event profiler page, for demos
tests/                    xUnit v3 tests
docs/                     architecture
```

## Data and footage

Lookback stores data only when it may. Official embedded players (YouTube,
Twitch) are analysed live and nothing is kept. Third-party footage needs a
recorded permission or licence before anything is written. See
[Data retention](docs/architecture.md#data-retention).
