# Live capture layer (M3): design and handoff

This note is what someone picking up the live capture work needs to know: the
design, what has landed and what's next. Update it with every PR on this work.

## Status

| Piece | Project | State |
|---|---|---|
| RAM replay ring (native arena, keyframe/QPC index, no per-packet allocation) | `Lookback.Replay` | **Done**, tested |
| Annex B → length-prefixed, `avcC` / `hvcC` / AAC ASC from in-band parameter sets | `Lookback.Replay` | **Done**, tested |
| fMP4 fragmenter (init segment + `moof`/`mdat`) and fragment reader | `Lookback.Replay` | **Done**, tested against ffprobe/ffmpeg (H.264, HEVC, AAC) |
| Clip writer (ring → fMP4 clip, starts on a keyframe, no re-encode) | `Lookback.Replay` | **Done**, tested |
| Segment store (2 s `.m4s`, `session.json`, budget, oldest-first eviction, pins, clip read-back from disk) | `Lookback.Replay` | **Done**, tested |
| Cache sweeper (unused segments deleted 24 h after session end, swept at launch) | `Lookback.Replay` | **Done**, tested |
| Governor policy (machine profile → ring/disk budget + quality tier) and runtime controller (step down under pressure, back up with hysteresis) | `Lookback.Governor` | **Done**, tested |
| Windows machine probe (RAM, store volume free space + seek penalty, DXGI adapters/outputs, hardware encoder MFTs per LUID), mapping to `MachineProfile` by the captured output, `lookback-capture probe` | `Lookback.Governor.Windows`, `Lookback.Governor` (mapping) | **Done**, tested on AETHER (~80 ms cold, <5 ms warm; RTX → `2160p60-hevc`) |
| Audio sample clock (WASAPI device position → QPC, weighted least-squares drift fit) and live assembler (downmix, resample to 48 kHz, silence for gaps) | `Lookback.Media` (`Live/`) | **Done**, tested |
| Live analyzer (capture threads hand frames/hops through pools + SPSC rings, below-normal worker publishes signals with capture QPC, thins to 30 fps, drops when behind) | `Lookback.Signals` | **Done**, tested |
| WGC capture (raw WinRT interop), DXGI duplication fallback, live session, bench CLI (`lookback-capture`) | `Lookback.Capture`, `Lookback.Capture.Cli` (Windows) | **Done**, verified on AETHER (DXGI ~57 fps, WGC ~140 fps, analysis 29.4 fps, 0 dropped; `--official-player` persists nothing) |
| GPU downscale to 384x216 BGRA (compute shader, letterboxed like FileSource; SDR/scRGB/PQ) and BGRA→NV12 (D3D11 video processor) | `Lookback.Capture` | Downscale **done**, WARP-tested. `Nv12Converter` **done**, tested on AETHER through the encoder and ticker tests (WARP has no video processor) |
| Media Foundation hardware encode (async MFT, NVENC/AMF/QSV, software fallback) | `Lookback.Encode` (Windows) | `MfVideoEncoder` **done**, verified on AETHER: NVENC H.264 and HEVC, software H.264 on the RTX and on WARP, all round-trip through the ring and muxer (ffprobe: every frame, keyframe every 2 s, no B-frames, no decode errors) |
| CFR encode clock and bench | `Lookback.Encode` (`EncodeTicker`), `Lookback.Capture.Cli` | **Done**, tested. Live on AETHER: WGC 144 fps in, HEVC 1080p60 out at 60.0 fps, 0 skipped, 0 busy (`lookback-capture --encode 8 --out clip.mp4`) |
| AAC encode (`MfAacEncoder`: Microsoft AAC MFT, sync, 48 kHz stereo, float→PCM16, 128/160/192 kbps, raw access units, ASC from `MF_MT_USER_DATA`, sample-count timeline with gap fill and trim) | `Lookback.Encode` (Windows) | **Done**, tested on AETHER and the Windows CI runner (ring → `ClipWriter` → ffprobe `aac,48000,2`, decoded onset within 0.1 ms of its QPC, 0 B per call) |
| WASAPI endpoint loopback (silence keep-alive), process loopback (build 20348+, runtime check, falls back to the endpoint where allowed), mic track; QPC via `SampleClock`, drift-following resample to 48 kHz stereo, hops through `LiveAudioAssembler`, re-open on default-device change; `AudioCapturePolicy` (official player: never system loopback, encoder or mic); `lookback-capture audio` | `Lookback.Audio`, `Lookback.Capture.Cli` (Windows) | **Done**, verified on AETHER (Realtek loopback hears a rendered 997 Hz tone, clock fit within 2 ppm, 0 B per packet, Yeti mic streams, re-open gap ~30 ms filled). Process loopback written, never run (needs 20348+) |
| Recorder control commands on `/events` (`rate`, `analyze`, `save` refused until live capture), parent-exit | `Lookback.Recorder`, `Lookback.Stream` | **Done**, tested end to end against the built recorder |
| Recorder live capture (`CaptureService`), `save`, storage budgets and `recorder.storage` | `Lookback.Recorder` | **Done**, verified on AETHER with the stand-in game window |
| Governor runtime controller in live sessions; testbench multi-recorder mode | `Lookback.Recorder` | Next |
| Present-hook DLL (C++, D3D11 keyed mutex, D3D12 shared fence + copy queue), test apps, an injector limited to them, `HookCaptureSource` | `native/hook`, `native/testapps`, `Lookback.Capture/Hook` | **Done**, tested on AETHER: both test apps at full rate (120/144 fps) with 0 hook drops, through the analysis tap and NVENC. Wiki: `Present-Hook.md` |

