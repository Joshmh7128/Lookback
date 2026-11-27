# Live capture (M3)

Live capture turns a running game into two streams: small frames for the
signal pipeline (the analysis tap), and encoded video for the RAM replay ring
and the disk store (the encode path). Both run on one D3D11 device on the
adapter that owns the captured output, and every timestamp is raw QPC.
Design notes, decisions and the build order live in
[`docs/capture-handoff.md`](https://github.com/Joshmh7128/Lookback/blob/main/docs/capture-handoff.md);
this page explains what runs today.

## The path

```mermaid
flowchart LR
    SRC["Capture source<br/>WGC · DXGI duplication"] -- "BGRA texture + QPC<br/>(irregular)" --> TAP{{LiveCaptureSession}}
    TAP -- "≤ 30 fps" --> DS["GpuDownscaler<br/>384x216 compute"] --> LA[LiveAnalyzer] --> BUS((bus))
    TAP -- FrameTap --> NV["Nv12Converter<br/>video processor, encode size"]
    NV -- latest frame --> TK["EncodeTicker<br/>CFR clock at tier fps"]
    TK -- "NV12 + tick QPC" --> ENC["MfVideoEncoder<br/>NVENC · AMF · QSV · software"]
    ENC -- "length-prefixed packets" --> RING[ReplayRing] --> CLIP["ClipWriter<br/>fMP4, no re-encode"]
```

| Stage | Type | What it does |
|---|---|---|
| Capture | `WgcCaptureSource`, `DxgiDuplicationSource` | Frames as GPU textures with their present/compose QPC. WGC only sends a frame when the window changes |
| Analysis tap | `GpuDownscaler` → `LiveAnalyzer` | Letterboxed 384x216 BGRA, read back through a 3-deep staging ring, thinned to 30 fps |
| Convert | `Nv12Converter` | D3D11 video processor: scale to the encode size, BT.709 limited range, letterboxed. Runs on the capture thread for every frame and keeps the latest result |
| Clock | `EncodeTicker` | A high-resolution waitable timer at the tier's fps encodes the latest converted frame at every tick, repeating it when nothing new arrived |
| Encode | `MfVideoEncoder` | Media Foundation encoder MFT chosen by the capture adapter's LUID; async with the DXGI device manager for hardware, sync for the software H.264 fallback. CBR, 2 s GOP, no B-frames, low latency |
| Keep | `ReplayRing`, `SegmentStore`, `ClipWriter` | Packets in native memory, 2 s fMP4 segments on disk, clips cut on a keyframe |

### Why a constant-rate clock

Players and editors expect constant frame rate, and the capture sources do
not provide it: WGC sends nothing while a window is static and bursts when it
changes, and DXGI follows the monitor's refresh. The ticker decouples the two.
Each tick's PTS is its *scheduled* time (`origin + n / fps`, computed from
the origin every tick so it cannot drift), not the time the thread woke up,
so output timing is exact even when the scheduler is late. A tick that is
already a full period late is skipped and counted rather than encoded in a
burst; a busy encoder (its 8-slot input pool full) is counted as `busy`.
The first tick is the first captured frame's QPC, so the recording starts on a
real frame and opens with an IDR.

### Encoder selection

`MFTEnum2` lists hardware encoder MFTs filtered by `MFT_ENUM_ADAPTER_LUID`,
so on a machine with two GPUs (AETHER drives monitors from the RTX card and
the iGPU) the encoder is always on the adapter that owns the captured output
and frames never cross adapters. Hardware MFTs are unlocked for async use and
given the D3D11 device manager; NV12 textures go in as DXGI surface buffers
without leaving the GPU. If no hardware MFT accepts the settings, H.264 falls
back to the Microsoft software MFT, which reads the same surface buffers.
HEVC has no software fallback. AV1 is not offered until the muxer writes `av1C`.

### Ephemeral sessions

An official-player session never creates the converter, ticker, encoder,
ring or store: the encode path is only attached when
`RetentionRules.CanPersist` allows it, so nothing exists that could reach
disk. See [Data Retention](Data-Retention).

