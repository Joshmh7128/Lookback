import { app, BrowserWindow, ipcMain, protocol, session, shell, WebContentsView, type IpcMainEvent, type IpcMainInvokeEvent } from "electron";
import { existsSync, statSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { BrowseState, EmbeddedView, LibrarySnapshot, RecorderState, Settings, StreamState, ViewBounds, ViewerReport } from "../shared/api";
import { browseArgs, runBrowse } from "./browse";
import { parseEmbedSource, validateEmbedSource } from "./embeds";
import { validRect } from "./frames";
import { eventsUrl } from "./handshake";
import { Library, validRating } from "./library";
import { datasetsFolder, locateTool, profilesFolder, RECORDER, recorderArgs, TESTBENCH, type LocateContext } from "./locate";
import { listProfiles } from "./profiles";
import { RecorderHost } from "./recorder";
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

const SMOKE = process.argv.includes("--smoke") || process.argv.includes("--smoke-viewer");
const SMOKE_VIEWER = process.argv.includes("--smoke-viewer");
/** Open the viewer on this link at startup; with --smoke-viewer, play it instead of the synthetic test pattern. */
const VIEWER_LINK = process.argv.find((a) => a.startsWith("--viewer-link="))?.slice("--viewer-link=".length) ?? null;
/** With --smoke, also wait for this many clip candidates (fed by the testbench) before passing. */
const SMOKE_CANDIDATES = Number(process.argv.find((a) => a.startsWith("--smoke-candidates="))?.slice("--smoke-candidates=".length) ?? 0) || 0;
const SMOKE_TIMEOUT_MS = 120_000;

// Tests and the smoke run point userData at a scratch folder.
if (process.env["LOOKBACK_USER_DATA"]) {
  app.setPath("userData", process.env["LOOKBACK_USER_DATA"]);
}

protocol.registerSchemesAsPrivileged([{ scheme: "app", privileges: { standard: true, secure: true, supportFetchAPI: true } }]);
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
  const profiles = profilesFolder(settings, locate());
  const command = locateTool(RECORDER, settings.recorderPath, locate(), recorderArgs(settings, profiles));
  if (!command) {
    recorder.reportFailure("Lookback.Recorder was not found. Build it with `dotnet build Lookback.slnx -c Release`, or set its path in Settings.");
    return recorder.state;
  }

  recorder.start(command);
  return recorder.state;
}

recorder.on("state", (state) => {
  send("lookback:state", state);
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
stream.on("envelope", (envelope) => {
  if (library.apply(envelope)) {
    scheduleLibrary();
  }
});
stream.on("state", (state) => send("lookback:stream", state));
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
  handle("lookback:start", fromMain, () => startRecorder());
  handle("lookback:stop", fromMain, async () => {
    await recorder.stop();
    return recorder.state;
  });
  handle("lookback:restart", fromMain, async () => {
    await recorder.stop();
    return startRecorder();
  });
  handle("lookback:get-log", fromMain, () => recorder.logTail);
  handle("lookback:get-stream", fromMain, (): StreamState => stream.state);
  handle("lookback:get-library", fromMain, (): LibrarySnapshot => library.snapshot());
  handle("lookback:rate", fromMain, (sessionId, candidateId, rating) => {
    if (typeof sessionId !== "string" || typeof candidateId !== "string" || !validRating(rating)) {
      return false;
    }

    const ok = library.rate(sessionId, candidateId, rating);
    if (ok) scheduleLibrary();
    return ok;
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
    viewer.report({ status, rect: validRect(r["rect"]) });
  });
  on("viewer:toggle-panel", fromViewer, () => viewer.togglePanel());
}

// ---------------------------------------------------------------- protocol

function registerProtocol(): void {
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

  let rendererReady = false;
  ipcMain.once("lookback:rendered", (event) => {
    if (fromMain(event)) rendererReady = true;
  });
  let viewerOpened = false;
  let rating = false;
  const timer = setInterval(() => {
    const state = recorder.state;
    if (state.status === "failed" || state.status === "exited") {
      clearInterval(timer);
      finish(false, { reason: `recorder ${state.status}`, detail: state.detail, log: recorder.logTail.slice(-15) });
      return;
    }

    const base = { recorder: state.status, stream: stream.state.status, received: stream.state.received, renderer: rendererReady };
    const up = state.status === "ready" && stream.state.status === "open" && rendererReady;
    const candidates = library.snapshot().sessions.reduce((n, x) => n + x.candidates.length, 0);
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
          // Rate the first row through the UI, as a user would.
          void mainWindow?.webContents
            .executeJavaScript(`document.querySelector("#lib-rows .rate button.up")?.click(); true`, true)
            .then(() => new Promise((r) => setTimeout(r, 300)))
            .then(() => {
              const rated = library.snapshot().sessions.flatMap((x) => x.candidates).filter((c) => c.rating === 1).length;
              finish(rated === 1, { ...base, candidates, rated });
            });
        }
      });
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

