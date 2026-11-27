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
- Tests (`npm test`, 72): handshake, recorder (fake child), ingest, frames, control, footage, media,
  library, stream, files (settings, static paths, locate, profiles, viewer
  server), and integration against the real recorder.
- CI job `app` (Linux and Windows): typecheck and tests; on Windows also builds
  the recorder, runs the integration tests and both smoke runs.
- `tsconfig.main.json` moved to `module`/`moduleResolution` `node16`
  (TypeScript 6 deprecates `node10`).

## Rating flow (added after the first review)

- 1–5 stars for content ("is this a highlight"), keys 1–5 / 0 / ↑↓ / Space,
  star filters and sort, "stored" vs "in memory" per rating.
- "Open footage…" (`footage.ts`): the recorder's `analyze` command first, else
  `lookback-testbench live <file> --pipe <recorder pipe> --speed 8 --title <path>`;
  sessions are linked to files by title.
- Preview (`media.ts`): `lookback-media://` with byte ranges, registered files
  only; the candidate window loops; saved clips play whole.
- Control channel (`control.ts`, `stream.ts`): `rate`/`analyze` with a 2 s ack,
  `clip.rating` envelopes applied. Contract recorded in
  [Desktop-Shell.md](wiki/Desktop-Shell.md#control-commands-on-events-agreed-recorder-side-pending).
- Verified: `--smoke --smoke-footage=sample.mp4 --smoke-profile=overwatch` opened
  the file through the fallback, listed its candidate, rated it 5 with the key,
  and the preview loaded the file (1280 wide, playing inside the window).

## Recorder follow-up (#26)

- Parent exit and the datasets folder passed to the recorder; supervision with
  restart backoff and a crash banner.
- Acks: stored ratings, refusal messages, analyze linking by `sessionId`;
  the testbench fallback only for recorders that never ack.
- Live recordings (#28): Save per candidate (button and <kbd>S</kbd>), the
  saved clip previewed, `recorder.storage` warnings (banner near the clips
  budget, blocking notice when full) and both budgets in Settings.
- Tested against recorders built from main, #25 and #28 (90/90 with #28).
- Not done: live save end to end needs a running game; the recorder's newer
  options (`StoreDirectory`, budgets, `Microphone`, `DetectionSettings`) are
  not in Settings yet.

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

- **Recorder side of the control commands** (`rate`, `analyze`, `save`, acks,
  `ClipRating` on field 20): agreed with the live-capture thread, which builds
  it with the recorder wiring step. Until then ratings are in memory and
  footage goes through the testbench.
- ~~Orphaned recorder~~: closed. The shell passes `--Recorder:ParentProcessId`
  and supervises the recorder (restart with backoff, crash banner); see
  [Desktop-Shell.md](wiki/Desktop-Shell.md#supervision).
- **Packaging.** `locate.ts` looks for `resources/recorder`, but nothing builds
  an installer yet.
- **Audio in calibration** needs player-only audio, which the embeds do not
  expose.