## The bench: `lookback-capture`

```powershell
dotnet build Lookback.slnx -c Release
$cap = "src\Lookback.Capture.Cli\bin\Release\net10.0-windows10.0.22621.0\lookback-capture.exe"
& $cap --list
& $cap --monitor 0 --seconds 20 --profiler
& $cap --monitor 0 --seconds 12 --encode 8 --out clip.mp4 --codec hevc --fps 60 --size 1920x1080 --mbps 20
```

`--encode <s> --out <file>` runs the encode path beside the analysis tap and,
at the end, writes the last `s` seconds from the ring as an MP4 (starting on
the keyframe at or before that point, so it can be up to one GOP longer).
`--software` forces the software encoder. With `--official-player` the
request is refused before capture starts.

Measured on AETHER (Windows 10 19045, RTX 4080 SUPER, 2560x1440 at 144 Hz),
monitor 0 with WGC, HEVC 1920x1080 at 60 fps, 20 Mbps:

```
capturing monitor \\.\DISPLAY1 with wgc on NVIDIA GeForce RTX 4080 SUPER; persist: True
encoding Hevc 1920x1080@60 20 Mbps with NVIDIA HEVC Encoder MFT (hardware)
  5.5s  capture  143.9 fps  analysis  29.8 fps  dropped 0  encode  60.5 fps  repeated   0  skipped 0  busy 0
...
ticker EncodeStats { Captured = 1742, Ticks = 726, Encoded = 726, Repeated = 0, Skipped = 0, EncoderBusy = 0, PacketsOut = 726 }
```

ffprobe on the 8 s clip: `hevc 1920x1080`, 486 frames, average frame rate
60.000, keyframes at exactly 0, 2, 4, 6 and 8 s, `has_b_frames=0`, and
`ffmpeg -v error -f null` decodes it with no errors.

On the iGPU (monitor 2, `AMD Radeon(TM) Graphics`), AMF H.264 and AMF HEVC
at 1080p60 also run live and decode cleanly.

When ticks and captures both stop, the bench prints `STALLED:` with each
thread's phase (`EncodeTicker.Phase`), and the watchdog acts as described
below. Bench flags: `--stall-ms` (watchdog threshold), `--isolate` (encoder on
its own device), `--no-blocklist`.

## Tests

`tests/Lookback.Capture.Tests` (Windows only; skips elsewhere):

| Test | Proves |
|---|---|
| `EncoderTests.Software_h264_on_warp_round_trips_through_the_muxer` | The software H.264 MFT on WARP (CI) takes NV12 DXGI surface buffers; 120 frames through `ReplayRing` + `ClipWriter` come back from ffprobe as 120 frames, keyframes at 0 and 2 s, no B-frames, no decode errors |
| `EncoderTests.Software_h264_on_the_gpu_reads_converter_output` | The same with `Nv12Converter` output on a real adapter |
| `EncoderTests.Hardware_encoder_round_trips_through_the_muxer` (H.264, HEVC) | On every display adapter, the hardware MFT for its LUID (NVENC on the RTX, AMF on the iGPU on AETHER): every frame in comes out, `Dispose` drains in under 3 s, and the clip checks out as above |
| `EncodeTickerTests.Irregular_capture_becomes_constant_rate_packets` | Frames at 10 fps, then a burst, then nothing, still give one packet per tick with constant spacing, repeats counted, nothing skipped or busy |
| `DownscalerTests.*` | The analysis downscale on WARP: BGRA order, 16:9 fill, 4:3 pillarbox like FileSource |

WARP has no video processor, so the CI encoder case uploads NV12 built on
the CPU; the converter is covered on real adapters.

## No deadlocks: one device lock, a watchdog, fail-fast

### The deadlock that was

Live capture used to stall within seconds, intermittently, on NVIDIA and
AMD alike (AMF HEVC hit it most). Native stacks from a hung process (cdb on
AETHER) showed a lock-order inversion:

