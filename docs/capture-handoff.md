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
| Governor policy (probe → ring/disk budget + quality tier, hysteresis) | `Lookback.Governor` | Next |
| Audio sample clock (WASAPI device position → QPC, drift correction) | `Lookback.Media` | Next |
| Live analyzer (push frames/audio from capture threads into the signal banks) | `Lookback.Signals` | Next |
| WGC capture (CsWinRT), DXGI duplication fallback | `Lookback.Capture` (Windows) | Planned |
| GPU downscale to 384x216 BGRA (compute shader, letterboxed like FileSource) and BGRA→NV12 (D3D11 video processor) | `Lookback.Capture` | Planned |
| Media Foundation hardware encode (async MFT, NVENC/AMF/QSV, software fallback), AAC MFT | `Lookback.Encode` (Windows) | Planned |
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

## Next steps, in order

1. `SegmentStore` + `SessionRecording`: the writer pulls from the ring by
   sequence, cuts at the first video keyframe once 2 s have passed, and
   waits for audio to pass the cut (or 500 ms). It writes `init-<run>.mp4`
   and `seg-<n>.m4s`, plus `segments.jsonl` entries with QPC ranges. It
   evicts store-wide, oldest first, and never touches pinned ranges or the
   open segment. Clip cutting from disk uses `Mp4FragmentReader`.
2. `CacheSweeper`: delete unpinned segments once `ended + grace` has passed
   (default 24 h). A session with no end marker (a crash) uses the last write
   time. Saved clips are never deleted.
3. `Lookback.Governor`: a pure policy over a `MachineProfile` (RAM, volumes
   with SSD/HDD, GPUs with encoder support, display). The ring gets
   min(5% of available RAM, 1 GB). Disk gets min(user cap, a share of free
   space) above a free-space floor. Pick the tier that holds a useful
   session length. At runtime, step down on memory/disk/encoder pressure
   with hysteresis, effective at the next keyframe. The Windows probe uses
   `GlobalMemoryStatusEx`, `IOCTL_STORAGE_QUERY_PROPERTY` (seek penalty),
   DXGI and `MFTEnumEx`.
4. Windows projects target `net10.0-windows10.0.19041.0` (Windows 10 support)
   and build on Linux CI with `EnableWindowsTargeting`. Their tests run on the
   Windows leg only (WARP for the downscale shader, the software H.264 MFT
   for encode).
