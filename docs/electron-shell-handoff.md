# Electron shell: work-in-progress handoff

Status: paused mid-scaffold (WIP, not built or tested yet). Code lives in `app/`.

## Done (written, not yet compiled)
- `package.json`, `tsconfig.*.json`: Electron 45.3.4, TypeScript, node:test. Main/preload are CommonJS; renderer pages are ES modules.
- `src/shared/api.d.ts`: types for the `window.lookback` and `window.lookbackViewer` preload APIs.
- `src/main/handshake.ts`: parses `LOOKBACK_READY {pipe, ws, token, ingest?, calibrate?}` and `LOOKBACK_PROFILER <url>`; loopback-only URL checks; token never logged.
- `src/main/recorder.ts`: spawns the recorder, reads the handshake, ready timeout, stderr log tail, stop with grace then kill.
- `src/main/locate.ts`: finds the recorder/testbench (settings, `LOOKBACK_RECORDER`/`LOOKBACK_TESTBENCH`, packaged `resources/`, repo `bin/Release|Debug/net10.0`).
- `src/main/settings.ts`: sanitized settings in userData, atomic save.
- `src/main/embeds.ts`: YouTube/Twitch link parsing to official-embed ids.
- `src/main/ingest.ts`: client for the calibration thread's `/ingest` WebSocket (video frames only, 384x216 BGRA, in memory).
- `src/main/viewer-server.ts`: loopback `http://localhost:PORT` host for the viewer page (embeds need a real origin; Twitch `parent=localhost`).
- `src/main/static-files.ts`, `src/main/browse.ts`, `src/main/security.ts`: app:// file resolution and CSPs, `lookback-testbench browse` runner, sandbox/navigation/permission hardening.

## Contracts agreed with other threads
- Live capture thread: shell consumes the existing stdout handshake unchanged; extra JSON fields are tolerated.
- Calibration thread: ready JSON gains `ingest` (ws) and `calibrate` (http) on the same token. Panel URL = `calibrate + "#token=" + token`. Ingest: text start `{"type":"start","platform","videoRef","profile"?,"width"?,"height"?}` (omit profile when blank), binary `u8 kind=1, i64 LE micros, u16 w, u16 h, BGRA`, text `{"type":"stop"}`. Video only; no system loopback audio.

## Next
1. `src/main/main.ts`: app:// protocol (hosts `lookback` = renderer, `browse` = generated browser page), main window, WebContentsViews for the profiler and dataset browser, IPC with sender checks, viewer window (in-memory partition `lookback-viewer`), frame subscription to ingest, calibrate panel docked in the viewer, `--smoke` mode.
2. `src/preload/preload.ts`, `src/preload/viewer-preload.ts` (self-contained, type-only imports).
3. Renderer: overview, live profiler, datasets, clips (candidates/QA/saved from the stream), settings; viewer page with YouTube IFrame API / Twitch Embed.
4. Unit tests in `src/test/` (handshake, embeds, settings, static paths, ingest encoding, recorder with a fake child).
5. CI job for `app/`, wiki page, then build and smoke-test on AETHER and open the PR.

## Update from the calibration thread (branch claude/project-thread-cj6xs2)
- The recorder serves `/ingest` and `/calibrate` by default (`Recorder:OfficialPlayer=true`). The ready JSON's `ingest` URL has no token: the shell appends `?token=` (as `ingest.ts` already does). `calibrate` gets `#token=` appended.
- Receiver: `src/Lookback.Calibration/CalibrationHost.cs`; sample client: `tests/Lookback.Calibration.Tests/IngestTests.cs`.
- Pass `--Recorder:ProfilesDirectory=<checkout>/profiles` from the shell so the panel's Save works.
