# System map

What exists today, what is in progress, and what is still planned, as of
27 November 2026 (main at `d2f3cda`). [Architecture](Architecture) describes
the target design; this page shows where the code actually is. Update it when
a component changes state.

| Stroke | Meaning |
|---|---|
| Solid | Built and merged on main |
| Dashed, amber | In progress on a branch |
| Dotted, grey | Planned |
| Red | A retention gate: what may reach disk is decided here |
| Grey fill | Outside Lookback (the game, the OS, your footage) |

## Processes and the wires between them

![The shell spawns the recorder, reads LOOKBACK_READY, receives /events, sends /ingest frames and loads the calibration panel; inside the recorder the event bus fans out to the pipe server and the WebSocket bridge](images/system-map-processes.svg)

The shell owns the recorder's lifetime and talks to it over three loopback
endpoints. The bus is the only path out of the recorder: the pipe server is
for the planned Signals and Clipper hosts, and the WebSocket bridge serves the
shell. On Windows the recorder's `CaptureService` records every allowed game
that game detection sees start: capture, encode, audio, the replay ring, 2 s
segments on disk, and the profile and clipper for events and candidates.
The recorder answers the shell's `rate`, `analyze` and `save` commands with an
ack: ratings are stored behind `RetentionRules` and broadcast as `clip.rating`,
footage is analysed in the recorder (profile + clipper on a private bus), and
`save` cuts a live candidate into `clips/`. `recorder.storage` reports the
20 GB footage and 10 GB clip budgets for the shell's warning banner. See [Recorder Host](Recorder-Host#control-commands)
and the recorder-wiring step in `docs/capture-handoff.md`.

## Live capture: what runs inside one session

![Frames and audio always feed the analyzer; everything right of the retention line is created only when RetentionRules allows the session to persist](images/system-map-capture.svg)

An official-player session stops at the dashed red line: the converter,
clock, encoders, ring and store are never created, so nothing exists that
could reach disk. Every timestamp is raw QPC; media time is derived only when
muxing. Measured on AETHER: WGC at 144 fps in, NVENC HEVC 1080p60 out at
60.0 fps with no skipped ticks. A lock-order inversion between WGC and the
encoder used to deadlock capture; one device lock fixed it, and a watchdog now
swaps stalled encoders or ends the recorder instead of hanging. See [Live Capture](Live-Capture).

## From signals to clips to a learned scorer

![Footage, live capture and the official player feed game-agnostic signals; profiles turn them into events and candidates; ratings will train an ONNX scorer that scores candidates](images/system-map-loop.svg)

Two writes leave this loop, and both are gated. Calibration writes only the
region and threshold numbers you confirm, through
`RetentionRules.ValidateDerived`. The planned `RatingStore` will write ratings
only for sessions that may persist, so official-player footage never becomes
training data. Ratings are 1–5 stars on content (is this moment a highlight);
video quality is measured automatically, not rated. The shell sends each
rating to the recorder and shows it as stored once the recorder acks it; a
refused or unanswered rating stays in the shell's memory.

## Status by component

| Component | Where | State |
|---|---|---|
| Event schema, bus, named pipes, WebSocket bridge, session log | Contracts · Core · Stream | Done (M0) |
| Recorder host and the `LOOKBACK_READY` handshake | Recorder | Done |
| Footage decoding, video and audio signals, live analyzer | Media · Signals | Done (M1) |
| Profile tree, detector, candidates, learned fingerprints | Profiles · `profiles/` | Done |
| Testbench CLI, timeline viewer, dataset browser, live profiler | Testbench · Profiler | Done |
| Official-player calibration, recorder side | Calibration | Done |
| Replay ring, fMP4 muxer, clip writer, segment store, cache sweeper | Replay | Done |
| Governor policy and runtime controller | Governor | Done |
| WGC and DXGI capture, GPU downscale, NV12 converter, live session | Capture | Done |
| Media Foundation encoders (NVENC, AMF, software), CFR encode clock, `--encode` bench | Encode · Capture.Cli | Done (#15) |
| Electron shell: library, timeline, 1–5 star rating with previews and Open footage, official-player viewer and ingest sender | `app/` | Done (#17) |
| Shell on the recorder's commands: supervision and parent-exit, stored ratings, Save clips, storage warnings | `app/` | Done (#26) |
| WASAPI loopback and mic, AAC encoder, `audio` bench | Audio · Encode · Capture.Cli | Done (#16); process loopback untested (needs build 20348+) |
| Governor Windows probe, `lookback-capture probe` | Governor.Windows | Done (#14) |
| [Clipper v1](Clipper): normalisation, excitement curve, peaks, windows, fusion with profile events, QA gates; `ClipperRunner` | Clipper | Done (#19, M2); hosted in the testbench, recorder hosting with the wiring |
| Game detection: process and window watcher, profile matching, allow/deny lists, debounced `GameLifecycle`, `lookback-detect` bench ([Game Detection](Game-Detection)) | Detection · Detection.Windows | Done (#20); the recorder wiring hosts it |
| Scoring plumbing: `ClipRating`, RatingStore, training, ONNX inference ([Clip Scoring](Clip-Scoring)) | Scoring · `ml/` | Done (#21, M4 plumbing); trained on synthetic data until real ratings exist |
| Present hook for our own test apps only, injector allowlist, hook capture source | `native/hook` · Capture | In progress (M6) |
| No-deadlock encode path: one device lock, stall watchdog, encoder fallback chain, blocklist, fail-fast | Core · Encode · Capture | Done (#22) |
| Recorder control commands: `rate` (stored, broadcast), `analyze` (footage through profile + clipper), parent-exit | Recorder · Stream | Done |
| Recorder live capture: detection → capture, encode, audio, ring, store, profile + clipper; `save`; storage budgets and `recorder.storage` | Recorder | Done |
| Governor runtime controller in live sessions; testbench multi-recorder mode | Recorder · Governor | Next |
| Training the scorer on real ratings | `ml/` · Scoring | Planned (M4), needs ratings |
| Quality reports: transcode, VMAF, audio checks | Quality | Planned (M5) |
| HDR encode tone-map, AV1 muxing (`av1C`) | Encode · Replay | Planned (M6) |
| Memory module (secondary, never on anti-cheat titles) | Memory | Planned (M7) |

## Known gaps

1. **One game at a time.** A second game that starts while one records is
   logged, not recorded. A testbench mode that runs several recorders at once
   (lower tier, larger footage budget) is planned.
2. **The quality tier is fixed per session.** The governor plans it at session
   start; its runtime controller (step down under pressure, back up with
   hysteresis) is built and tested but not wired into live sessions yet.
3. **The learned scorer is built but not hosted.** Candidates carry the
   heuristic score until the recorder hosts the ONNX scorer and real ratings
   have trained a model.
4. **Every HUD region is an estimate.** Profiles need calibrating against real
   gameplay (official-player calibration exists for this).
5. **AV1 is capped.** The muxer has no `av1C` writer, so the governor tops out
   at HEVC; software and H.264 fallbacks cover machines without HEVC.
6. **The pipe server has no consumers yet.** It is there for out-of-process
   Signals and Clipper hosts; today everything runs inside the recorder.
