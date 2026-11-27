# Lookback

A local game recorder built around **automatic clipping**. Lookback watches
frames, audio and input from any game, finds the moments worth keeping,
scores them, checks each candidate, and learns from how clips are rated.

Windows, C# on .NET 10, with a small native present hook for D3D11/D3D12 and
an Electron shell. See [docs/architecture.md](docs/architecture.md).

## Status

- **M0 (done):** event schema, in-process bus, named pipes, WebSocket
  bridge, session log with retention enforcement, recorder host.
- **M1:** footage decoding, game-agnostic video and audio signals, and the
  testbench with an interactive timeline viewer.

Live capture, the clipper and the Electron shell come in later milestones.

## Build and test

Requires the .NET 10 SDK, and ffmpeg on PATH for footage.

```
dotnet build Lookback.slnx
dotnet test --solution Lookback.slnx
dotnet run --project src/Lookback.Recorder
dotnet run --project src/Lookback.Testbench -- analyze my-clip.mp4 --out out
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
src/Lookback.Recorder/    service host
src/Lookback.Testbench/   footage CLI and timeline viewer
tests/                    xUnit v3 tests
docs/                     architecture
```

## Data and footage

Lookback stores data only when it may. Official embedded players (YouTube,
Twitch) are analysed live and nothing is kept. Third-party footage needs a
recorded permission or licence before anything is written. See
[Data retention](docs/architecture.md#data-retention).
