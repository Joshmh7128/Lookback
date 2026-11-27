# Event schema

Source: `proto/lookback/v1/events.proto`. Generated into the
`Lookback.Contracts.V1` namespace.

## Envelope

Every message on the stream, on the pipe, on the socket and in the log is an
`Envelope`.

| Field | Type | Meaning |
|---|---|---|
| `qpc` | fixed64 | When the event happened, in raw QPC ticks |
| `session_id` | string | Which recording session it belongs to; also the log file name |
| `topic` | string | Routing key, such as `signals.video` (see [Topics](#topics)) |
| `source_id` | string | The producer, such as `signals.video.motion` |
| `seq` | uint64 | Per-producer counter starting at 1; a gap means events were dropped |
| `payload` | oneof | Exactly one of the messages below |

`fixed64` is used for timestamps because QPC values are large: a varint would
take 9 to 10 bytes, a fixed64 always takes 8 and encodes faster.

```mermaid
classDiagram
    class Envelope {
        fixed64 qpc
        string session_id
        string topic
        string source_id
        uint64 seq
        oneof payload
    }
    Envelope --> SessionStarted
    Envelope --> SessionEnded
    Envelope --> GameLifecycle
    Envelope --> SignalSchema
    Envelope --> SignalFrame
    Envelope --> ClipCandidate
    Envelope --> QaReport
    Envelope --> ClipSaved
    Envelope --> GameEvent
    Envelope --> ProfileDetected
    QaReport --> QaCheck
    SignalFrame ..> SignalSchema : schema_id
    class ProfileDetected {
        string profile_id
        repeated string path
        ProfileLevel level
        DetectionMethod method
        float confidence
        fixed64 at_qpc
        repeated string evidence
    }
    class GameEvent {
        string name
        string game_profile
        fixed64 at_qpc
        fixed64 window_start_qpc
        fixed64 window_end_qpc
        float confidence
        float weight
        repeated string evidence
    }

    class SessionStarted {
        SourceKind source_kind
        Retention retention
        string permission_ref
        string title
        fixed64 qpc_frequency
        string game_profile
    }
    class GameLifecycle {
        Kind kind
        uint32 pid
        string exe
        GraphicsApi api
        string engine
        uint64 hwnd
        uint64 adapter_luid
        float confidence
    }
    class SignalSchema {
        uint32 schema_id
        SignalFamily family
        string[] names
    }
    class SignalFrame {
        uint32 schema_id
        fixed64 window_start_qpc
        fixed64 window_end_qpc
        float[] values
    }
    class ClipCandidate {
        string candidate_id
        fixed64 start_qpc
        fixed64 end_qpc
        fixed64 peak_qpc
        float heuristic_score
        optional float model_score
        float highlight_probability
        string[] reasons
    }
    class QaCheck {
        string name
        bool passed
        float value
        string detail
    }
```

## Payloads

**`SessionStarted`** opens every session and is always the first record in a
session log. `source_kind` and `retention` drive [Data Retention](Data-Retention).
`qpc_frequency` makes the log's timestamps interpretable anywhere.

**`SessionEnded`** closes a session, with a reason.

**`GameLifecycle`** reports detection: detected, focused, unfocused, exited.
`api` and `engine` come from the loaded-module probe; `hwnd` and
`adapter_luid` tell capture which window and GPU to use.

**`SignalSchema` and `SignalFrame`** carry features. A producer publishes its
schema once (`schema_id` plus an ordered list of names, such as
`["motion", "luma_mean", "flash"]`), then sends frames with only a
`schema_id` and a packed `float` array. At 30 frames per second, repeating
names in every frame would multiply the size of the stream several times
over; this keeps a frame to a few bytes per value. Each frame covers a window
(`window_start_qpc` to `window_end_qpc`) because features such as audio
loudness describe a span, not an instant.

**`GameEvent`** (field 18 of the envelope) is a named game moment, such as
an Overwatch `elimination` or `ultimate_used`, recognised by a
[game profile](Game-Profiles)'s rules from the game-agnostic signals.
`at_qpc` is the moment; `window_start_qpc` to `window_end_qpc` is the footage
worth keeping around it; `confidence` is how sure the rule is (base plus
boosts); `weight` is how much the moment matters to the clipper; `evidence`
lists, in words, the conditions and boosts that made it fire, so a person
can see why.

**`ProfileDetected`** (field 19) says which profile is active: the game, a
sub-game beneath it (a Roblox experience) or a mode. `path` lists the tree
from the game down. `method` says how it was decided: the executable, a hint
(launch arguments or the client's own log), a visual fingerprint, or chosen
by hand. It is published on `game.profile` at the start and whenever the
active profile changes, so the clipper and the viewer always know which
rules applied when.

**`ClipCandidate`** is a proposed clip: its window, the peak inside it,
the heuristic score, an optional model score (`optional` so "not scored yet"
differs from 0), the calibrated `highlight_probability`, and human-readable
`reasons`. The [clipper](Clipper) publishes them, fusing profile events with
peaks of the excitement curve; its `heuristic_score` is 0..1
(1 − e^(−raw)) and `highlight_probability` equals it until M4 calibrates a
model. A profile runner on its own (no clipper) still publishes event-only
candidates whose `heuristic_score` is the unbounded raw sum.

**`QaReport`** lists the QA checks run on a candidate, each with a pass flag,
a measured value and detail. The clipper publishes one right after each
candidate; v1 checks `length`, `black`, `frozen` and `silence`.

**`ClipSaved`** records a clip written to disk.

## Enums

| Enum | Values |
|---|---|
| `SourceKind` | `LIVE_GAME`, `FILE`, `OFFICIAL_PLAYER` |
| `Retention` | `EPHEMERAL`, `OWNED`, `PERMISSIONED`, `OPEN_LICENSE` |
| `GraphicsApi` | `D3D11`, `D3D12`, `VULKAN`, `OPENGL` |
| `ProfileLevel` | `GAME`, `SUBGAME`, `MODE` |
| `DetectionMethod` | `EXECUTABLE`, `HINT`, `VISUAL`, `MANUAL` |
| `SignalFamily` | `VIDEO`, `AUDIO`, `INPUT`, `RULES` (a profile's rule margins, for viewers), `CLIPPER` (5: the clipper's excitement curve, for viewers) |

Every enum starts with an `_UNSPECIFIED = 0` value, as proto3 requires, so a
missing field can be told apart from a real choice. `RetentionRules` rejects
`RETENTION_UNSPECIFIED` outright.

## Topics

Defined in `src/Lookback.Contracts/Topics.cs`.

| Topic | Payload |
|---|---|
| `session` | `SessionStarted`, `SessionEnded` |
| `game.lifecycle` | `GameLifecycle` |
| `signals.video`, `signals.audio`, `signals.input` | `SignalSchema`, `SignalFrame` |
| `signals.rules` | `SignalSchema`, `SignalFrame`: one margin per profile condition, published by the profile runner |
| `signals.clipper` | `SignalSchema` (schema id 4), `SignalFrame` every 100 ms: the excitement curve, its thresholds and each component's z-score |
| `game.event` | `GameEvent` |
| `game.profile` | `ProfileDetected` |
| `clip.candidate` | `ClipCandidate` from the clipper (source `clipper`); a profile runner without a clipper publishes event-only ones (source `profile.<game>.candidates`) |
| `qa.report` | `QaReport`, right after its candidate (source `clipper`) |
| `clip.saved` | `ClipSaved` |

Subscriptions use patterns: an exact name, `*` for everything, or a prefix
ending in `.*`. `Topics.Matches("signals.*", "signals.video")` is true;
`Topics.Matches("signals.*", "signalsx.video")` is false because the dot is
part of the prefix.

## Evolving the schema

Field numbers are permanent. Add new fields with new numbers; never renumber
or reuse one. Breaking changes go in a new package, `lookback.v2`.