## Data flow

```
capture backend (WGC | DXGI dup | hook) ── ID3D11Texture2D + QPC
   ├─ analysis tap: compute downscale 384x216 → staging (3-deep, no stall) → VideoFrame → LiveAnalyzer → signals.video
   └─ encode path:  video processor → NV12 (latest) ── EncodeTicker (CFR at tier fps) → MF encoder MFT ── packets
WASAPI loopback ── float PCM + device QPC ── SampleClock ─┬─ mono 48 kHz hops → LiveAnalyzer → signals.audio
                                                         └─ AAC MFT ── packets
packets ──▶ ReplayRing (RAM, ~2 min) ──▶ SegmentWriter (reads ring by sequence) ──▶ 2 s fMP4 segments on disk
clip request ──▶ ring if it still covers the start, else segments ──▶ ClipWriter ──▶ clips/<session>-<n>.mp4
```

Ephemeral sessions (official-player viewer) run only the analysis tap and the
audio analysis. The encoder, ring and segment store are never created, so
nothing exists that could reach disk. `RetentionRules.CanPersist` is the
gate, as for the session log.

## Decisions and why

- **One clock.** Everything is raw QPC. WGC's `SystemRelativeTime` and WASAPI's
  `u64QPCPosition` are QPC in 100 ns units (convert with `QpcTime.Rescale`).
  DXGI's `LastPresentTime` is raw QPC. MP4 media time is derived from QPC
  relative to an origin, only at mux time.
- **Our own muxer, not the MF sink writer.** The ring and the segments need
  packets, not a file. Muxing in managed code keeps it platform-neutral and
  testable on Linux with ffprobe. Clips are fragmented MP4 (init + one
  fragment per GOP), which browsers, VLC, ffmpeg and editors read.
- **No B-frames** in the live encoder (DTS = PTS, low latency). The muxer
  still writes signed composition offsets, so B-frames would work.
- **Parameter sets stay in-band** and `avcC`/`hvcC` is built from the first
  keyframe. If an encoder restarts with new parameters (tier change), the
  segment store starts a new run with a new init segment.
- **AV1 is not muxed yet.** `av1C` needs a sequence-header parser. Until it
  lands, the governor tops out at HEVC even though AETHER's NVENC can do AV1.
