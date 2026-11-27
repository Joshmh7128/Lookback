# Handoff: official-player calibration

Branch: `claude/project-thread-cj6xs2`. Kept current so the work can be picked up locally.

## Goal

Josh watches a YouTube or Twitch video in the official embedded player (the
Electron shell's viewer window, built in the shell thread under `app/`). The
recorder analyses the playback live, proposes HUD region rectangles and rule
thresholds for the chosen profile, Josh confirms, and only those numbers are
written into `profiles/**.json`. No frames, audio, signal series or video ids
are ever written.

## Wire format agreed with the shell (ingest)

`ws://127.0.0.1:PORT/ingest?token=T` (address in `LOOKBACK_READY` as `ingest`,
panel at `calibrate` + `#token=T`).

- Text `{"type":"start","platform":"youtube"|"twitch","videoRef":"...","profile":"cs2"}` (`profile` omitted when blank).
- Binary, little-endian: `byte kind` (1 video, 2 audio), `int64` microseconds since start,
  then video `uint16 w, uint16 h, BGRA8` (384x216 preferred) or audio `uint32 rate, float32 mono`.
- Text `{"type":"stop"}`.
- First cut from the shell is video only.

### Shell side: done (branch `shell/electron-viewer`)

All six steps landed in `app/` (`src/main/viewer.ts`, `ingest.ts`, `frames.ts`):
handshake fields read, start on play (profile omitted when blank), frame
subscription cropped to the player and scaled to 384x216 BGRA at 30 fps,
stop on end/close/source change, panel docked at `calibrate#token=`, no
loopback audio, in-memory partition. Verified end to end on AETHER with a
YouTube video and a live Twitch channel (about 150 frames in 5 s, none
dropped, the panel reporting them analysed). See
[docs/wiki/Desktop-Shell.md](wiki/Desktop-Shell.md).

`tests/Lookback.Calibration.Tests/IngestTests.cs` and
`app/src/test/recorder-integration.test.ts` are working reference clients.

## Done

- `RetentionRules.ValidateDerived` + `DerivedWriteKind.ConfirmedProfileParameters`, with tests.
- `WebSocketBridgeOptions.Sockets` (extra token/origin-checked sockets) and `SocketUrl`.
- `src/Lookback.Calibration`: `OfficialPlayerSession` (ephemeral, no session log),
  `CalibrationHost` (`/ingest`, `/calibrate`, `/calibrate/ws`), `CalibrationProbe`
  (marks, static HUD), `ThresholdTuner`, `ProfileTextPatcher` + `CalibrationWriter`
  (edits number tokens in place, validates the tree before writing), `calibrate.html`.
- Recorder serves them by default; `Recorder:ProfilesDirectory` enables Save;
  `LOOKBACK_READY` gains `ingest` and `calibrate`.
- Docs: wiki `Official-Player-Calibration.md`, Data-Retention, CLAUDE.md rule.
- Flagged items: Siege exe names match Ubisoft Support; League map ids 11/12/22/30
  match Riot's map list. Marvel Rivals exe still unverified (needs an install).
- README merge-conflict block fixed.
- 300 tests pass on Linux.

## Next

- Windows run on AETHER (build + tests).
- ~~End-to-end with the Electron viewer~~ done (see above).
- Audio in calibration once the shell can provide player-only audio.
