# Architecture

This page shows the whole system, including the parts that later milestones
build. Parts that exist today are marked with their milestone (**M0**, **M1**,
**M2**, **M3**); unmarked parts are still planned. For the current state of
every component (built, in progress, planned), see the [System Map](System-Map).

## Processes

Native work runs outside Electron, so a driver reset, an encoder crash or a
fault in the capture path never takes down the UI, and the recorder can run
headless.

```mermaid
flowchart LR
    subgraph Game["Game process"]
        G[Game renderer]
    end

    subgraph TestApp["Our test app (never a game)"]
        TA[Test renderer]
        H["native/hook<br/>(C++, M6)"]
    end

    subgraph Recorder["Lookback.Recorder (C#)"]
        DET[Detection<br/>M2]
        CAP[Capture backends<br/>WGC · DXGI dup · hook · FileSource]
        GPU[GPU downscale + convert<br/>M3]
        ENC[Hardware encode<br/>M3]
        AUD[WASAPI loopback + mic<br/>M3]
        AAC[AAC encode<br/>M3]
        RING[RAM replay ring<br/>M3]
        DISK[Disk session store<br/>M3]
        GOV[Resource governor<br/>M3]
        BUS(("Event bus<br/>M0"))
        PIPES[Pipe server<br/>M0]
        WS[WebSocket bridge<br/>M0]
    end

    subgraph Signals["Lookback.Signals (C#)"]
        VS[Video signals<br/>M1]
        AS[Audio signals<br/>M1]
        IS[Input signals]
        LOG[Session log<br/>M0]
    end

    subgraph Clipper["Lookback.Clipper (C#)"]
        NORM[Per-game normalisation<br/>M2]
        CAND[Candidates<br/>M2]
        SCORE[ONNX scoring<br/>M4]
        QA[Per-candidate QA<br/>M2 v1]
    end

    subgraph Shell["Electron shell"]
        UI[Library · timeline · rating · viewer]
    end

    TA -- present --> H -- shared texture + fence --> CAP
    G -. WGC window capture .-> CAP
    DET --> CAP --> GPU --> ENC --> RING --> DISK
    AUD --> AAC --> RING
    AUD -- 10 ms hops --> AS
    GOV -. budgets .-> RING & DISK & ENC
    GPU -- small frames --> VS
    VS & AS & IS --> BUS
    BUS --> PIPES & WS & LOG
    PIPES <--> Signals & Clipper
    WS --> UI
    NORM --> CAND --> SCORE --> QA --> BUS
```

| Process | Language | Owns |
|---|---|---|
| `Lookback.Recorder` | C# | Detection, capture, GPU work, encode, replay ring, disk store, audio, governor, the bus and its transports |
| `Lookback.Signals` | C# | Video, audio and input feature extraction; session feature log |
| `Lookback.Clipper` | C# | Normalisation, excitement curve, candidates, scoring, QA ([Clipper](Clipper); runs beside the profile runner today, in the testbench and later in the recorder) |
| `Lookback.Testbench` | C# CLI | Runs footage files through Signals and Clipper |
| `native/hook`, `native/testapps` | C++ | Present hook for D3D11/D3D12, the only native code because it lives inside the rendering process; and the two test apps that are its only permitted targets. Games are captured with WGC. See [Present Hook](Present-Hook) |
| `app/` | Electron + TypeScript | Library, live timeline, ratings, official-player viewer ([Desktop Shell](Desktop-Shell)) |
| `ml/` | Python | Offline training and ONNX export |

## One clock

Every timestamp is a raw QPC tick. QPC is what `Stopwatch` reads on Windows,
and it is also the clock behind DXGI present statistics and Windows.Graphics.Capture
frame times, so captured frames need no conversion. Audio sample positions are
rescaled to QPC with `QpcTime.Rescale`. `SessionStarted.qpc_frequency` records
the counter frequency so a log can be read on any machine.

```mermaid
flowchart LR
    F[Frame present time<br/>QPC] --> E[Envelope.qpc]
    A[Audio sample index<br/>48 kHz] -- QpcTime.Rescale --> E
    I[Raw Input arrival<br/>QPC] --> E
    E --> T[One timeline]
```

## How an event flows (M0 path)

