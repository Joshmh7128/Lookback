import { app, BrowserWindow, dialog, ipcMain, protocol, session, shell, WebContentsView, type IpcMainEvent, type IpcMainInvokeEvent } from "electron";
import { existsSync, mkdirSync, rmSync, statSync, writeFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { spawn, type ChildProcess } from "node:child_process";
import { dirname, join } from "node:path";
import type { BackfillPlan, BrowseState, CandidateView, EmbeddedView, FootageJob, LibrarySnapshot, PreviewSource, RecorderState, Settings, ShellPaths, StorageListing, StorageState, StreamState, ViewBounds, ViewerReport } from "../shared/api";
import { browseArgs, runBrowse } from "./browse";
import { backfillMessage, backfillPlanMessage, HISTORY_TIMEOUT_MS, historyEnvelopes, historyMessage, newRequestId, parseStorage, PREVIEW_TIMEOUT_MS, previewMessage, previewPath, rateMessage, refusal, SAVE_TIMEOUT_MS, saveMessage, storageDeleteMessage, storageKeepMessage, storageMessage, validFootageRequest } from "./control";
import { parseEmbedSource, validateEmbedSource } from "./embeds";
import { FootageManager } from "./footage";
import { validRect } from "./frames";
import { validPosition } from "./ingest";
import { eventsUrl } from "./handshake";
import { Library, validRating } from "./library";
import { datasetsFolder, defaultRatingsFolder, locateTool, modelFolder, profilesFolder, RECORDER, recorderArgs, TESTBENCH, type LocateContext } from "./locate";
import { MEDIA_SCHEME, MediaRegistry, serveMedia, VIDEO_EXTENSIONS } from "./media";
import { isEmptyEdits, isProfileId, profileSaveArgs, profileViewArgs, runProfileTool, sanitizeEdits } from "./profile-editor";
import { listProfiles } from "./profiles";
import { RecorderHost } from "./recorder";
import { RecorderSupervisor } from "./supervisor";
import { allowNavigation, hardenApp, lockPermissions } from "./security";
import { loadSettings, saveSettings, sanitizeSettings } from "./settings";
import { BROWSE_CSP, contentType, resolveStatic, SHELL_CSP } from "./static-files";
import { StreamClient } from "./stream";
import { ViewerController } from "./viewer";
import { ViewerServer } from "./viewer-server";

// The Lookback desktop shell's main process. It spawns the recorder, reads
// its handshake, keeps the clip library from the /events stream, hosts the
// recorder's own pages (profiler, calibration panel) and the dataset
// browser, and runs the official-player viewer.
//
// What the shell writes to disk: its settings file and the generated dataset
// browser page (profile and manifest data only), both in userData. No session
// data: no frames, audio, video ids, events or ratings.

const SMOKE = process.argv.includes("--smoke") || process.argv.includes("--smoke-viewer") || process.argv.includes("--smoke-demo");
/**
 * The front-to-back demo, end to end on the real recorder: the stand-in game
 * (flashes and tones) is detected and recorded, a real candidate appears, its
 * cached clip plays from the Library at once, rating it keeps the clip, and
 * after a recorder restart the session and the clip are still there.
 */
const SMOKE_DEMO = process.argv.includes("--smoke-demo");
const SMOKE_VIEWER = process.argv.includes("--smoke-viewer");
/** Open the viewer on this link at startup; with --smoke-viewer, play it instead of the synthetic test pattern. */
const VIEWER_LINK = process.argv.find((a) => a.startsWith("--viewer-link="))?.slice("--viewer-link=".length) ?? null;
/** With --smoke, also wait for this many clip candidates (fed by the testbench) before passing. */
const argValue = (name: string): string | null => process.argv.find((a) => a.startsWith(`--${name}=`))?.slice(name.length + 3) ?? null;
/** With --smoke, analyse this file through "Open footage" (no dialog), then rate and preview a candidate in the window. */
const SMOKE_FOOTAGE = argValue("smoke-footage");
const SMOKE_PROFILE = argValue("smoke-profile") ?? "generic";
/** With --smoke, kill the recorder once it is up and pass when the supervisor has it ready again on a new handshake. */
const SMOKE_SUPERVISE = process.argv.includes("--smoke-supervise");
/** With --smoke, feed synthetic storage reports (near the clips budget, then full) and check the window's warnings. */
const SMOKE_STORAGE = process.argv.includes("--smoke-storage");
/** With --smoke, let the recorder record the repo's stand-in game window and drive Save on its live session. */
const SMOKE_LIVE = process.argv.includes("--smoke-live");
/** With --smoke, also wait for this many clip candidates before rating one through the window. */
const SMOKE_CANDIDATES = Number(argValue("smoke-candidates") ?? (SMOKE_FOOTAGE ? 1 : 0)) || 0;
const SMOKE_TIMEOUT_MS = 120_000;
let demoStoreCleared = false;

// Tests and the smoke run point userData at a scratch folder.
if (process.env["LOOKBACK_USER_DATA"]) {
  app.setPath("userData", process.env["LOOKBACK_USER_DATA"]);
}

protocol.registerSchemesAsPrivileged([
  { scheme: "app", privileges: { standard: true, secure: true, supportFetchAPI: true } },
  // Candidate previews stream from the file with byte ranges.
  { scheme: MEDIA_SCHEME, privileges: { standard: true, secure: true, stream: true, supportFetchAPI: true } },
]);
hardenApp();

const appRoot = app.getAppPath();
const dist = join(appRoot, "dist");
const repoRoot = findRepoRoot(appRoot);
const locate = (): LocateContext => ({
  repoRoot,
  resourcesDir: app.isPackaged ? process.resourcesPath : null,
  platform: process.platform,
  env: process.env,
  exists: existsSync,
});

const recorder = new RecorderHost();
const stream = new StreamClient();
const library = new Library();
const media = new MediaRegistry();
const footage = new FootageManager({
  request: (id, text) => stream.request(id, text),
  pipe: () => recorder.connection?.pipe ?? null,
  testbench: (args) => locateTool(TESTBENCH, settings.testbenchPath, locate(), args),
  sessionsByTitle: (title) => library.sessionsByTitle(title),
  linked: (sessionId) => {
    if (library.setPreviewable(sessionId, true)) scheduleLibrary();
  },
  log: (line) => recorder.note(line),
});
footage.on("changed", (jobs: FootageJob[]) => send("lookback:footage", jobs));
let settings: Settings;
let mainWindow: BrowserWindow | null = null;
let embedded: { view: EmbeddedView; contents: WebContentsView; url: string } | null = null;
let viewer: ViewerController;
let browseBuiltAt: number | null = null;
let quitting = false;

function settingsFile(): string {
  return join(app.getPath("userData"), "settings.json");
}

function browseFile(): string {
  return join(app.getPath("userData"), "browse", "index.html");
}

function findRepoRoot(start: string): string | null {
  // In a checkout the app lives in <repo>/app; packaged, there is no repo.
  const parent = join(start, "..");
  return !app.isPackaged && existsSync(join(parent, "Lookback.slnx")) ? parent : null;
}

// ---------------------------------------------------------------- recorder

function startRecorder(): RecorderState {
  const ctx = locate();
  // Smoke runs keep everything the recorder writes (ratings, recordings, clips)
  // in their own userData, never in the checkout or the user's store.
  const smokeDir = SMOKE ? join(app.getPath("userData"), "smoke-recorder") : null;
  const args = recorderArgs(settings, { profilesDir: profilesFolder(settings, ctx), datasetsDir: smokeDir ? join(smokeDir, "datasets") : datasetsFolder(settings, ctx), parentPid: process.pid, modelDir: modelFolder(ctx) });
  if (smokeDir) {
    mkdirSync(smokeDir, { recursive: true });
    args.push(`--Recorder:StoreDirectory=${join(smokeDir, "store")}`);
    if (SMOKE_DEMO && !demoStoreCleared) {
      // Each demo run starts from an empty store, so what it finds is what it recorded.
      demoStoreCleared = true;
      rmSync(join(smokeDir, "store"), { recursive: true, force: true });
    }

    if (SMOKE_LIVE || SMOKE_DEMO) {
      const detection = join(smokeDir, "detection.json");
      writeFileSync(detection, JSON.stringify({ allow: [{ exe: "lookback-testwindow.exe", profile: "generic" }] }));
      args.push(`--Recorder:DetectionSettings=${detection}`);
    }
  }

  const command = locateTool(RECORDER, settings.recorderPath, ctx, args);
  if (!command) {
    recorder.reportFailure("Lookback.Recorder was not found. Build it with `dotnet build Lookback.slnx -c Release`, or set its path in Settings.");
    return recorder.state;
  }

  recorder.start(command);
  return recorder.state;
}

// Restarts the recorder after an unexpected exit (see supervisor.ts).
const supervisor = new RecorderSupervisor(recorder, () => {
  recorder.note("restarting the recorder after an unexpected exit");
  return startRecorder().status !== "failed";
});
supervisor.on("state", (state) => send("lookback:supervision", state));

recorder.on("state", (state) => {
  send("lookback:state", state);
  if (state.status !== "ready") storage = null;
  const info = recorder.connection;
  if (info) {
    stream.connect(eventsUrl(info));
  } else {
    stream.disconnect();
  }

  viewer?.recorderChanged();
  if (embedded?.view === "profiler") {
    refreshEmbedded();
  }
});
recorder.on("log", (line) => send("lookback:log", line));

// ----------------------------------------------------------------- library

let libraryTimer: NodeJS.Timeout | null = null;
let sentRevision = -1;
// The recorder's storage use (recorder.storage), every 10 s and after each save.
let storage: StorageState | null = null;
/** Preview cuts the recorder made, per session and candidate; temporary, never saved clips. */
const previews = new Map<string, string>();
stream.on("envelope", (envelope) => {
  const s = parseStorage(envelope);
  if (s) {
    storage = s;
    send("lookback:storage", s);
    return;
  }

  if (library.apply(envelope)) {
    scheduleLibrary();
    const topic = (envelope as { topic?: unknown }).topic;
    if (topic === "session") footage.link();
  }
});
stream.on("state", (state) => {
  send("lookback:stream", state);
  if (state.status === "open") void loadHistory();
});

/**
 * Recorded sessions from before this connection (an earlier run of the app
 * or the recorder): the recorder keeps each live session's own envelopes,
 * and replaying them rebuilds those sessions in the library exactly as they
 * looked live, so their clips can be watched, rated and saved. Sessions the
 * library already has are left alone.
 */
async function loadHistory(): Promise<void> {
  const known = new Set(library.snapshot().sessions.map((s) => s.id));
  const id = newRequestId();
  const envelopes = historyEnvelopes(await stream.request(id, historyMessage(id), HISTORY_TIMEOUT_MS));
  let changed = false;
  for (const e of envelopes) {
    if (!known.has(e["sessionId"] as string)) changed = library.apply(e) || changed;
  }

  if (changed) scheduleLibrary();
}

/** Files the storage page listed: the only paths it may reveal or delete. */
const storedPaths = new Set<string>();
// The message count moves without a state change; refresh it once a second.
let sentReceived = -1;
setInterval(() => {
  const state = stream.state;
  if (state.received !== sentReceived) {
    sentReceived = state.received;
    send("lookback:stream", state);
  }
}, 1000).unref();

function scheduleLibrary(): void {
  if (!libraryTimer) {
    // At most four snapshots a second, however busy the stream is.
    libraryTimer = setTimeout(() => {
      libraryTimer = null;
      if (library.revision !== sentRevision) {
        sentRevision = library.revision;
        send("lookback:library", library.snapshot());
      }
    }, 250);
  }
}

// -------------------------------------------------------------- main window

function send(channel: string, value: unknown): void {
  const contents = mainWindow?.webContents;
  if (contents && !contents.isDestroyed()) {
    contents.send(channel, value);
  }
}

function createMainWindow(): void {
  const win = new BrowserWindow({
    width: 1360,
    height: 860,
    minWidth: 900,
    minHeight: 560,
    title: "Lookback",
    backgroundColor: "#101317",
    show: false,
    webPreferences: {
      preload: join(dist, "preload", "preload.js"),
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      spellcheck: false,
    },
  });
  allowNavigation(win.webContents, (url) => url.protocol === "app:" && url.host === "lookback");
  win.once("ready-to-show", () => {
    if (!SMOKE) win.show();
  });
  win.on("resize", () => send("lookback:resized", null));
  win.on("closed", () => {
    mainWindow = null;
    embedded = null;
    if (!quitting) {
      app.quit();
    }
  });
  mainWindow = win;
  void win.loadURL("app://lookback/index.html");
}

/** Only the main window's own page may call the shell API. */
function fromMain(event: IpcMainInvokeEvent | IpcMainEvent): boolean {
  return mainWindow !== null && event.sender === mainWindow.webContents && (event.senderFrame?.url ?? "").startsWith("app://lookback/");
}

function fromViewer(event: IpcMainInvokeEvent | IpcMainEvent): boolean {
  const contents = viewer.playerContents;
  return contents !== null && event.sender === contents && event.senderFrame === contents.mainFrame;
}

function handle(channel: string, check: (e: IpcMainInvokeEvent) => boolean, fn: (...args: unknown[]) => unknown): void {
  ipcMain.handle(channel, (event, ...args: unknown[]) => {
    if (!check(event)) {
      throw new Error("not allowed");
    }

    return fn(...args);
  });
}

function on(channel: string, check: (e: IpcMainEvent) => boolean, fn: (...args: unknown[]) => void): void {
  ipcMain.on(channel, (event, ...args: unknown[]) => {
    if (check(event)) {
      fn(...args);
    }
  });
}

// ----------------------------------------------------------- embedded pages

function embeddedUrl(view: EmbeddedView): string | null {
  if (view === "profiler") {
    return recorder.profiler;
  }

  return existsSync(browseFile()) ? "app://browse/index.html" : null;
}

function showEmbedded(view: EmbeddedView | null, bounds: ViewBounds | null): boolean {
  const win = mainWindow;
  if (!win) {
    return false;
  }

  if (!view || !bounds) {
    if (embedded) {
      win.contentView.removeChildView(embedded.contents);
      embedded.contents.webContents.close();
      embedded = null;
    }

    return true;
  }

  const url = embeddedUrl(view);
  if (!url) {
    showEmbedded(null, null);
    return false;
  }

  if (!embedded || embedded.view !== view || embedded.url !== url) {
    showEmbedded(null, null);
    const contents = new WebContentsView({
      webPreferences: {
        // The recorder's pages run in the in-memory pages partition; the
        // generated browser page is the shell's own.
        session: view === "profiler" ? pagesSession() : session.defaultSession,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        spellcheck: false,
      },
    });
    const origin = new URL(url).origin;
    allowNavigation(contents.webContents, (u) => (view === "browse" ? u.protocol === "app:" && u.host === "browse" : u.origin === origin));
    win.contentView.addChildView(contents);
    void contents.webContents.loadURL(url).catch(() => undefined);
    embedded = { view, contents, url };
  }

  embedded.contents.setBounds(roundBounds(bounds));
  return true;
}

function refreshEmbedded(): void {
  if (embedded) {
    const bounds = embedded.contents.getBounds();
    showEmbedded(embedded.view, bounds);
    send("lookback:embedded", embeddedUrl(embedded?.view ?? "profiler") !== null);
  }
}

function roundBounds(b: ViewBounds): ViewBounds {
  const r = (v: number): number => Math.max(0, Math.round(v));
  return { x: r(b.x), y: r(b.y), width: r(b.width), height: r(b.height) };
}

function pagesSession(): Electron.Session {
  return session.fromPartition("lookback-pages");
}

// --------------------------------------------------------------------- IPC

function registerIpc(): void {
  handle("lookback:get-state", fromMain, () => recorder.state);
  handle("lookback:start", fromMain, () => {
    supervisor.reset();
    return startRecorder();
  });
  handle("lookback:stop", fromMain, async () => {
    supervisor.reset();
    await recorder.stop();
    return recorder.state;
  });
  handle("lookback:restart", fromMain, async () => {
    supervisor.reset();
    await recorder.stop();
    return startRecorder();
  });
  handle("lookback:get-supervision", fromMain, () => supervisor.state);
  handle("lookback:get-paths", fromMain, (): ShellPaths => {
    const datasets = datasetsFolder(settings, locate());
    return { ratings: datasets ?? defaultRatingsFolder(process.env), ratingsInCheckout: datasets !== null, profiles: profilesFolder(settings, locate()) };
  });
  handle("lookback:get-log", fromMain, () => recorder.logTail);
  handle("lookback:get-stream", fromMain, (): StreamState => stream.state);
  handle("lookback:get-library", fromMain, (): LibrarySnapshot => library.snapshot());
  handle("lookback:rate", fromMain, (sessionId, candidateId, rating) => {
    if (typeof sessionId !== "string" || typeof candidateId !== "string" || !validRating(rating)) {
      return false;
    }

    const ok = library.rate(sessionId, candidateId, rating);
    if (!ok) return false;
    scheduleLibrary();
    // Offer it to the recorder; without an ack it stays in memory only.
    const id = newRequestId();
    void stream.request(id, rateMessage(id, sessionId, candidateId, rating)).then((ack) => {
      if (!ack) return; // an older recorder: memory only
      const changed = ack.ok && ack.stored ? library.markStored(sessionId, candidateId, rating) : !ack.ok && library.markRefused(sessionId, candidateId, rating, refusal(ack));
      if (changed) scheduleLibrary();
    });
    return true;
  });
  handle("lookback:save", fromMain, (sessionId, candidateId) => {
    if (typeof sessionId !== "string" || typeof candidateId !== "string" || !library.candidateOf(sessionId, candidateId)) return false;
    if (storage?.clipsFull) {
      library.setSaving(sessionId, candidateId, false, { note: "Saved clips are at their storage limit; delete some clips first." });
      scheduleLibrary();
      return false;
    }

    library.setSaving(sessionId, candidateId, true);
    scheduleLibrary();
    const id = newRequestId();
    void stream.request(id, saveMessage(id, sessionId, candidateId), SAVE_TIMEOUT_MS).then((ack) => {
      if (!ack) {
        library.setSaving(sessionId, candidateId, false, { note: "The recorder did not answer; it may not be able to save clips yet." });
      } else if (ack.ok) {
        library.setSaving(sessionId, candidateId, false, { path: ack.path, note: null });
      } else {
        library.setSaving(sessionId, candidateId, false, { note: refusal(ack) });
      }

      scheduleLibrary();
    });
    return true;
  });
  handle("lookback:get-storage", fromMain, () => storage);
  handle("lookback:open-clips", fromMain, async () => {
    // The folder of a clip the recorder reported saving; the shell knows no other.
    const clip = library.snapshot().sessions.flatMap((s) => s.candidates).find((c) => c.savedPath && existsSync(c.savedPath));
    if (!clip?.savedPath) return false;
    return (await shell.openPath(dirname(clip.savedPath))) === "";
  });
  handle("lookback:open-footage", fromMain, async (raw) => {
    const request = validFootageRequest(raw);
    if (!request || !mainWindow) return null;
    const picked = await dialog.showOpenDialog(mainWindow, {
      title: "Open footage to analyse",
      properties: ["openFile"],
      filters: [{ name: "Video", extensions: VIDEO_EXTENSIONS }],
    });
    const path = picked.canceled ? undefined : picked.filePaths[0];
    return path ? footage.open(path, request) : null;
  });
  handle("lookback:get-footage", fromMain, () => footage.list);
  handle("lookback:profiles", fromMain, () => listProfiles(profilesFolder(settings, locate())));
  handle("lookback:preview", fromMain, async (sessionId, candidateId): Promise<PreviewSource | null> => {
    if (typeof sessionId !== "string" || typeof candidateId !== "string") return null;
    const s = library.sessionOf(sessionId);
    const c = library.candidateOf(sessionId, candidateId);
    if (!s || !c || s.qpcFrequency <= 0) return null;
    const length = Math.max(0, (c.endQpc - c.startQpc) / s.qpcFrequency);
    if (c.savedPath && existsSync(c.savedPath)) {
      return { url: media.urlFor(c.savedPath), start: 0, end: length, kind: "clip" };
    }

    if (s.sourceKind === "live_game") {
      // A live recording has no file of its own: the recorder cuts a temporary
      // preview from its footage (not a saved clip; Save keeps one).
      const key = `${sessionId}\u0000${candidateId}`;
      let path = previews.get(key) ?? null;
      if (!path || !existsSync(path)) {
        const id = newRequestId();
        path = previewPath(await stream.request(id, previewMessage(id, sessionId, candidateId), PREVIEW_TIMEOUT_MS));
        if (path && existsSync(path)) previews.set(key, path);
      }

      return path && existsSync(path) ? { url: media.urlFor(path), start: 0, end: length, kind: "clip" } : null;
    }

    const file = footage.pathFor(sessionId);
    if (!file || !existsSync(file)) return null;
    // A file session's clock starts at the file's first frame.
    const start = Math.max(0, (c.startQpc - s.startQpc) / s.qpcFrequency);
    return { url: media.urlFor(file), start, end: start + length, kind: "footage" };
  });
  handle("lookback:forget", fromMain, (sessionId) => {
    const ok = typeof sessionId === "string" && library.forget(sessionId);
    if (ok) scheduleLibrary();
    return ok;
  });
  handle("lookback:show-view", fromMain, (view, bounds) => {
    const v = view === "profiler" || view === "browse" ? view : null;
    return showEmbedded(v, v ? validRect(bounds) : null);
  });
  handle("lookback:browse-state", fromMain, (): BrowseState => {
    const file = browseFile();
    return { available: existsSync(file), builtAt: browseBuiltAt ?? (existsSync(file) ? statSync(file).mtimeMs : null) };
  });
  handle("lookback:build-browse", fromMain, async () => {
    const datasets = datasetsFolder(settings, locate()) ?? "";
    const command = locateTool(TESTBENCH, settings.testbenchPath, locate(), browseArgs(browseFile(), datasets));
    if (!command) {
      return { ok: false, message: "lookback-testbench was not found. Build the solution or set its path in Settings." };
    }

    const result = await runBrowse(command);
    if (result.ok) {
      browseBuiltAt = Date.now();
      refreshEmbedded();
    }

    return result;
  });
  // Profiles tab: the testbench reads and writes the checkout's profiles/ (only that folder).
  const profileTool = async (args: (dir: string) => string[], stdin: string | null) => {
    const dir = profilesFolder(settings, locate());
    if (!dir) {
      return { ok: false, message: "No profiles folder: run Lookback from a checkout, or set the profiles folder in Settings." };
    }

    const command = locateTool(TESTBENCH, settings.testbenchPath, locate(), args(dir));
    if (!command) {
      return { ok: false, message: "lookback-testbench was not found. Build the solution (launch.bat does) or set its path in Settings." };
    }

    return runProfileTool(command, stdin);
  };
  handle("lookback:profile-view", fromMain, (id) => {
    if (id !== null && !isProfileId(id)) return { ok: false, message: "Not a profile id." };
    return profileTool((dir) => profileViewArgs(dir, id), null);
  });
  handle("lookback:profile-save", fromMain, async (id, raw) => {
    const edits = sanitizeEdits(raw);
    if (!isProfileId(id) || !edits) return { ok: false, message: "The edit is malformed." };
    if (isEmptyEdits(edits)) return { ok: false, message: "Nothing to save." };
    const result = await profileTool((dir) => profileSaveArgs(dir, id), JSON.stringify(edits));
    if (result.ok) {
      recorder.note(`Profiles: saved ${id} (${(result.files ?? []).join(", ") || "no change"}); applies from the next recording`);
    }

    return result;
  });
  handle("lookback:get-settings", fromMain, () => settings);
  handle("lookback:save-settings", fromMain, (value) => {
    settings = saveSettings(settingsFile(), sanitizeSettings(value));
    return settings;
  });
  handle("lookback:open-viewer", fromMain, async (link) => {
    const source = typeof link === "string" && link.trim() ? parseEmbedSource(link) : null;
    if (typeof link === "string" && link.trim() && !source) {
      return false;
    }

    await viewer.open(source);
    return true;
  });
  handle("lookback:storage-list", fromMain, async (): Promise<StorageListing | null> => {
    const id = newRequestId();
    const ack = await stream.request(id, storageMessage(id), HISTORY_TIMEOUT_MS);
    if (!ack?.ok || typeof ack.data !== "object" || ack.data === null) return null;
    const listing = ack.data as StorageListing;
    storedPaths.clear();
    for (const f of [...(listing.clips ?? []), ...(listing.cache ?? [])]) {
      if (typeof f.path === "string") storedPaths.add(f.path);
    }

    return listing;
  });
  handle("lookback:storage-delete", fromMain, async (kind, target): Promise<string | null> => {
    if (kind !== "session" && kind !== "clip" && kind !== "cache") return "Unknown kind.";
    const t = typeof target === "object" && target !== null ? (target as Record<string, unknown>) : {};
    const sessionId = typeof t["sessionId"] === "string" ? t["sessionId"] : undefined;
    const path = typeof t["path"] === "string" ? t["path"] : undefined;
    if (path && !storedPaths.has(path)) return "That file is not in the store listing; refresh and try again.";
    const id = newRequestId();
    const ack = await stream.request(id, storageDeleteMessage(id, kind, { sessionId, path }), HISTORY_TIMEOUT_MS);
    if (!ack) return "The recorder did not answer.";
    if (!ack.ok) return refusal(ack);
    if (kind === "session" && sessionId && library.forget(sessionId)) scheduleLibrary();
    if (path) storedPaths.delete(path);
    for (const [key, p] of previews) {
      if (!existsSync(p)) previews.delete(key);
    }

    return null;
  });
  // Rating backfill: the plan is cached so a start knows whether to ask for the footage file.
  let backfillPlan: BackfillPlan | null = null;
  handle("lookback:backfill-plan", fromMain, async (): Promise<BackfillPlan | null> => {
    const id = newRequestId();
    const ack = await stream.request(id, backfillPlanMessage(id), HISTORY_TIMEOUT_MS);
    backfillPlan = ack?.ok && typeof ack.data === "object" && ack.data !== null ? (ack.data as BackfillPlan) : null;
    return backfillPlan;
  });
  handle("lookback:backfill", fromMain, async (sessionId): Promise<string | null> => {
    if (typeof sessionId !== "string") return "Unknown session.";
    const row = backfillPlan?.sessions.find((s) => s.sessionId === sessionId);
    if (!row) return "Refresh the list and try again.";
    let path: string | undefined;
    if (row.sourceKind !== "live-game") {
      if (!mainWindow) return "";
      const picked = await dialog.showOpenDialog(mainWindow, {
        title: `The footage the ${row.game} ratings of ${sessionId} came from`,
        properties: ["openFile"],
        filters: [{ name: "Video", extensions: VIDEO_EXTENSIONS }],
      });
      path = picked.canceled ? undefined : picked.filePaths[0];
      if (!path) return "";
    }

    const id = newRequestId();
    const ack = await stream.request(id, backfillMessage(id, sessionId, path), HISTORY_TIMEOUT_MS);
    if (!ack) return "The recorder did not answer.";
    return ack.ok ? null : refusal(ack);
  });
  handle("lookback:storage-keep", fromMain, async (sessionId, keep): Promise<boolean> => {
    if (typeof sessionId !== "string" || typeof keep !== "boolean") return false;
    const id = newRequestId();
    return (await stream.request(id, storageKeepMessage(id, sessionId, keep), HISTORY_TIMEOUT_MS))?.ok === true;
  });
  handle("lookback:storage-reveal", fromMain, (path) => {
    if (typeof path !== "string" || !storedPaths.has(path) || !existsSync(path)) return false;
    shell.showItemInFolder(path);
    return true;
  });
  handle("lookback:reveal", fromMain, (path) => {
    // Only paths the recorder itself reported as saved clips.
    if (typeof path !== "string" || !library.isSavedClip(path) || !existsSync(path)) {
      return false;
    }

    shell.showItemInFolder(path);
    return true;
  });

  handle("viewer:parse", fromViewer, (input) => (typeof input === "string" ? parseEmbedSource(input) : null));
  handle("viewer:initial", fromViewer, () => viewer.takeInitialSource());
  handle("viewer:profiles", fromViewer, () => listProfiles(profilesFolder(settings, locate())));
  on("viewer:select", fromViewer, (source, profile) => {
    viewer.select(source === null ? null : validateEmbedSource(source), typeof profile === "string" && /^[A-Za-z0-9._-]{0,128}$/.test(profile) ? profile : "");
  });
  on("viewer:report", fromViewer, (report) => {
    const r = typeof report === "object" && report !== null ? (report as Record<string, unknown>) : {};
    const statuses = ["idle", "loading", "playing", "paused", "ended", "error"];
    const status = typeof r["status"] === "string" && statuses.includes(r["status"]) ? (r["status"] as ViewerReport["status"]) : "idle";
    viewer.report({ status, rect: validRect(r["rect"]), position: validPosition(r["position"]) });
  });
  on("viewer:toggle-panel", fromViewer, () => viewer.togglePanel());
}

// ---------------------------------------------------------------- protocol

function registerProtocol(): void {
  protocol.handle(MEDIA_SCHEME, (request) => serveMedia(request, media));
  protocol.handle("app", async (request) => {
    const url = new URL(request.url);
    let file: string | null = null;
    let csp = SHELL_CSP;
    if (url.host === "lookback") {
      file = resolveStatic(join(dist, "renderer"), url.pathname);
    } else if (url.host === "browse" && url.pathname === "/index.html") {
      file = existsSync(browseFile()) ? browseFile() : null;
      csp = BROWSE_CSP;
    }

    if (!file || !existsSync(file)) {
      return new Response("not found", { status: 404 });
    }

    const body = await readFile(file);
    return new Response(new Uint8Array(body), {
      status: 200,
      headers: { "Content-Type": contentType(file), "Content-Security-Policy": csp, "X-Content-Type-Options": "nosniff", "Cache-Control": "no-store" },
    });
  });
}

// -------------------------------------------------------------------- smoke

function runSmoke(): void {
  const started = Date.now();
  // What the main window shows, read back from its DOM.
  const rendered = async (): Promise<unknown> => {
    const contents = mainWindow?.webContents;
    if (!contents || contents.isDestroyed()) return null;
    const script = `(() => { const t = (id) => document.getElementById(id)?.textContent ?? null;
      return { summary: t("session-summary"), recorder: t("pill-recorder"), stream: t("pill-stream"), libraryRows: document.querySelectorAll("#lib-rows tr").length, timeline: t("tl-info") }; })()`;
    return contents.executeJavaScript(script, false).catch(() => null);
  };
  let finished = false;
  const finish = (ok: boolean, detail: Record<string, unknown>): void => {
    if (finished) return;
    finished = true;
    void rendered()
      .then((window) => {
        process.stdout.write(`LOOKBACK_SMOKE ${ok ? "ok" : "fail"} ${JSON.stringify({ ...detail, window })}\n`);
        process.exitCode = ok ? 0 : 1;
        return shutdown();
      })
      .finally(() => app.exit(ok ? 0 : 1));
  };

  // The live run: start the stand-in game, wait for its LIVE_GAME session,
  // then press Save in the window. That window rarely gives the clipper a
  // candidate, so the run adds one to the shell's library only; the recorder
  // does not know it and refuses the save, which proves the whole path from
  // the button to the recorder's answer on screen.
  const smokeLive = async (base: Record<string, unknown>): Promise<{ ok: boolean; detail: Record<string, unknown> }> => {
    const exe = repoRoot ? join(repoRoot, "tests", "Lookback.Detection.TestWindow", "bin", "Release", "net10.0-windows10.0.22621.0", "lookback-testwindow.exe") : "";
    if (!exe || !existsSync(exe)) return { ok: false, detail: { ...base, reason: "lookback-testwindow.exe is not built" } };
    testWindow = spawn(exe, ["Lookback smoke", "60"], { stdio: ["pipe", "ignore", "ignore"], windowsHide: false });
    const wait = async (check: () => boolean, ms: number): Promise<boolean> => {
      const end = Date.now() + ms;
      while (!check()) {
        if (Date.now() > end) return false;
        await new Promise((r) => setTimeout(r, 200));
      }

      return true;
    };
    const live = (): ReturnType<Library["snapshot"]>["sessions"][number] | undefined => library.snapshot().sessions.find((s) => s.sourceKind === "live_game");
    if (!(await wait(() => live() !== undefined, 45_000))) return { ok: false, detail: { ...base, reason: "no LIVE_GAME session for the test window", log: recorder.logTail.slice(-15) } };
    const session = live()!;
    const at = session.startQpc + 5 * (session.qpcFrequency || 1e7);
    stream.emit("envelope", {
      qpc: String(at),
      sessionId: session.id,
      topic: "clip.candidate",
      clipCandidate: { candidateId: "smoke-candidate", startQpc: String(session.startQpc), endQpc: String(at), peakQpc: String(at), heuristicScore: 0.5, reasons: ["smoke"] },
    });
    await new Promise((r) => setTimeout(r, 600));
    const contents = mainWindow!.webContents;
    const clicked: unknown = await contents.executeJavaScript(
      `(() => { document.querySelector('#tabs button[data-tab="library"]').click();
        const s = [...document.querySelectorAll("#lib-sessions button")].find((b) => b.textContent.includes(${JSON.stringify(session.title || "")}) && b.textContent.includes("live game"));
        if (s) s.click();
        return true; })()`,
      true,
    );
    await new Promise((r) => setTimeout(r, 400));
    const pressed: unknown = await contents.executeJavaScript(`(() => { const b = [...document.querySelectorAll("#lib-rows button")].find((x) => x.textContent === "Save"); if (!b) return false; b.click(); return true; })()`, true);
    const refused = await wait(() => library.candidateOf(session.id, "smoke-candidate")?.saveNote !== null && library.candidateOf(session.id, "smoke-candidate")?.saveNote !== undefined, 35_000);
    await new Promise((r) => setTimeout(r, 400));
    const shown: unknown = await contents.executeJavaScript(`(() => [...document.querySelectorAll("#lib-rows .save-note")].map((n) => n.textContent))()`, false);
    await wait(() => storage !== null, 12_000);
    const note = library.candidateOf(session.id, "smoke-candidate")?.saveNote ?? null;
    return {
      ok: clicked === true && pressed === true && refused && Array.isArray(shown) && shown.length > 0,
      detail: { ...base, session: { title: session.title, retention: session.retention, sourceKind: session.sourceKind }, saveButton: pressed, saveNote: note, shown, storage },
    };
  };

  // The demo: everything the user would do, through the window, with timings.
  const smokeDemo = async (base: Record<string, unknown>): Promise<{ ok: boolean; detail: Record<string, unknown> }> => {
    const exe = repoRoot ? join(repoRoot, "tests", "Lookback.Detection.TestWindow", "bin", "Release", "net10.0-windows10.0.22621.0", "lookback-testwindow.exe") : "";
    if (!exe || !existsSync(exe)) return { ok: false, detail: { ...base, reason: "lookback-testwindow.exe is not built" } };
    testWindow = spawn(exe, ["Lookback demo", "240", "--action"], { stdio: ["pipe", "ignore", "ignore"], windowsHide: false });
    const steps: Record<string, unknown> = {};
    const t0 = Date.now();
    const wait = async (check: () => boolean, ms: number): Promise<boolean> => {
      const end = Date.now() + ms;
      while (!check()) {
        if (Date.now() > end) return false;
        await new Promise((r) => setTimeout(r, 200));
      }

      return true;
    };
    const js = (code: string): Promise<unknown> => mainWindow!.webContents.executeJavaScript(code, true).catch(() => null);
    const fail = (reason: string): { ok: boolean; detail: Record<string, unknown> } => ({ ok: false, detail: { ...base, reason, steps, log: recorder.logTail.slice(-20) } });

    // 1. The game is detected and recorded.
    const live = (): ReturnType<Library["snapshot"]>["sessions"][number] | undefined => library.snapshot().sessions.find((s) => s.sourceKind === "live_game" && !s.ended);
    if (!(await wait(() => live() !== undefined, 45_000))) return fail("the stand-in game was never recorded");
    const session = live()!;
    steps["recordingAfterMs"] = Date.now() - t0;

    // 2. The clipper proposes a real candidate (a flash with a tone every 20 s).
    const firstCandidate = (): CandidateView | undefined => library.snapshot().sessions.find((s) => s.id === session.id)?.candidates[0];
    if (!(await wait(() => firstCandidate() !== undefined, 90_000))) return fail("no candidate within 90 s of recording");
    const candidate = firstCandidate()!;
    steps["firstCandidateAfterMs"] = Date.now() - t0;
    steps["candidate"] = { id: candidate.id, seconds: (candidate.endQpc - candidate.startQpc) / (session.qpcFrequency || 1e7), reasons: candidate.reasons };

    // 3. Select it in the Library: the cached clip plays at once.
    await new Promise((r) => setTimeout(r, 1500)); // the cache cuts it a moment after it is proposed
    await js(`document.querySelector('#tabs button[data-tab="library"]').click();
      [...document.querySelectorAll("#lib-sessions button")].find((b) => b.textContent.includes(${JSON.stringify(session.title || "")}))?.click(); true`);
    await new Promise((r) => setTimeout(r, 300));
    const clicked = Date.now();
    await js(`document.querySelector('#lib-rows tr[data-id="${candidate.id}"]')?.click(); true`);
    const playable = await (async () => {
      const end = Date.now() + 10_000;
      while (Date.now() < end) {
        const ready = await js(`(() => { const v = document.getElementById("preview-video"); return !!v && !!v.currentSrc && v.readyState >= 2; })()`);
        if (ready === true) return true;
        await new Promise((r) => setTimeout(r, 100));
      }

      return false;
    })();
    steps["previewPlayableAfterMs"] = Date.now() - clicked;
    if (!playable) return fail("the selected candidate's clip did not play within 10 s");
    const cached = [...previews.values()].some((p) => p.includes("previews"));
    steps["playedFromCache"] = cached;

    // 4. Rate it 4 stars with the keyboard: its clip is kept (moves to saved clips).
    await js(`document.dispatchEvent(new KeyboardEvent("keydown", { key: "4", bubbles: true })); true`);
    if (!(await wait(() => library.candidateOf(session.id, candidate.id)?.rating === 4 && library.candidateOf(session.id, candidate.id)?.ratingStored === true, 10_000))) return fail("the rating was not stored");
    if (!(await wait(() => !!library.candidateOf(session.id, candidate.id)?.savedPath, 20_000))) return fail("rating did not keep the clip");
    const kept = library.candidateOf(session.id, candidate.id)!.savedPath!;
    steps["keptClip"] = kept;

    // 5. Stop the game, restart the recorder: the session and its clip come back from the store.
    testWindow?.stdin?.end();
    await wait(() => library.sessionOf(session.id)?.ended === true, 30_000);
    library.forget(session.id);
    scheduleLibrary();
    supervisor.reset();
    await recorder.stop();
    startRecorder();
    const restarted = Date.now();
    if (!(await wait(() => library.sessionOf(session.id) !== null, 45_000))) return fail("the session did not come back after a recorder restart");
    steps["restoredAfterMs"] = Date.now() - restarted;
    const restored = library.candidateOf(session.id, candidate.id);
    steps["restored"] = { rating: restored?.rating ?? null, savedPath: restored?.savedPath ?? null, candidates: library.snapshot().sessions.find((s) => s.id === session.id)?.candidates.length ?? 0 };
    const id = newRequestId();
    const listing = (await stream.request(id, storageMessage(id), HISTORY_TIMEOUT_MS))?.data as StorageListing | undefined;
    steps["storage"] = listing ? { sessions: listing.sessions.length, clips: listing.clips.length, cache: listing.cache.length, cacheBytes: listing.cacheBytes } : null;
    // The window shows the session again (it gets the library at most four times a second).
    await new Promise((r) => setTimeout(r, 1000));
    const shown = await js(`document.getElementById("session-summary")?.textContent ?? ""`);
    steps["windowSummary"] = shown;
    const ok = cached && restored?.rating === 4 && restored?.savedPath === kept && (listing?.clips.some((c) => c.path === kept) ?? false) && typeof shown === "string" && !shown.startsWith("No sessions");
    return { ok, detail: { ...base, steps } };
  };

  let rendererReady = false;
  ipcMain.once("lookback:rendered", (event) => {
    if (fromMain(event)) rendererReady = true;
  });
  let viewerOpened = false;
  let rating = false;
  let footageOpened = false;
  let killed: { pid: number | null; endpoint: string | null } | null = null;
  let storageStep = 0;
  let liveStep = 0;
  let testWindow: ChildProcess | null = null;
  const timer = setInterval(() => {
    const state = recorder.state;
    // In the supervise run the exit is the point; the supervisor brings it back.
    if ((state.status === "failed" || state.status === "exited") && !(SMOKE_SUPERVISE && killed)) {
      clearInterval(timer);
      finish(false, { reason: `recorder ${state.status}`, detail: state.detail, log: recorder.logTail.slice(-15) });
      return;
    }

    const base = { recorder: state.status, stream: stream.state.status, received: stream.state.received, renderer: rendererReady };
    const up = state.status === "ready" && stream.state.status === "open" && rendererReady;
    const candidates = library.snapshot().sessions.reduce((n, x) => n + x.candidates.length, 0);
    if (SMOKE_LIVE || SMOKE_DEMO) {
      if (!up || liveStep > 0) return;
      liveStep = 1;
      clearInterval(timer);
      void (SMOKE_DEMO ? smokeDemo(base) : smokeLive(base)).then(
        (r) => {
          testWindow?.stdin?.end();
          finish(r.ok, r.detail);
        },
        (err: unknown) => {
          testWindow?.stdin?.end();
          finish(false, { ...base, reason: err instanceof Error ? err.message : String(err), log: recorder.logTail.slice(-15) });
        },
      );
      return;
    }

    if (SMOKE_STORAGE) {
      if (!up || storageStep > 0) return;
      storageStep = 1;
      clearInterval(timer);
      const gib = 1024 ** 3;
      const report = (clips: number, near: boolean, full: boolean): void => {
        stream.emit("envelope", {
          sessionId: "recorder",
          topic: "recorder.storage",
          storageStatus: { clipsBytes: String(clips), clipsBudgetBytes: String(10 * gib), footageBytes: String(3 * gib), footageBudgetBytes: String(20 * gib), clipsNearLimit: near, clipsFull: full, volumeFreeBytes: String(400 * gib) },
        });
      };
      const read = async (): Promise<unknown> =>
        mainWindow?.webContents.executeJavaScript(
          `(() => ({ banner: document.getElementById("storage-banner").hidden ? null : document.getElementById("storage-text").textContent, dialog: document.getElementById("clips-full").open, settings: document.getElementById("storage-settings").textContent }))()`,
          false,
        );
      const pause = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));
      void (async () => {
        report(9.2 * gib, true, false);
        await pause(300);
        const near = (await read()) as Record<string, unknown>;
        report(10 * gib, true, true);
        await pause(300);
        const full = (await read()) as Record<string, unknown>;
        const ok = typeof near["banner"] === "string" && /9\.2 of 10 GB/.test(near["banner"] as string) && near["dialog"] === false && full["dialog"] === true && /full/.test(String(full["banner"])) && /20 GB/.test(String(full["settings"]));
        finish(ok, { ...base, near, full });
      })();
      return;
    }

    if (SMOKE_SUPERVISE) {
      if (up && !killed) {
        killed = { pid: state.pid, endpoint: state.endpoint };
        recorder.note(`smoke: killing the recorder (pid ${state.pid}) to test the supervisor`);
        if (state.pid) process.kill(state.pid);
      } else if (killed && up && state.pid !== killed.pid) {
        clearInterval(timer);
        finish(state.endpoint !== killed.endpoint, { ...base, before: killed, after: { pid: state.pid, endpoint: state.endpoint }, supervision: supervisor.state });
      }

      return;
    }
    if (up && !SMOKE_VIEWER && candidates >= SMOKE_CANDIDATES) {
      if (SMOKE_CANDIDATES === 0) {
        clearInterval(timer);
        finish(true, base);
        return;
      }

      // Pass only once the main window itself lists them.
      void rendered().then((w) => {
        const rows = typeof w === "object" && w !== null ? Number((w as Record<string, unknown>)["libraryRows"]) : 0;
        if (rows >= SMOKE_CANDIDATES && !rating) {
          clearInterval(timer);
          rating = true;
          // Rate the selected candidate as a user would: open the Library tab
          // and press 5. Then see what the preview player loaded.
          const contents = mainWindow?.webContents;
          void contents
            ?.executeJavaScript(`document.querySelector('#tabs button[data-tab="library"]').click(); document.dispatchEvent(new KeyboardEvent("keydown", { key: "5", bubbles: true })); true`, true)
            .then(() => new Promise((r) => setTimeout(r, SMOKE_FOOTAGE ? 2500 : 400)))
            .then(() =>
              contents.executeJavaScript(
                `(() => { const v = document.getElementById("preview-video"); return { src: (v.currentSrc || "").split(":")[0], readyState: v.readyState, time: Math.round(v.currentTime * 10) / 10, width: v.videoWidth, error: v.error ? v.error.code : null, info: document.getElementById("preview-info").textContent }; })()`,
                false,
              ),
            )
            .then((preview: unknown) => {
              const rated = library.snapshot().sessions.flatMap((x) => x.candidates).filter((c) => c.rating === 5).length;
              const p = (preview ?? {}) as Record<string, unknown>;
              const previewOk = !SMOKE_FOOTAGE || (p["src"] === MEDIA_SCHEME && Number(p["readyState"]) >= 2 && Number(p["width"]) > 0);
              const stored = library.snapshot().sessions.flatMap((x) => x.candidates).filter((c) => c.rating === 5 && c.ratingStored).length;
              finish(rated === 1 && previewOk, { ...base, candidates, rated, stored, preview, footage: footage.list });
            });
        }
      });
    }


    if (up && SMOKE_FOOTAGE && !footageOpened) {
      footageOpened = true;
      void footage.open(SMOKE_FOOTAGE, { profile: SMOKE_PROFILE, retention: "owned", permissionRef: "" });
    }

    if (up && SMOKE_VIEWER && !viewerOpened) {
      viewerOpened = true;
      void viewer.open(VIEWER_LINK ? parseEmbedSource(VIEWER_LINK) : null);
    }

    if (SMOKE_VIEWER && viewerOpened) {
      const official = library.snapshot().sessions.find((s) => s.sourceKind === "official_player");
      const ingest = viewer.ingestStatus;
      if (official && official.signalFrames >= 150 && ingest.framesSent >= 150) {
        clearInterval(timer);
        void viewer.panelStatus().then((panel) => finish(true, { ...base, ingest, panel, session: { title: official.title, retention: official.retention, signalFrames: official.signalFrames } }));
        return;
      }
    }

    if (Date.now() - started > SMOKE_TIMEOUT_MS) {
      clearInterval(timer);
      void viewer.playerStatus().then((player) => finish(false, { ...base, reason: "timed out", player, ingest: viewer.ingestStatus, log: recorder.logTail.slice(-15) }));
    }
  }, 250);
}

