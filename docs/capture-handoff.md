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
| Governor policy (machine profile → ring/disk budget + quality tier) and runtime controller (step down under pressure, back up with hysteresis) | `Lookback.Governor` | **Done**, tested. The Windows probe is in progress in a parallel session (branch `claude/governor-probe`) |
| Audio sample clock (WASAPI device position → QPC, weighted least-squares drift fit) and live assembler (downmix, resample to 48 kHz, silence for gaps) | `Lookback.Media` (`Live/`) | **Done**, tested |
| Live analyzer (capture threads hand frames/hops through pools + SPSC rings, below-normal worker publishes signals with capture QPC, thins to 30 fps, drops when behind) | `Lookback.Signals` | **Done**, tested |
| WGC capture (raw WinRT interop), DXGI duplication fallback, live session, bench CLI (`lookback-capture`) | `Lookback.Capture`, `Lookback.Capture.Cli` (Windows) | **Done**, verified on AETHER (DXGI ~57 fps, WGC ~140 fps, analysis 29.4 fps, 0 dropped; `--official-player` persists nothing) |
| GPU downscale to 384x216 BGRA (compute shader, letterboxed like FileSource; SDR/scRGB/PQ) and BGRA→NV12 (D3D11 video processor) | `Lookback.Capture` | Downscale **done**, WARP-tested. `Nv12Converter` **done**, tested on AETHER through the encoder and ticker tests (WARP has no video processor) |
| Media Foundation hardware encode (async MFT, NVENC/AMF/QSV, software fallback) | `Lookback.Encode` (Windows) | `MfVideoEncoder` **done**, verified on AETHER: NVENC H.264 and HEVC, software H.264 on the RTX and on WARP, all round-trip through the ring and muxer (ffprobe: every frame, keyframe every 2 s, no B-frames, no decode errors) |
| CFR encode clock and bench | `Lookback.Encode` (`EncodeTicker`), `Lookback.Capture.Cli` | **Done**, tested. Live on AETHER: WGC 144 fps in, HEVC 1080p60 out at 60.0 fps, 0 skipped, 0 busy (`lookback-capture --encode 8 --out clip.mp4`) |
| AAC encoder MFT | `Lookback.Encode` (Windows) | In progress in a parallel session (branch `claude/capture-audio`) |
| WASAPI loopback, process loopback (Win10 20348+ only), mic track | `Lookback.Audio` (Windows) | In progress in a parallel session (branch `claude/capture-audio`) |
| Recorder wiring, control commands on `/events` | `Lookback.Recorder` | Planned; contract additions to be agreed with the Electron thread first |
| Present-hook DLL (C++, D3D11 keyed mutex, D3D12 shared fence) and an injector limited to our own testbench apps | `native/hook` | Planned (M6 scope) |

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

Steps 3–5 are running in parallel sessions on their own branches
(`claude/capture-audio`: steps 3 and 4; `claude/governor-probe`: step 5).
Rebase on main before merging; they touch this file's rows too.

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
3. AAC encoder MFT (sync, 48 kHz stereo float→PCM16) fed from WASAPI.
4. WASAPI endpoint loopback (no process loopback on 19045) + mic, through
   `SampleClock` and `LiveAudioAssembler`.
5. Windows probe for the governor: `GlobalMemoryStatusEx`,
   `IOCTL_STORAGE_QUERY_PROPERTY` (seek penalty), DXGI adapters, and
   `MfVideoEncoder.List` per adapter LUID.
6. Recorder wiring (`CaptureService`, control commands on `/events`).
   Message the Electron thread before touching the contract. Ephemeral
   sessions never create the encoder, ring or store.
   Agreed with the shell session (2026-11-27), recorder side not built yet.
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
   ProfileRunner first at session end. M4's `RatingStore`/`RatingSink` plug
   in here too.
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
7. Present-hook DLL (M6).

Windows projects target `net10.0-windows10.0.22621.0` with
`SupportedOSPlatformVersion` 10.0.19041.0 and build on Linux with
`EnableWindowsTargeting`. Their tests skip off Windows.
