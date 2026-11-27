# Architecture

This page shows the whole system, including the parts that later milestones
build. Parts that exist today are marked **M0** or **M1**.

## Processes

Native work runs outside Electron, so a driver reset, an encoder crash or a
fault in the capture path never takes down the UI, and the recorder can run
headless.

```mermaid
flowchart LR
    subgraph Game["Game process"]
        G[Game renderer]
        H["native/hook<br/>(C++, opt-in, M6)"]
    end

    subgraph Recorder["Lookback.Recorder (C#)"]
        DET[Detection<br/>M3]
        CAP[Capture backends<br/>WGC · DXGI dup · hook · FileSource]
        GPU[GPU downscale + convert<br/>M3]
        ENC[Hardware encode<br/>M3]
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
        NORM[Per-game normalisation]
        CAND[Candidates]
        SCORE[ONNX scoring]
        QA[Per-candidate QA]
    end

    subgraph Shell["Electron shell"]
        UI[Library · timeline · rating · viewer]
    end

    G -- present --> H -- shared texture + fence --> CAP
    G -. WGC window capture .-> CAP
    DET --> CAP --> GPU --> ENC --> RING --> DISK
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
| `Lookback.Clipper` | C# | Normalisation, excitement curve, candidates, scoring, QA |
| `Lookback.Testbench` | C# CLI | Runs footage files through Signals and Clipper |
| `native/hook` | C++ | Present hook for D3D11/D3D12; the only native code, because it lives inside the game |
| `app/` | Electron + TypeScript | Library, live timeline, ratings, official-player viewer |
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

## Recording tiers (M3)

| Tier | Contents | Default retention | Size at 1080p60 HEVC 20 Mbps |
|---|---|---|---|
| RAM replay ring | Encoded packets, indexed by keyframe | ~2 minutes, sized by the governor | ~150 MB per minute |
| Disk session store | Whole session as 2 s fMP4 segments | Whole session within a disk budget; unused segments deleted 24 h after the session | ~9 GB per hour |
| Feature log | Signals and embeddings | Outlives the video | ~80 MB per hour |

Clips are cut by remuxing packets from a keyframe, with no re-encode.

## Auto-clipping pipeline (M2 to M4)

```mermaid
flowchart LR
    S[Signals<br/>video · audio · input] --> N[Per-game z-scores]
    N --> C[Excitement curve]
    C --> P[Peak detection<br/>with hysteresis]
    P --> W["Windows [t−pre, t+post]"]
    W --> M[Merge overlaps]
    M --> SC[Score<br/>heuristic → ONNX]
    SC --> QA[QA gates]
    QA --> OUT[clip.candidate + qa.report]
    OUT --> R[Ratings in the app]
    R --> T[Training] --> SC
```

## Milestones

```mermaid
flowchart LR
    M0[M0 foundation ✓] --> M1[M1 signals on footage] --> M2[M2 clipper + viewer] --> M3[M3 live capture]
    M3 --> M4[M4 learned scoring] --> M5[M5 quality reports] --> M6[M6 present hook + HDR] --> M7[M7 memory module]
```
