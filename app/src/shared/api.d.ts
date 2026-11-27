// Types shared by the main process, the preloads and the renderer pages.
// Declarations only: nothing here exists at run time, so a sandboxed preload
// or a browser module can import it with `import type`.

export type RecorderStatus = "stopped" | "starting" | "ready" | "exited" | "failed";

/** What the shell knows about the recorder. Never carries the token. */
export interface RecorderState {
  status: RecorderStatus;
  /** How the recorder was launched, for the overview, e.g. "dotnet Lookback.Recorder.dll". */
  command: string | null;
  pid: number | null;
  pipe: string | null;
  /** ws:// address of /events, without the token. */
  endpoint: string | null;
  /** True when the recorder serves the live profiler page. */
  profiler: boolean;
  /** True when the recorder accepts official-player frames at /ingest. */
  ingest: boolean;
  /** True when the recorder serves the calibration panel. */
  calibrate: boolean;
  /** Why the recorder is not running, when it stopped or failed. */
  detail: string | null;
  startedAt: number | null;
}

export interface Settings {
  /** Recorder executable or .dll. Empty means find it automatically. */
  recorderPath: string;
  /** lookback-testbench executable or .dll, for the dataset browser. Empty means find it automatically. */
  testbenchPath: string;
  /** Datasets folder for the browser. Empty means the repository's datasets/. */
  datasetsDir: string;
  pipeName: string;
  /** 0 picks a free port. */
  webSocketPort: number;
  /** Ask the recorder to serve the live profiler page. */
  profilerPage: boolean;
  /** Start the recorder when the app opens. */
  autoStart: boolean;
}

export type EmbeddedView = "profiler" | "browse";

export interface ViewBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface BrowseResult {
  ok: boolean;
  /** Last lines of the testbench output, or why it could not run. */
  message: string;
}

export interface BrowseState {
  /** True when a generated browser page exists. */
  available: boolean;
  builtAt: number | null;
}

/** The API the main window's preload exposes as `window.lookback`. */
export interface LookbackApi {
  getState(): Promise<RecorderState>;
  /** The full /events URL including the token, or null while the recorder is not ready. */
  getConnection(): Promise<string | null>;
  onState(listener: (state: RecorderState) => void): () => void;
  startRecorder(): Promise<RecorderState>;
  stopRecorder(): Promise<RecorderState>;
  restartRecorder(): Promise<RecorderState>;
  getLog(): Promise<string[]>;
  onLog(listener: (line: string) => void): () => void;
  /** Show an embedded page over `bounds` (in CSS pixels of the main window), or hide it with null. */
  showView(view: EmbeddedView | null, bounds: ViewBounds | null): Promise<boolean>;
  getBrowseState(): Promise<BrowseState>;
  buildBrowse(): Promise<BrowseResult>;
  getSettings(): Promise<Settings>;
  saveSettings(settings: Settings): Promise<Settings>;
  openViewer(source: string | null): Promise<void>;
  revealClip(path: string): Promise<boolean>;
  /** Tells the main process the renderer's stream state (used by the smoke test). */
  reportStream(connected: boolean): void;
}

export type ViewerPlatform = "youtube" | "twitch";

/** A video the viewer can play through an official embedded player. */
export type EmbedSource =
  | { platform: "youtube"; videoId: string; startSeconds: number }
  | { platform: "twitch"; kind: "channel"; channel: string }
  | { platform: "twitch"; kind: "video"; videoId: string };

export type PlaybackStatus = "idle" | "loading" | "playing" | "paused" | "ended" | "error";

/** What the viewer page reports to the main process. */
export interface ViewerReport {
  status: PlaybackStatus;
  /** The player's rectangle in CSS pixels of the viewer page. */
  rect: ViewBounds | null;
}

export interface IngestState {
  /** "off": the recorder has no ingest endpoint; "idle": nothing playing. */
  status: "off" | "idle" | "connecting" | "streaming" | "error";
  framesSent: number;
  framesDropped: number;
  detail: string | null;
}

/** The API the viewer window's preload exposes as `window.lookbackViewer`. */
export interface ViewerApi {
  /** Parse a pasted link. Null when it is not a YouTube or Twitch video or channel. */
  parse(input: string): Promise<EmbedSource | null>;
  /** The source the shell asked the viewer to open, if any. */
  initialSource(): Promise<EmbedSource | null>;
  onOpen(listener: (source: EmbedSource) => void): () => void;
  profiles(): Promise<string[]>;
  select(source: EmbedSource | null, profile: string): void;
  report(report: ViewerReport): void;
  onIngest(listener: (state: IngestState) => void): () => void;
}