```mermaid
sequenceDiagram
    participant P as Producer (signal extractor)
    participant F as EnvelopeFactory
    participant B as InProcessEventBus
    participant Q as Subscriber queues
    participant Pi as Pipe connection
    participant W as WebSocket client
    participant L as SessionLogRecorder

    P->>F: Create("signals.video")
    F-->>P: Envelope{qpc, session, source, seq}
    P->>B: Publish(envelope)
    Note over B: lock-free: reads a snapshot<br/>of subscribers, never blocks
    B->>Q: TryWrite to each matching queue<br/>(drop oldest if full)
    Q-->>Pi: framed protobuf to other processes
    Q-->>W: JSON text frame to Electron
    Q-->>L: append to .lbklog if retention allows
```

## From a game starting to capture (M2)

```mermaid
sequenceDiagram
    participant OS as Windows (process list, windows)
    participant D as DetectionRunner (1 s poll)
    participant B as Event bus
    participant R as Recorder
    participant C as Capture
    OS-->>D: Overwatch.exe pid 4242 with a 2560×1440 borderless window
    Note over D: matched by executable,<br/>window held 3 s
    D->>B: game.lifecycle DETECTED (profile, hwnd, policy)
    D->>R: Changed(DETECTED, GameInstance)
    R->>C: CaptureTarget for the window, external capture (policy)
    D->>B: FOCUSED / UNFOCUSED / WINDOW_CHANGED
    OS-->>D: process gone for 5 s
    D->>B: game.lifecycle EXITED
    D->>R: Changed(EXITED)
    R->>C: stop, end the session
```

Detection reads only what the OS shows about other programs: no memory
reads, no injection, no hooks. Its profile match carries the capture policy
the recorder must honour. See [Game Detection](Game-Detection).

## Live audio (M3)

WASAPI loopback (and the mic) is stamped in QPC through `SampleClock`,
resampled to 48 kHz stereo at the measured device rate, then encoded to AAC
into the replay ring and cut into 10 ms hops for the audio signals.
Official-player sessions never get system loopback or the encoder. Details
in [Live Capture](Live-Capture#live-audio).

## Recording tiers (M3)

| Tier | Contents | Default retention | Size at 1080p60 HEVC 20 Mbps |
|---|---|---|---|
| RAM replay ring | Encoded packets, indexed by keyframe | ~2 minutes, sized by the governor | ~150 MB per minute |
| Disk session store | Whole session as 2 s fMP4 segments | Whole session within a disk budget; unused segments deleted 24 h after the session | ~9 GB per hour |
| Feature log | Signals and embeddings | Outlives the video | ~80 MB per hour |

Clips are cut by remuxing packets from a keyframe, with no re-encode.

The [resource governor](Resource-Governor) sizes the ring, the disk budget
and the quality tier from a probe of the PC (RAM, the store volume's free
space and SSD/HDD, the GPU that scans out the captured monitor and its
hardware encoders), then steps quality down under pressure. The probe is in
`Lookback.Governor.Windows`; the mapping and policy in `Lookback.Governor`
are platform-neutral and tested without the hardware.

## Auto-clipping pipeline (M2 to M4)

```mermaid
flowchart LR
    S[Signals<br/>video · audio · input] --> N["Per-game z-scores<br/>M2 ✓"]
    S --> GP[Game profile rules<br/>game.event ✓]
    GP --> M
    N --> C["Excitement curve<br/>M2 ✓"]
    C --> P["Peak detection<br/>with hysteresis ✓"]
    P --> W["Windows [t−pre, t+post] ✓"]
    W --> M["Merge overlaps,<br/>dedupe with events ✓"]
    M --> SC["Score<br/>heuristic ✓ → ONNX (M4)"]
    SC --> QA["QA gates<br/>v1 ✓"]
    QA --> OUT[clip.candidate + qa.report]
    OUT --> R[Ratings in the app]
    R --> T[Training] --> SC
```

Clipper v1 (M2) builds everything up to the QA gates; see [Clipper](Clipper).
Scoring is a bounded heuristic until the learned scorer (M4) replaces it.

Game profiles sit beside the excitement curve, not in place of it. A profile
names moments in one game (an Overwatch elimination) from the same
game-agnostic signals measured inside that game's HUD regions; the clipper
fuses those named moments with peaks of the generic curve into one candidate
set. See [Game Profiles](Game-Profiles) and [Clipper](Clipper).

## Milestones

```mermaid
flowchart LR
    M0[M0 foundation ✓] --> M1[M1 signals on footage ✓] --> GPR[Game profiles] --> M2["M2 clipper v1 ✓<br/>+ rating UI, viewer"] --> M3[M3 live capture]
    M3 --> M4[M4 learned scoring] --> M5[M5 quality reports] --> M6[M6 present hook + HDR] --> M7[M7 memory module]
```