```
WGC capture thread:  dcomp!CFlipConsumer::ProcessUpdateContent   holds  D3D11 device lock
                     → our frame callback                         waits  GpuDevice.Lock (managed)
encode ticker:       holds GpuDevice.Lock
                     → ID3D11DeviceContext::CopyResource          waits  D3D11 device lock
```

WGC raises `FrameArrived` from inside DirectComposition's flip processing,
which already holds the device's multithread lock. Our own managed lock was
taken second on that thread and first on the encode thread. Whether the two
threads met was down to timing, which is why it came and went, and why a busy
machine or a slower encoder made it likelier. Moving the encoder to its own
device did not help at the time, because the handoff still took the capture
device's lock the same way.

**Fix:** `GpuDevice` has exactly one lock, D3D11's own recursive multithread
lock (`ID3D11Multithread::Enter`, exposed as `using (gpu.Enter())`). There is
no second lock to order, and the capture callback simply re-enters the lock
DirectComposition already holds. After the fix, on AETHER, 1080p60 for 12 s:
NVENC HEVC 3/3, AMF HEVC 3/3, AMF H.264 1/1, and the opt-in separate-device
mode on both GPUs: 0 stalls, 0 encoder swaps, every clip decodes.

### The harness that keeps it from coming back

Every thread on the path reports a heartbeat (`Beat`: which step, since when)
to a `StallWatchdog` (`Lookback.Core.Diagnostics`), which never blocks or
interrupts the stuck thread:

| Watch | Stalled when | Action |
|---|---|---|
| Encode ticker step | one `Encode` call runs longer than `StallAfter` (2 s) | swap encoders |
| Encoder output | ticks are due but no packet comes out for `StallAfter` | swap encoders |
| Capture thread | the frame callback runs longer than 2 × `StallAfter` | fail fast |

A swap abandons the stuck encoder (released on a reaper thread only once no
call is inside it, otherwise leaked) and starts the next one in the fallback
chain on a fresh thread, on the same tick grid: hardware MFTs for the codec →
hardware H.264 → software H.264. Creating the replacement is itself bounded
(a driver can hang there too). The stalled encoder goes into
`%LOCALAPPDATA%\Lookback\encoder-blocklist.json`, keyed by adapter vendor,
device and driver version, so the next run starts further down the chain and
a driver update gets a fresh chance. When the chain is exhausted, after
`MaxRecoveries` swaps, or when the capture thread is wedged, `Fatal` writes the
reason and every thread's phase to stderr and ends the process
(`Environment.FailFast`): the recorder closes instead of hanging, and the shell
restarts it. `Dispose` never waits more than a few seconds on an encoder.

`EncodeTickerFaultTests` inject encoders that hang inside a call, accept
frames but never output, or refuse all input, and check the swap, the tick
grid, the blocklist, the fatal path and a prompt `Dispose`.

## Known issues

- **AMF HEVC ignores the bitrate.** It produced about 3x the CBR target in
  `EncoderTests` (4 Mbps asked, ~12 Mbps out); AMF H.264 and NVENC hold it.
- **GOPs count frames, not time.** A tick the encoder had no room for
  (`busy`) shifts every later IDR by one frame (seen once on AMF H.264 at
  60 fps: keyframes at 2.017 s). Segments and clips cut on whatever keyframes
  exist, so this costs clip-start precision only; forcing IDRs from the ticker
  at GOP-aligned ticks would pin them to 2 s.

## Not yet

- WASAPI loopback, mic and the AAC encoder; the governor's Windows probe;
  wiring into the recorder with control commands on `/events`. See the
  handoff's next steps.
- HDR sources reach the encoder as if SDR (the analysis tap handles them
  correctly); the encode tone-map comes with M6.
- Governor tier changes (a new encoder and a new segment run) are designed
  but not wired.
- Output samples come back from Media Foundation as SharpGen wrapper objects:
  a small managed allocation per packet on the encoder's event thread. Not
  measured yet.
