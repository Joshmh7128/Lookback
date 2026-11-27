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

## Done

- `RetentionRules.ValidateDerived` + `DerivedWriteKind.ConfirmedProfileParameters`, with tests.
- `WebSocketBridgeOptions.Sockets` (extra token/origin-checked sockets) and `SocketUrl`.

## Next

- `src/Lookback.Calibration`: ingest handler, ephemeral official-player session
  (signal bank + profile runner, no session log), calibration probe (marks,
  per-cell response, static mask, threshold search), profile text patcher
  (edits numbers in place, keeps comments), `/calibrate` panel.
- Recorder: `Recorder:ProfilesDirectory`, serve ingest + panel, extra ready fields.
- Docs: Data-Retention wiki, CLAUDE.md exception for confirmed profile parameters.
- Flagged: Siege and Marvel Rivals exe names, League map ids.
- README has a leftover merge-conflict block on main (Build and test section).