- **Ring locking.** One short `Lock` per packet append (copy into native
  memory). It has two writer threads (video and audio encoders) and rare
  readers, so a lock is simpler than per-track SPSC rings and doesn't
  allocate. The append test checks that it allocates zero bytes.

## AETHER (the Windows test box)

Windows 10 22H2 (19045), RTX 4080 SUPER (NVENC H.264/HEVC/AV1 MFTs) driving the
4K144 display, and the 7800X3D iGPU (AMF H.264/HEVC) driving another monitor.
Also a Parsec virtual display. 64 GB RAM, NVMe on C: and D:. VS 2022 17.14
(MSVC 14.44), Windows SDK 22621 and 26100, cmake 3.31, ffmpeg 7.0.1, .NET SDK
10.0.404.

Consequences:
- No process loopback (needs build 20348+). Fall back to endpoint loopback.
- WGC `IsBorderRequired` is Windows 11 only. Check at runtime; on Windows 10
  the yellow border shows.
- Multi-adapter: create the capture/convert/encode device on the adapter
  that owns the target output, and pick the encoder MFT by that adapter's LUID
  (`MFT_ENUM_ADAPTER_LUID`).
- Windows work is built and tested in a separate clone,
  `D:\Lookback Game Recorder\capture-m3`.

## Store layout (as built)

```
<root>/sessions/<id>/session.json    manifest: source, retention, QPC origin/frequency, runs (track configs), pins, ended
<root>/sessions/<id>/init-001.mp4    init segment per run (a new run when track formats change)
<root>/sessions/<id>/seg-000001.m4s  moof+mdat, starts on a video keyframe, ~2 s
<root>/sessions/<id>/segments.jsonl  {run, seq, file, startQpc, endQpc, bytes} per segment
<root>/clips/<id>-001.mp4            saved clips (never swept)
```

`SessionRecording` pulls packets from the ring by sequence number, so the
encoders only ever feed the ring. Late audio (behind the last cut) is
dropped and counted in `Late`. Eviction is store-wide, oldest first, and
skips pinned ranges and the newest segment of an open session.

## Next steps, in order

Where it stopped: the video encode path runs end to end on AETHER. The
three things flagged to verify all hold: the MS software MFT accepts DXGI
surface buffers (on the RTX and on WARP), NVENC raises NeedInput before the
first `Encode` call, and `Dispose` drains without hanging (well under 3 s).
`EncodeTicker` is the CFR clock: `Nv12Converter` runs on the capture thread for
every frame, a high-resolution waitable timer encodes the latest NV12 frame at
each tick with PTS = scheduled tick time, repeats when nothing new arrived,
and skips (counts) ticks that are a full period late instead of bursting.
`lookback-capture --encode <s> --out clip.mp4` exercises it live. See the
wiki page `Live-Capture.md` for the path, measurements and tests.

Steps 3–5 have landed: audio (steps 3 and 4, `claude/capture-audio`) and the
governor probe (step 5, `claude/governor-probe`).

1. ~~Encoder test on Windows~~ **done** (`tests/Lookback.Capture.Tests/EncoderTests.cs`,
   every display adapter: NVENC and AMF on AETHER).
   ~~Live capture deadlock~~ **fixed**: a lock-order inversion between WGC's
   flip thread (holds the D3D11 device lock, then took our managed lock) and
   the encode ticker (the reverse). `GpuDevice` now has one lock, D3D11's own
   (`using (gpu.Enter())`); never add a second lock around device use. A
   `StallWatchdog` swaps stalled encoders down the fallback chain, blocklists
   them per adapter/driver, and fails fast when nothing is left or capture is
   wedged. Encoder-on-own-device (`IsolateDevice`) is verified and opt-in.
   Still open: AMF HEVC ignores the CBR bitrate, and IDRs drift one frame per
   busy tick (force IDRs at GOP-aligned ticks).
