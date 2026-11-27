import { BaseWindow, WebContentsView, type NativeImage, type Session, type WebContents } from "electron";
import { EventEmitter } from "node:events";
import { performance } from "node:perf_hooks";
import type { EmbedSource, IngestState, PlaybackStatus, ViewBounds, ViewerReport } from "../shared/api";
import { videoRef } from "./embeds";
import { cropRect, FrameThrottle } from "./frames";
import type { ReadyInfo } from "./handshake";
import { INGEST_HEIGHT, INGEST_WIDTH, IngestSession } from "./ingest";
import { allowNavigation } from "./security";
import type { ViewerServer } from "./viewer-server";

// The official-player viewer: one window with the player page (official
// YouTube or Twitch embed, in the in-memory `lookback-viewer` partition) and
// the recorder's calibration panel docked beside it.
//
// While a video plays, the main process subscribes to the player page's
// frames, crops them to the player element, scales them to 384x216 BGRA and
// streams them to the recorder's /ingest at 30 fps. Frames live only in
// memory on their way to the socket; nothing on this path touches disk, and
// the video's id is never logged. No audio is sent: the only audio the shell
// could tap is the whole system mix.

const PANEL_WIDTH = 460;
/** After an ingest connection fails, wait this long before trying again. */
const RETRY_MS = 3000;

export interface ViewerOptions {
  server: ViewerServer;
  /** Preload for the player page. */
  preload: string;
  /** In-memory partition for the player page. */
  playerSession: Session;
  /** In-memory partition for the recorder's calibration panel. */
  panelSession: Session;
  /** The recorder's handshake while it is ready. */
  connection: () => ReadyInfo | null;
  /** The calibration panel URL (token in its fragment) while offered. */
  calibrateUrl: () => string | null;
  log: (line: string) => void;
  /** Copy the player page's console to the log (smoke test only: it may name the video). */
  consoleToLog?: boolean;
  /** Load the synthetic test pattern instead of the embed page (smoke test). */
  testPattern?: boolean;
}

interface ViewerEvents {
  ingest: [IngestState];
  closed: [];
}

export class ViewerController extends EventEmitter<ViewerEvents> {
  private window: BaseWindow | null = null;
  private player: WebContentsView | null = null;
  private panel: WebContentsView | null = null;
  private panelShown = true;
  private panelUrl: string | null = null;
  private source: EmbedSource | null = null;
  private profile = "";
  private status: PlaybackStatus = "idle";
  private rect: ViewBounds | null = null;
  private ingest: IngestSession | null = null;
  private lastFailure = 0;
  private subscribed = false;
  private readonly throttle = new FrameThrottle();
  private pending: EmbedSource | null = null;
  private ingestState: IngestState = { status: "off", framesSent: 0, framesDropped: 0, detail: null };

  constructor(private readonly options: ViewerOptions) {
    super();
  }

  /** The player page's web contents, for IPC sender checks. */
  get playerContents(): WebContents | null {
    return this.player?.webContents ?? null;
  }

  get isOpen(): boolean {
    return this.window !== null;
  }

  get ingestStatus(): IngestState {
    return { ...this.ingestState };
  }

  /** The source the shell asked for when opening; handed to the page once. */
  takeInitialSource(): EmbedSource | null {
    const s = this.pending;
    this.pending = null;
    return s;
  }

  async open(source: EmbedSource | null): Promise<void> {
    if (this.window) {
      this.window.focus();
      if (source) {
        this.player?.webContents.send("viewer:open", source);
      }

      return;
    }

    await this.options.server.start();
    this.pending = source;
    const win = new BaseWindow({ width: 1280 + PANEL_WIDTH, height: 780, minWidth: 720, minHeight: 420, title: "Lookback viewer", backgroundColor: "#101317", show: false });
    const player = new WebContentsView({
      webPreferences: {
        session: this.options.playerSession,
        preload: this.options.preload,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        webSecurity: true,
        spellcheck: false,
        autoplayPolicy: "no-user-gesture-required",
        // Frames keep flowing while the window is behind another one.
        backgroundThrottling: false,
      },
    });
    const origin = this.options.server.origin;
    allowNavigation(player.webContents, (url) => url.origin === origin);
    if (this.options.consoleToLog) {
      player.webContents.on("console-message", (e) => this.options.log(`viewer console ${e.level}: ${e.message.slice(0, 300)}`));
    }

    win.contentView.addChildView(player);
    this.window = win;
    this.player = player;
    this.layout();
    win.on("resize", () => this.layout());
    win.on("closed", () => this.onClosed());
    player.webContents.once("did-finish-load", () => win.show());
    this.refreshPanel();
    await player.webContents.loadURL(this.options.server.url(this.options.testPattern ? "test-pattern.html" : "viewer.html"));
  }

