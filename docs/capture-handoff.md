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
| Governor policy (machine profile → ring/disk budget + quality tier) and runtime controller (step down under pressure, back up with hysteresis) | `Lookback.Governor` | **Done**, tested. The Windows probe is still to come |
| Audio sample clock (WASAPI device position → QPC, weighted least-squares drift fit) and live assembler (downmix, resample to 48 kHz, silence for gaps) | `Lookback.Media` (`Live/`) | **Done**, tested |
| Live analyzer (capture threads hand frames/hops through pools + SPSC rings, below-normal worker publishes signals with capture QPC, thins to 30 fps, drops when behind) | `Lookback.Signals` | **Done**, tested |
| WGC capture (raw WinRT interop), DXGI duplication fallback, live session, bench CLI (`lookback-capture`) | `Lookback.Capture`, `Lookback.Capture.Cli` (Windows) | **Done**, verified on AETHER (DXGI ~57 fps, WGC ~140 fps, analysis 29.4 fps, 0 dropped; `--official-player` persists nothing) |
| GPU downscale to 384x216 BGRA (compute shader, letterboxed like FileSource; SDR/scRGB/PQ) and BGRA→NV12 (D3D11 video processor) | `Lookback.Capture` | Downscale **done**, WARP-tested. `Nv12Converter` written, builds, not yet run |
| Media Foundation hardware encode (async MFT, NVENC/AMF/QSV, software fallback), AAC MFT | `Lookback.Encode` (Windows) | `MfVideoEncoder` **written, builds, never run**. AAC, CFR ticker and tests still to do |
| WASAPI loopback, process loopback (Win10 20348+ only), mic track | `Lookback.Audio` (Windows) | Planned |
| Recorder wiring, control commands on `/events` | `Lookback.Recorder` | Planned; contract additions to be agreed with the Electron thread first |
| Present-hook DLL (C++, D3D11 keyed mutex, D3D12 shared fence) and an injector limited to our own testbench apps | `native/hook` | Planned (M6 scope) |

## Data flow

```
capture backend (WGC | DXGI dup | hook) ── ID3D11Texture2D + QPC
   ├─ analysis tap: compute downscale 384x216 → staging (3-deep, no stall) → VideoFrame → LiveAnalyzer → signals.video
   └─ encode path:  video processor → NV12 (CFR ticker at tier fps) → MF encoder MFT ── packets
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

Where it stopped: `MfVideoEncoder` compiles but has not been run anywhere. It
enumerates encoder MFTs with `MFTEnum2` filtered by the capture adapter's LUID,
unlocks async hardware MFTs and gives them the D3D11 device manager, sets
ICodecAPI (CBR, 2 s GOP, 0 B-frames, low latency), copies each NV12 frame into
an 8-slot texture pool wrapped once in DXGI surface buffers, and emits
length-prefixed packets to an `IPacketSink`, announcing the track on the first
keyframe (falling back to `MF_MT_MPEG_SEQUENCE_HEADER` for the parameter sets).
Software H.264 runs synchronously on the same DXGI buffers. Things to verify
first on AETHER: that the MS software MFT accepts DXGI surface buffers (if not,
map a staging copy into a memory buffer), that NVENC really raises NeedInput
before the first `Encode` call, and that `Dispose` drains without hanging.

1. Encoder test on Windows: software H.264 on WARP (CI) and NVENC/AMF on
   AETHER. Encode ~120 frames into a `ReplayRing`, `ClipWriter.Mux`, and
   check with ffprobe (frame count, keyframe every 2 s, no B-frames).
2. CFR ticker: a thread at tier fps that runs `Nv12Converter` on the
   latest captured texture (repeating it when WGC sends nothing) and calls
   `Encode`. Hook it through `LiveCaptureSession.FrameTap`. Add
   `--encode <seconds> --out clip.mp4` to `lookback-capture`.
3. AAC encoder MFT (sync, 48 kHz stereo float→PCM16) fed from WASAPI.
4. WASAPI endpoint loopback (no process loopback on 19045) + mic, through
   `SampleClock` and `LiveAudioAssembler`.
5. Windows probe for the governor: `GlobalMemoryStatusEx`,
   `IOCTL_STORAGE_QUERY_PROPERTY` (seek penalty), DXGI adapters, and
   `MfVideoEncoder.List` per adapter LUID.
6. Recorder wiring (`CaptureService`, control commands on `/events`).
   Message the Electron thread before touching the contract. Ephemeral
   sessions never create the encoder, ring or store.
7. Present-hook DLL (M6).

Windows projects target `net10.0-windows10.0.22621.0` with
`SupportedOSPlatformVersion` 10.0.19041.0 and build on Linux with
`EnableWindowsTargeting`. Their tests skip off Windows.