2. ~~CFR ticker and `--encode`~~ **done** (`EncodeTicker`, `EncodeBench`).
3. ~~AAC encoder MFT~~ **Done** (`MfAacEncoder`). The Microsoft encoder
   emits no priming frame (packet k decodes to samples k·1024 onward), so
   `EncoderDelay` is 0. It anchors on its first input's QPC and keeps the
   sample count on the clock: silence before late input, early input
   trimmed (20 ms tolerance). ProcessInput/ProcessOutput are raw vtable
   calls, so a call allocates 0 B.
4. ~~WASAPI loopback + mic~~ **Done** (`Lookback.Audio`: `WasapiSource`,
   `AudioCapturePolicy`; bench: `lookback-capture audio`). Notes for whoever
   wires it:
   - Loopback `u64QPCPosition` is the engine's presentation time, about one
     device period (10.5 ms on AETHER) after the packet is readable. It is
     kept as is: it marks when the sound plays, like a frame's present time.
   - With nothing playing, endpoint loopback delivers no packets at all, so
     the source plays silence on the endpoint (`KeepLoopbackAlive`). Process
     loopback has no such stream; the assembler and the encoder fill its
     gaps when the next packet comes.
   - The resampler follows `SampleClock.MeasuredRate` once the fit has 5 s,
     so 48 kHz output tracks QPC instead of drifting into periodic drops.
   - A default-device change re-opens on the new device (its format may
     differ); the timeline carries on and the gap is filled. Tested by
     driving the notification hook, not by switching AETHER's real default.
   - Process loopback (`ActivateAudioInterfaceAsync`) is written to the docs
     but has never run: AETHER is 19045. Its test runs on 20348+ with an
     audio device, which CI runners don't have.
   - Wiring: `AudioCapturePolicy.Decide(started, OsBuild, gamePid, mic)`
     gives the plan; then `WasapiSource.Start(AudioSourceOptions.ForGame(plan,
     pid), plan.Encode ? aac.Encode : null, b => analyzer.SubmitAudio(b))`.
     Game audio is AAC track "game"; the mic is a second track "mic", and
     mic hops are not analysed.
5. ~~Windows probe for the governor~~ **Done.** `WindowsMachineProbe.Run(storePath)`
   then `MachineProfileMapper.Map(probe, captureTarget.ToProbeTarget())` gives
   the `MachineProfile` for `GovernorPolicy.Decide`. The recorder should probe
   once per capture target at session start. See the wiki's Resource Governor page.