  close(): void {
    this.window?.close();
  }

  /** The player page's status line (for the smoke test). */
  async playerStatus(): Promise<string | null> {
    const contents = this.player?.webContents;
    if (!contents || contents.isDestroyed()) {
      return null;
    }

    const text: unknown = await contents.executeJavaScript("document.getElementById('status')?.textContent ?? null", false).catch(() => null);
    return typeof text === "string" ? text : null;
  }

  /** The calibration panel's status line (for the smoke test), or null without a panel. */
  async panelStatus(): Promise<string | null> {
    const contents = this.panel?.webContents;
    if (!contents || contents.isDestroyed()) {
      return null;
    }

    const text: unknown = await contents.executeJavaScript("document.getElementById('status')?.textContent ?? null", false).catch(() => null);
    return typeof text === "string" ? text : null;
  }

  togglePanel(): void {
    this.panelShown = !this.panelShown;
    this.layout();
  }

  /** The page picked a source and profile. A change ends the current ingest session. */
  select(source: EmbedSource | null, profile: string): void {
    const changed = !sameSource(source, this.source) || profile !== this.profile;
    this.source = source;
    this.profile = profile;
    if (changed) {
      this.endIngest("source changed");
    }
  }

  /** The page's playback status and player rectangle; sent on change and about once a second while playing. */
  report(report: ViewerReport): void {
    this.rect = report.rect;
    const was = this.status;
    this.status = report.status;
    if (report.status === "playing") {
      this.beginIngest();
    } else if (report.status === "ended" || report.status === "idle" || report.status === "error") {
      this.endIngest(report.status);
    }

    if (was === "playing" && report.status !== "playing") {
      this.throttle.reset();
    }
  }

  /** The recorder came up, went down or restarted. */
  recorderChanged(): void {
    const info = this.options.connection();
    if (!info?.ingest) {
      this.endIngest("recorder not ready");
      this.setIngest({ status: "off", framesSent: 0, framesDropped: 0, detail: info ? "the recorder has no ingest endpoint" : "the recorder is not running" });
    } else if (this.ingestState.status === "off") {
      this.setIngest({ status: "idle", framesSent: 0, framesDropped: 0, detail: null });
    }

    this.refreshPanel();
  }

  private beginIngest(): void {
    if (this.ingest || !this.source || !this.window) {
      return;
    }

    const info = this.options.connection();
    if (!info?.ingest) {
      return;
    }

    if (performance.now() - this.lastFailure < RETRY_MS) {
      return;
    }

    const session = new IngestSession(info.ingest, info.token, { platform: this.source.platform, videoRef: videoRef(this.source), profile: this.profile });
    this.ingest = session;
    session.on("state", (state) => {
      if (this.ingest !== session) {
        return;
      }

      this.setIngest(state);
      if (state.status === "error" || (state.status === "idle" && state.detail)) {
        // The socket went away under us (recorder restarted, or refused).
        this.options.log(`viewer: ingest ${state.status}${state.detail ? `: ${state.detail}` : ""}`);
        this.lastFailure = performance.now();
        this.ingest = null;
        this.stopFrames();
      } else if (state.status === "streaming" && !this.subscribed) {
        this.options.log(`viewer: streaming ${this.source?.platform ?? ""} player frames to the recorder${this.profile ? ` (profile ${this.profile})` : ""}`);
        this.startFrames();
      }
    });
    this.setIngest(session.state);
    session.open();
  }

