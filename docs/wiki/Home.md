# Lookback wiki

Lookback is a local Windows game recorder built around **automatic clipping**.
It watches frames, audio and input from any game, finds the moments worth
keeping, scores them, runs QA on every candidate, and learns from how clips
are rated. Signals are game-agnostic; no game-specific API is required.

This wiki explains how each part works and how the parts connect. Pages are
written from the source in `docs/wiki/` in the repository and published here
automatically when `main` changes, so the wiki always matches the code.

## Start here

| Page | What it covers |
|---|---|
| [Quickstart](Quickstart) | Build, analyse a clip, open the report, watch events fire live |
| [Architecture](Architecture) | The whole system: processes, data flow, recording tiers, milestones |
| [Detection Walkthrough](Detection-Walkthrough) | How frames and audio become game events with no game API, stage by stage, with a worked example |
| [Detection Design Notes](Detection-Design-Notes) | Why detection is built this way: trade-offs, limits, measurement, common questions |
| [Project Structure](Project-Structure) | Every folder and project, and how they depend on each other |
| [Infrastructure](Infrastructure) | Build system, CI, runtime launch, security boundaries |
| [Event Schema](Event-Schema) | `events.proto`: every message, field and topic |
| [Data Retention](Data-Retention) | What may be stored, from which sources, and where that is enforced |
| [Core Library](Core-Library) | Clock, lock-free ring, event bus, envelope factory |
| [Stream Library](Stream-Library) | Framing, named pipes, WebSocket bridge, session log |
| [Recorder Host](Recorder-Host) | The service executable and its startup handshake |
| [Media Library](Media-Library) | Analysis format, frames and audio blocks, the ffmpeg footage decoder |
| [Signals Library](Signals-Library) | Every video and audio signal, how it is computed and why |
| [Profiles and Local Datasets](Local-Datasets) | Profile structure, and how the local dataset is built, weighted, stored and compared |
| [Game Profiles](Game-Profiles) | Per-game data: HUD regions, event rules, Overwatch, calibration, datasets and scoring |
| [Clipper](Clipper) | Signals and events to clip candidates: normalisation, the excitement curve, peaks, fusion, QA gates |
| [Testbench](Testbench) | The footage CLI and the interactive timeline viewer |
| [Live Profiler](Live-Profiler) | Watch every event fire in real time, for demos and profile debugging |
| [Dataset Browser](Dataset-Browser) | Sift profiles and datasets: what each profile identifies, its key signals and regions, coverage and scores |
| [Testing](Testing) | What each test proves |

## Status

| Milestone | Scope | State |
|---|---|---|
| M0 | Schema, bus, pipes, WebSocket bridge, session log, recorder host, CI | Done |
| M1 | FileSource, video and audio signals, testbench and timeline viewer | Done (GPU downscale moves to M3 with live capture) |
| Profiles | Game profiles as data, Overwatch first, event detector, calibration view, dataset scoring | In review |
| Profile tree | Game, sub-game and mode profiles (Roblox experiences), detection at each level, learned fingerprints, clip candidates | In review |
| M2 | Game identifier, Clipper v1, rating UI, official-player viewer | In progress: [Clipper v1](Clipper) built (curve, peaks, fusion with profile events, QA gates) |
| M3 | Live capture, encode, replay ring, disk store, resource governor | Planned |
| M4 | Training, ONNX scoring, calibration | Planned |
| M5 | Transcode, VMAF report, audio checks | Planned |
| M6 | D3D11/D3D12 present hook, DXGI duplication, HDR | Planned |
| M7 | Memory module (secondary) | Planned |

## The one-paragraph version

Every component speaks one language: a protobuf `Envelope` stamped with a
raw QueryPerformanceCounter (QPC) tick. Producers publish envelopes onto an
in-process bus that never blocks them. The bus fans out to named pipes (the
other Lookback processes), a loopback WebSocket (the Electron shell) and the
session log on disk. Whether anything reaches disk is decided in one place,
`RetentionRules`, so a source that must not be stored (an official YouTube or
Twitch player) cannot be stored by accident.