6. Recorder wiring. **Control commands done** (`ControlService`,
   `SessionDirectory`, `FootageSessions`, `ParentWatcher`; the bridge's
   `WebSocketBridgeOptions.Control` hook). **Live capture done**
   (`CaptureService`: detection → probe and plan → capture, encode, audio,
   ring, store, profile + clipper; `save`; 20 GB footage and 10 GB clip
   budgets with `recorder.storage`). Still to do: wire `GovernorController`
   (step the tier down under pressure) into live sessions, and a testbench
   mode that runs several recorders at once with a lower tier and a larger
   footage budget.
   Message the Electron thread before touching the contract. Ephemeral
   sessions never create the encoder, ring or store.
   Agreed with the shell session (2026-11-27); `rate` and `analyze` are built, `save` waits for `CaptureService`.
   Additive: `LOOKBACK_READY` and the existing `/events` and `/ingest`
   traffic do not change. Inbound text JSON on `/events`, same token and
   Origin checks:
   `{"type":"rate","id":"<client request id>","sessionId":"…","candidateId":"…","stars":1..5}`
   (`stars` 0 clears) and, once clips can be cut,
   `{"type":"save","id":"…","sessionId":"…","candidateId":"…"}`. The
   recorder answers that client only with
   `{"type":"ack","id":"…","ok":true}` or `ok:false` plus
   `"error":"ephemeral|unknown-session|unknown-candidate|bad-request"`.
   On success it publishes `ClipRating` (new Envelope field 20, topic
   `clip.rating`: `candidate_id`=1, `session_id`=2, `stars`=3; the field
   number the M4 scorer design reserved) or the existing `ClipSaved` on
   `clip.saved`. Ratings persist through the M4 `RatingStore` only when
   `RetentionRules.CanPersist` allows; official-player sessions get
   `ephemeral` and nothing is written. The shell keeps ratings in memory
   when there is no ack within ~2 s or the ack is `ok:false`. `stars` is
   1–5 for content ("is this a highlight"), 0 clears.
   Also agreed:
   `{"type":"analyze","id":"…","path":"<absolute path>","profile":"<optional>","retention":"owned|permissioned|open-license","permissionRef":"<unless owned>"}`.
   The recorder acks, then runs FootageAnalyzer + ProfileRunner (with
   `publishCandidates: false`) + `ClipperRunner` on the file and publishes
   a normal FILE session on `/events` (SessionStarted with `title` = exactly
   the path sent, which the shell uses to link previews, then `game.event`,
   `clip.candidate`, `qa.report`, SessionEnded; no signal frames to the
   shell). `RetentionRules.Validate` applies. Until this exists the shell
   falls back to `lookback-testbench live <file> --pipe <recorder pipe>`.
   Hosting the clipper (branch `claude/clipper-v1`): with a profile,
   `new ProfileRunner(bus, catalog, gameId, method, sessionId, freq, publishCandidates: false)`
   and `new ClipperRunner(bus, catalog, gameId, sessionId, freq)`; for an
   unknown game `new ClipperRunner(bus, sessionId, freq)`. Dispose the
   ProfileRunner first at session end. Per-candidate features for M4 come
   from `ClipperRunner.CandidateFinished` (`CandidateFeatures`,
   "clipper-features/1"; runs on the pump thread, hand off).
   Hosting ratings (PR #21, `Lookback.Scoring`): create
   `new RatingStore(datasetRoot)` and
   `new RatingSink(bus, store, new RatingSinkOptions { Source = "shell", SessionLookup = …, CandidateLookup = …, OnOutcome = … })`
   before any session starts (it learns sessions from `session` and
   candidates from `clip.*`). On `rate`: ack, then publish `ClipRating` on
   `clip.rating`; the sink applies `RetentionRules.CanPersist` and refuses
   ephemeral, official-player and unknown sessions. Or call
   `store.Append(...)` synchronously and put its `RatingWriteResult` in the
   ack, but never both, or the rating is written twice.
   Hosting game detection (PR #20, `src/Lookback.Detection.Windows`):
   `await using DetectionRunner d = WindowsDetection.CreateRunner(bus);`
   (1 s poll, built-in profiles, user allow/deny list in
   `%LOCALAPPDATA%\Lookback\detection.json`). `d.Changed` fires on the poll
   thread after the `game.lifecycle` envelope is published, with `Kind`
   Detected, Focused, Unfocused, WindowChanged or Exited. On Detected or
   WindowChanged call `WindowsDetection.TryCaptureTarget(c.Game)` (null if the
   window is gone) and start or retarget the capture session. Honour
   `c.Game.Policy` (`AllowPresentHook`, `AllowMemoryReading`); never relax it.
   Wire `d.HintsChanged += g => profileRunner.ApplyHints(g.Hints, now)` and
   call `ApplyHints` once right after starting the ProfileRunner. If the
   capture session reuses `c.Game.Id` as its session id, publish one lifecycle
   envelope again after `SessionStarted` (`StreamSnapshot` resets per session).
   **Never create a DXGI factory while encoding** (`CaptureTarget.Outputs`,
   `ForWindow`, `FindWindow`, the governor probe's adapter scan): measured by
   the detection session on AETHER, it stalls NVENC briefly and the ticker
   counts busy ticks. Call those helpers only when (re)targeting, never on a
   timer, and run the governor probe before the encoder starts. Detection
   gets adapter LUIDs from `QueryDisplayConfig` instead.
7. ~~Present-hook DLL (M6)~~ **done** (test apps only; see the wiki's
   Present-Hook page for the sync model, allowlist and limits).

Windows projects target `net10.0-windows10.0.22621.0` with
`SupportedOSPlatformVersion` 10.0.19041.0 and build on Linux with
`EnableWindowsTargeting`. Their tests skip off Windows.
