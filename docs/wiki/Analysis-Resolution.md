# Analysis resolution

Every signal reads a small analysis frame, 384x216 by default
([Media Library](Media-Library#the-analysis-format)). The question this page
answers: would analysing at full resolution find better moments, and would
skipping the downscale make live capture cheaper?

**Short answer: no on both.** The downscale is about 3% of the GPU; analysing
at the native 2560x1440 doubles the recorder's GPU load, costs 24 to 26 ms of
CPU per analysed frame (three quarters of a core at 30 fps, with dropped frames
even on an idle desktop), and finds 97 to 99% the same events, with slightly
noisier signals. The default stays 384x216. The size is now a setting, so the
experiment can be repeated on other games and footage.

## Settings

| Where | Setting | Default |
|---|---|---|
| Testbench | `analyze` / `live` `--analysis <WxH>` or `--analysis native` | `384x216` |
| Recorder | `Recorder:AnalysisSize` (`WxH`; an invalid value is logged and ignored) | unset = `384x216` |
| Capture bench | `lookback-capture --analysis <WxH> --regions <profile>` | `384x216`, no regions |
| Code | `AnalysisFormat.Parse("768x432")`, `LiveSessionOptions.Analysis`, `new RegionSignal(regions, format)` | `AnalysisFormat.Default` |

Sizes must be even, between 64x36 and 7680x4320. Every signal is normalised
(means, fractions, histogram distances), so a picture with no detail finer than
384x216 gives the same values at any size; `AnalysisSizeTests` checks this for
every signal. What a larger size adds is fine detail and its noise. Profile
thresholds were set at 384x216, and the official-player calibration still
proposes regions at 384x216 (rectangles are normalised, so they apply at any
size).

## How it was measured

On AETHER (RTX 4080 SUPER, 7800X3D, three 2560x1440 monitors at 144 Hz), with
the Overwatch profile (7 HUD regions) on Josh's own Overwatch recordings from
the store (2560x1440 HEVC at 60 fps, remuxed from their segments; owned, not
committed):

- **A**: 7 min 14 s (13 028 analysed frames) at 384x216, 768x432, 1280x720,
  1920x1080 and 2560x1440 (native).
- **B**: 10 min 37 s (19 122 frames) at 384x216, 768x432 and 1280x720.

Footage runs used `lookback-testbench analyze --analysis <size>`; the signal
logs were compared per rule and per signal. Live runs used
`lookback-capture --monitor 1 --analysis <size> --regions overwatch` for 25 s
each, with no game running, a small video window keeping WGC delivering frames
at 144 fps, with and without a 2560x1440 60 fps HEVC encode at 40 Mbps.
GPU use is the Windows GPU-engine counter of the capture process alone.

## Cost

### Footage: signal bank CPU time per analysed frame

| Size | Pixels | A: mean / worst (ms) | B: mean / worst (ms) | A: analysis speed |
|---|---|---|---|---|
| 384x216 | 1x | **0.60** / 9.6 | **0.81** / 7.4 | 8.9x real time |
| 768x432 | 4x | 2.40 / 10.3 | 3.04 / 15.0 | 8.0x |
| 1280x720 | 11x | 6.76 / 28.5 | 5.60 / 15.8 | 3.8x |
| 1920x1080 | 25x | 12.67 / 36.1 | | 1.7x |
| 2560x1440 | 44x | 24.78 / 105.2 | | 0.8x (slower than real time) |

The cost follows the pixel count, as expected of linear passes. The budget is
33 ms per frame at 30 fps, on one below-normal-priority thread that shares the
CPU with the game.

### Live: capture tap, readback, signals, GPU

| Size | Readback per frame | Capture-thread tap (readback) | Signals mean / worst | Dropped, with encode | GPU 3D, analysis only | GPU 3D, with encode |
|---|---|---|---|---|---|---|
| 384x216 | 0.3 MB | **0.15 ms** (0.07) | **0.63 / 10.5 ms** | 0 of 762 | **3.3%** | **14.9%** |
| 768x432 | 1.3 MB | 0.28 ms (0.20) | 2.27 / 8.1 ms | 0 of 763 | | |
| 1280x720 | 3.7 MB | 0.50 ms (0.41) | 5.93 / 17.4 ms | 0 of 762 | 7.5% | 17.2% |
| 1920x1080 | 8.3 MB | 0.90 ms (0.81) | 13.90 / 29.8 ms (16.0 / 128 with encode) | 2 of 762 | | |
| 2560x1440 | 14.7 MB | 1.53 ms (1.45) | 23.98 / 51.9 ms (25.7 / 135 with encode) | 6 of 757 | 24.1% | 30.1% |

The video-encode engine ran at 31 to 33% in every encode run, independent of
the analysis size. With a game running, the analysis worker gets less CPU,
so the drops at 1080p and native would be far more frequent than on this
idle desktop; the tap's readback also runs on the capture thread, which the
watchdog guards.

### Where the GPU time goes

At 384x216 the analysis tap (capture, downscale, readback) is about 3% of the
GPU's 3D engine. Adding the encode path takes it to 15%: that is the NV12
conversion, which runs for every captured frame (144 a second on a 144 Hz
monitor, though the encoder takes 60), plus NVENC on its own engine. Skipping
the downscale would not remove that work; it would add a 14.7 MB readback per
analysed frame and double the 3D load.

## Quality

There are no labels yet, so the runs were compared with each other and the
differences checked by looking at the frames.

### Events per rule

Events at each size, and how many of them match a 384x216 event within 0.5 s:

| Rule | A: 384 | A: 768 | A: 1280 | A: 1080p | A: native | B: 384 | B: 768 | B: 1280 |
|---|---|---|---|---|---|---|---|---|
| `elimination` | 332 | 339 (328) | 341 (328) | 341 (328) | 339 (326) | 533 | 539 (532) | 543 (530) |
| `kill_feed_activity` | 215 | 220 (214) | 221 (214) | 221 (214) | 223 (214) | 384 | 392 (384) | 395 (383) |
| `ultimate_used` | 47 | 47 (47) | 47 (47) | 47 (47) | 47 (47) | 73 | 73 (68) | 74 (65) |
| `death` | 16 | 16 (16) | 16 (16) | 16 (16) | 16 (16) | 17 | 17 (17) | 17 (17) |
| `low_health` | 12 | 12 (12) | 12 (12) | 12 (12) | 12 (12) | 58 | 58 (58) | 58 (58) |
| `round_result` | 8 | 8 (8) | 8 (8) | 8 (8) | 8 (8) | 11 | 11 (11) | 11 (11) |
| `multikill` | 162 | 163 (142) | 166 (145) | 166 (145) | 165 (145) | 262 | 265 (245) | 267 (224) |
| `triple_kill` | 105 | 108 (79) | 108 (80) | 108 (80) | 108 (84) | 173 | 175 (149) | 177 (134) |
| `team_fight` | 68 | 68 (35) | 69 (47) | 69 (47) | 69 (42) | 123 | 127 (109) | 128 (72) |

The rules over region and whole-frame signals fire on the same moments at every
size; larger sizes add 1 to 3% more `elimination` and `kill_feed_activity`
events. The composites (`multikill`, `triple_kill`, `team_fight`) shift more
because one extra member event moves where a streak starts, not because the
moments differ. Clip candidates: A had 15 at 384x216 and 17 to 19 at larger
sizes, B 39 and 36 to 43, mostly the same windows re-split.

The 4 `kill_feed_activity` events only 1280x720 found in A: two are real new
kill-feed lines (117 s and 226 s) that 384x216 did not report within 0.5 s, one
is the hero-select screen, and one is the scene behind the translucent feed.
A gain of two real events against two false ones in seven minutes.

### Signal noise

Every signal at 768x432 and above correlates at 0.996 or better with its
384x216 version. Region activity (mean absolute luma change) rises 8 to 25%
at 1280x720, and its spikes stand out *less* from the noise: the ratio of the
99.5th percentile above the median to the spread (robust z) falls:

| Signal | A: 384 | A: 1280 | A: native | B: 384 | B: 1280 |
|---|---|---|---|---|---|
| `region.kill_feed.activity` | 12.9 | 11.6 | 11.5 | 8.1 | 7.2 |
| `region.elimination_notice.activity` | 8.5 | 7.0 | 6.8 | 5.9 | 5.1 |
| `region.ultimate.activity` | 17.1 | 15.5 | 15.0 | 19.9 | 18.3 |
| `region.banner.activity` | 6.9 | 5.5 | 5.2 | 4.4 | 3.7 |
| `region.objective.activity` | 16.6 | 13.5 | 13.1 | 12.1 | 10.4 |
| `motion.energy` | 4.2 | 3.7 | 3.6 | 2.9 | 2.7 |
| `scene.cut` | 25.1 | 25.9 | 25.5 | 14.3 | 14.6 |

This is what averaging predicts. Each 384x216 pixel is the mean of about 44
source pixels, which cancels encoder noise and grain; the HUD changes the rules
look for (a kill-feed line appearing, a meter emptying) are tens of source
pixels across and survive the average. At full size the noise comes back and
the changes are no larger.

### Why the events are wrong when they are

The rules over-fire at every size: about one `elimination` a second in fights.
In ten sampled 384x216 `kill_feed_activity` events, two were new kill-feed
lines; the rest were menus (4), the scene behind the translucent feed (3) and
a line expiring (1). The `elimination_notice` region (below the crosshair)
covers the weapon and the scene, not the elimination text. None of these
depend on resolution: they need calibrated regions
([Official-Player Calibration](Official-Player-Calibration)), menu states, and
rules that tell a new line from a fading one. Separately, the generic HUD
mask (`hud.coverage`) learned nothing on either recording at any size, most
likely because its 10 s warm-up falls on animated menus.

## Recommendation

- **Keep 384x216** for every signal. Nothing in the data favours a larger size,
  and the costs grow with the pixel count. The defaults are unchanged.
- **Do not build a native-resolution HUD region path now.** Region signals
  measure brightness, colour and change over an area; fine detail adds noise to
  those, not information. Resolution starts to matter only for a signal that
  reads detail the average destroys (whose name is on a kill-feed line, which
  icon is lit). If such a signal is added, crop just its rectangles on the GPU
  at native size (the Overwatch kill feed is 5% of the frame, about 0.8 MB per
  read at 1440p) rather than raising the whole analysis frame.
- **To use less GPU**, convert to NV12 only the frames the encoder will take
  (60 a second) instead of every captured frame (up to the monitor's refresh).
- **For better events**, calibrate the Overwatch regions and gate rules on menu
  states; both matter far more than resolution.

## Repeating the measurements

```powershell
dotnet build Lookback.slnx -c Release
$tb = "src\Lookback.Testbench\bin\Release\net10.0\lookback-testbench.exe"
& $tb analyze <footage.mp4> --profile overwatch --analysis 1280x720 --out out\1280
$cap = "src\Lookback.Capture.Cli\bin\Release\net10.0-windows10.0.22621.0\lookback-capture.exe"
& $cap --monitor 1 --seconds 25 --analysis 1280x720 --regions overwatch
```

`analyze` prints `analysis 1280x720: video signals 6.76 ms/frame (max 28.5 ms)`.
The capture bench prints, every second and in total, the capture-thread time
of the tap and its readback and the worker's signal time per analysed frame.
Footage in the store is fMP4 segments: concatenate `init-001.mp4` and the
`seg-*.m4s` files in order and remux them (`ffmpeg -i joined.fmp4 -c copy
out.mp4`) to get a file the testbench can read.