// ---------------------------------------------------------------- lifecycle

async function shutdown(): Promise<void> {
  quitting = true;
  supervisor.dispose();
  footage.stopAll();
  viewer?.close();
  stream.disconnect();
  await recorder.stop();
}

if (!SMOKE && !app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on("second-instance", () => {
    if (mainWindow) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  void app.whenReady().then(() => {
    settings = loadSettings(settingsFile());
    const playerSession = session.fromPartition("lookback-viewer"); // no "persist:": in memory only
    const panelSession = pagesSession();
    // The embeds see an ordinary Chromium user agent.
    playerSession.setUserAgent(app.userAgentFallback.replace(/\s(Electron|lookback-app)\/\S+/g, ""));
    lockPermissions(session.defaultSession, () => false);
    lockPermissions(panelSession, () => false);
    lockPermissions(playerSession, (contents) => contents !== null && contents === viewer?.playerContents);

    viewer = new ViewerController({
      server: new ViewerServer(join(dist, "viewer")),
      preload: join(dist, "preload", "viewer-preload.js"),
      playerSession,
      panelSession,
      connection: () => recorder.connection,
      calibrateUrl: () => recorder.calibrate,
      log: (line) => recorder.note(line),
      testPattern: SMOKE_VIEWER && !VIEWER_LINK,
      consoleToLog: SMOKE_VIEWER,
    });
    viewer.on("ingest", (state) => send("lookback:ingest", state));

    registerProtocol();
    registerIpc();
    createMainWindow();
    if (settings.autoStart || SMOKE) {
      startRecorder();
    }

    if (SMOKE) {
      runSmoke();
    } else if (VIEWER_LINK) {
      // Development convenience: open the viewer on a link at startup.
      void viewer.open(parseEmbedSource(VIEWER_LINK));
    }
  });

  let cleaned = false;
  app.on("before-quit", (event) => {
    if (!cleaned) {
      event.preventDefault();
      cleaned = true;
      void shutdown().finally(() => app.quit());
    }
  });
  app.on("window-all-closed", () => {
    if (!quitting) app.quit();
  });
}