  private endIngest(why: string): void {
    const session = this.ingest;
    this.ingest = null;
    this.stopFrames();
    if (session) {
      session.close();
      this.options.log(`viewer: ingest stopped (${why}); ${session.state.framesSent} frames sent, ${session.state.framesDropped} dropped`);
      this.setIngest({ ...session.state, status: this.options.connection()?.ingest ? "idle" : "off" });
    }
  }

  private startFrames(): void {
    const contents = this.player?.webContents;
    if (!contents || this.subscribed) {
      return;
    }

    this.subscribed = true;
    this.throttle.reset();
    contents.beginFrameSubscription(false, (image) => this.onFrame(image));
  }

  private stopFrames(): void {
    if (this.subscribed) {
      this.subscribed = false;
      const contents = this.player?.webContents;
      if (contents && !contents.isDestroyed()) {
        contents.endFrameSubscription();
      }
    }
  }

  private onFrame(image: NativeImage): void {
    const session = this.ingest;
    const player = this.player;
    const now = performance.now();
    if (!session?.streaming || !player || this.status !== "playing" || !this.throttle.take(now)) {
      return;
    }

    const size = image.getSize();
    const crop = cropRect(this.rect, player.getBounds().width, size.width, size.height);
    if (!crop) {
      return;
    }

    // Chromium crops and scales; toBitmap is BGRA8 on Windows (Skia's N32
    // order on little-endian). One copy per frame, held only until sent.
    const bitmap = image.crop(crop).resize({ width: INGEST_WIDTH, height: INGEST_HEIGHT, quality: "good" }).toBitmap();
    if (bitmap.length === INGEST_WIDTH * INGEST_HEIGHT * 4) {
      session.sendVideo(bitmap, INGEST_WIDTH, INGEST_HEIGHT, now);
    }
  }

  private refreshPanel(): void {
    const win = this.window;
    if (!win) {
      return;
    }

    const url = this.options.calibrateUrl();
    if (url === this.panelUrl) {
      return;
    }

    this.panelUrl = url;
    if (this.panel) {
      win.contentView.removeChildView(this.panel);
      this.panel.webContents.close();
      this.panel = null;
    }

    if (url) {
      const panel = new WebContentsView({
        webPreferences: { session: this.options.panelSession, sandbox: true, contextIsolation: true, nodeIntegration: false, spellcheck: false },
      });
      const origin = new URL(url).origin;
      allowNavigation(panel.webContents, (u) => u.origin === origin);
      win.contentView.addChildView(panel);
      this.panel = panel;
      void panel.webContents.loadURL(url).catch(() => this.options.log("viewer: the calibration panel did not load"));
    }

    this.layout();
  }

  private layout(): void {
    const win = this.window;
    if (!win || !this.player) {
      return;
    }

    const { width, height } = win.getContentBounds();
    const panelWidth = this.panel && this.panelShown ? Math.min(PANEL_WIDTH, Math.floor(width / 2)) : 0;
    this.player.setBounds({ x: 0, y: 0, width: width - panelWidth, height });
    if (this.panel) {
      this.panel.setVisible(panelWidth > 0);
      this.panel.setBounds({ x: width - panelWidth, y: 0, width: panelWidth, height });
    }
  }

  private onClosed(): void {
    this.endIngest("viewer closed");
    for (const view of [this.player, this.panel]) {
      if (view && !view.webContents.isDestroyed()) {
        view.webContents.close();
      }
    }

    this.window = null;
    this.player = null;
    this.panel = null;
    this.panelUrl = null;
    this.source = null;
    this.status = "idle";
    this.rect = null;
    this.emit("closed");
  }

  private setIngest(state: IngestState): void {
    this.ingestState = state;
    this.emit("ingest", { ...state });
    const contents = this.player?.webContents;
    if (contents && !contents.isDestroyed()) {
      contents.send("viewer:ingest", state);
    }
  }
}

function sameSource(a: EmbedSource | null, b: EmbedSource | null): boolean {
  if (a === null || b === null) {
    return a === b;
  }

  return a.platform === b.platform && videoRef(a) === videoRef(b);
}
