# Lookback architecture

This is the repository summary. In-depth pages with diagrams, one per
component, are in [`docs/wiki/`](wiki/Home.md) and published to the GitHub
wiki.

## Goal

A local game recorder whose focus is **automatic clipping**: it watches
frames, audio and input from any game, finds the moments worth keeping,
scores them, runs per-game QA on each candidate, and learns from how clips
are rated. Signals are game-agnostic; no game-specific APIs are required.

## Processes

| Process | Owns |
|---|---|
| `Lookback.Recorder` | Detection, capture (WGC, DXGI duplication, the present hook on our own test apps, FileSource), GPU downscale, hardware encode, RAM replay ring, disk session store, audio, resource governor |
| `Lookback.Signals` | Video, audio and input feature extraction; the event stream; session feature log |
| `Lookback.Clipper` | Per-game normalisation, excitement curve, candidates, ONNX scoring, QA |
| `Lookback.Testbench` | Feeds footage files through Signals and Clipper |
| `native/hook` | C++ present hook for D3D11/D3D12 (the only native code; it lives in the rendering process, and only ever in our own `native/testapps`, never a game) |
| `app/` | Electron shell: library, live timeline, rating UI, official-player viewer |
| `ml/` | Offline Python training and ONNX export |

## Event stream

- Schema: [`proto/lookback/v1/events.proto`](../proto/lookback/v1/events.proto). Every message is an `Envelope` stamped with raw QPC ticks; `SessionStarted` carries the counter frequency.
- In process: `IEventBus` (`InProcessEventBus`). Lock-free publish; each subscriber has its own bounded queue that drops its oldest events, so a slow consumer never stalls capture.
- Between processes: named pipes, length-prefixed protobuf, current-user ACL.
- To Electron: loopback WebSocket at `/events`, JSON, token plus Origin allow-list. The recorder prints `LOOKBACK_READY {json}` on stdout with the endpoint and token.
- Persistence: append-only session log (`.lbklog`) plus a watermark index (`.lbkidx`) for correct seeking even when producers publish slightly out of order.

## Data retention

`RetentionRules` is the single gate for anything touching disk.

| Source | Allowed retention |
|---|---|
| Live game you play | `OWNED` |
| Your own footage file | `OWNED` |
| Third-party footage file | `PERMISSIONED` or `OPEN_LICENSE`, with a `permission_ref` |
| Official embedded player (YouTube, Twitch) | `EPHEMERAL` only: analysed live, nothing stored |

`SessionLogWriter` refuses to create files for ephemeral sessions or for
third-party footage without a permission reference.

## Recording tiers

| Tier | Contents | Default retention |
|---|---|---|
| RAM replay ring | Encoded packets by keyframe | ~2 minutes, sized by the governor |
| Disk session store | Whole session as 2 s fMP4 segments | Session, within a disk budget; unused segments cleaned 24 h after the session |
| Feature log | Signals and embeddings (~80 MB/hour) | Outlives the video |

## Milestones

| # | Scope |
|---|---|
| M0 | Solution, contracts, bus, pipes, WebSocket bridge, session log, CI |
| M1 | FileSource (ffmpeg), video and audio signals, testbench CLI and timeline viewer |
| M2 | Game identifier, Clipper v1 (built: `src/Lookback.Clipper`), rating UI, official-player viewer |
| M3 | Live capture: detection, WGC, GPU downscale, encode, ring, disk store, governor, audio, Raw Input |
| M4 | Training, ONNX scoring, calibration |
| M5 | Transcode, VMAF report, audio checks |
| M6 | Present hook D3D11/D3D12, DXGI duplication, HDR |
| M7 | Memory module (secondary) |
