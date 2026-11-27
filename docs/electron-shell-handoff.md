# Electron shell: handoff

Status: **landed** (branch `shell/electron-viewer`). The shell builds, runs and
is tested on AETHER (Windows 10 19045); the official-player viewer and its
`/ingest` sender are verified end to end against `Lookback.Recorder` with a
real YouTube video and a live Twitch channel. Full description:
[docs/wiki/Desktop-Shell.md](wiki/Desktop-Shell.md).

## What landed

- `src/main/main.ts`: `app://` protocol (`lookback` = renderer, `browse` = the
  generated dataset browser), main window, embedded views for the profiler and
  dataset browser, IPC with sender checks, recorder lifecycle, `--smoke`,
  `--smoke-viewer`, `--smoke-candidates=N`, `--viewer-link=<url>`,
  `LOOKBACK_USER_DATA`.
- `src/main/stream.ts` + `src/main/library.ts`: the main process holds the
  `/events` socket (reconnect with backoff) and builds the library: sessions,
  timeline marks, clip candidates, QA, saved clips, ratings. The renderer
  gets snapshots over IPC (at most 4 per second) and opens no sockets.
- `src/main/viewer.ts`: viewer `BaseWindow` with the player view (in-memory
  partition `lookback-viewer`) and the calibration panel docked beside it
  (in-memory partition `lookback-pages`); frame subscription → crop to the
  player → 384x216 BGRA → 30 fps → kind-1 messages; start on play, stop on
  end/close/source or profile change.
- `src/main/frames.ts` (throttle, crop math), `src/main/profiles.ts` (profile
  ids for the picker), `recorder.ts` (`note`, `reportFailure`), `locate.ts`
  (`--Recorder:ProfilesDirectory`, profiles/datasets folders), `ingest.ts`
  (start message exactly `{type, platform, videoRef, profile?}`; injectable
  socket; capture timestamps).
- Preloads: `preload.ts` (`window.lookback`), `viewer-preload.ts`
  (`window.lookbackViewer`).
- Renderer: overview, library (filter, sort, rate, reveal saved clips),
  timeline (canvas lanes, live window), profiler, datasets, settings. Viewer
  page with the YouTube IFrame Player API and the Twitch embed; a synthetic
  test-pattern page for the smoke run.
- Tests (`npm test`, 54): handshake, recorder (fake child), ingest, frames,
  library, stream, files (settings, static paths, locate, profiles, viewer
  server), and integration against the real recorder.
- CI job `app` (Linux and Windows): typecheck and tests; on Windows also builds
  the recorder, runs the integration tests and both smoke runs.
- `tsconfig.main.json` moved to `module`/`moduleResolution` `node16`
  (TypeScript 6 deprecates `node10`).

## Verified on AETHER

- `npm run smoke`: recorder ready, stream open, window rendered.
- `npm run smoke:viewer` (synthetic player): about 160 frames in 5 s, none
  dropped, ephemeral "YouTube embedded player" session with as many signal
  frames, panel "158 frames analysed". Pixel probe: BGRA order and the crop at
  1.5x display scale are right.
- `--smoke-viewer --viewer-link=https://www.youtube.com/watch?v=M7lc1UVf-VE`:
  156 frames sent, panel "153 frames analysed".
- `--smoke-viewer --viewer-link=https://www.twitch.tv/<live channel>`: 151
  frames sent, panel "144 frames analysed".
- Closing the main window stops the recorder.

## Contracts

- `LOOKBACK_READY` and the `/ingest` wire format are used unchanged.
- The main process's WebSocket (Node's) sends no Origin; the bridge accepts
  it on the token.

## Open items

- **Ratings persistence.** Proposed to the live-capture thread: client
  commands on `/events`, e.g. `{"type":"rate","sessionId","candidateId","rating":1|-1|0}`,
  persisted by the recorder behind `RetentionRules` (refused for ephemeral
  sessions) and echoed on the bus. Until then ratings are in memory.
- **Orphaned recorder.** If the shell is killed rather than quit, the
  recorder keeps running. A recorder option to exit when its parent exits
  would close it.
- **Packaging.** `locate.ts` looks for `resources/recorder`, but nothing builds
  an installer yet.
- **Audio in calibration** needs player-only audio, which the embeds do not
  expose.
