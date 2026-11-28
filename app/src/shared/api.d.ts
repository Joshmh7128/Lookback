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
  /** True when it ended in a way worth restarting (an unexpected exit, or no handshake in time). */
  restartable: boolean;
}

/** The shell's watch over the recorder: restarts after unexpected exits. */
export interface SupervisionState {
  /** Restarts in the last minute. */
  restarts: number;
  maxRestarts: number;
  /** When the next restart is due (ms since the epoch), or null. */
  nextRestartAt: number | null;
  /** True after too many restarts in a minute: the shell stopped trying. */
  gaveUp: boolean;
  lastCrash: { at: number; detail: string; fatal: string | null; stderr: string[] } | null;
}

/** The recorder's storage use, from `recorder.storage` (StorageStatus, Envelope field 21). Sizes in bytes. */
export interface StorageState {
  clipsBytes: number;
  clipsBudgetBytes: number;
  footageBytes: number;
  footageBudgetBytes: number;
  /** Saved clips reached the warning threshold (90% of the budget by default). */
  clipsNearLimit: boolean;
  /** Saved clips are at the budget: the recorder refuses new saves. */
  clipsFull: boolean;
  volumeFreeBytes: number;
  /** When the shell received it (ms since the epoch). */
  at: number;
}

/** Where things live, for the Settings page. */
export interface ShellPaths {
  /** Where the recorder stores ratings (`<dir>/<game>/ratings.jsonl`). */
  ratings: string;
  /** True when that is the checkout's datasets folder (passed to the recorder), false for the recorder's default. */
  ratingsInCheckout: boolean;
  profiles: string | null;
}

/** The main process's connection to the recorder's /events stream. */
export interface StreamState {
  status: "off" | "connecting" | "open" | "retrying";
  /** Envelopes received since the shell started. */
  received: number;
  detail: string | null;
}

export interface Settings {
  /** Recorder executable or .dll. Empty means find it automatically. */
  recorderPath: string;
  /** lookback-testbench executable or .dll, for the dataset browser. Empty means find it automatically. */
  testbenchPath: string;
  /** Datasets folder for the browser. Empty means the repository's datasets/. */
  datasetsDir: string;
  /** Profiles folder the calibration panel saves into. Empty means the repository's profiles/. */
  profilesDir: string;
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

/** A profile in the editor's list (`lookback-testbench profile-view`). */
export interface ProfileSummary {
  id: string;
  displayName: string;
  parent: string | null;
  level: "game" | "subgame" | "mode";
  /** 0 for a game. */
  depth: number;
}

/**
 * A profile with everything it inherits applied. `source` on every number is
 * the id of the profile whose file sets it, or "defaults" (the clipper's
 * defaults, `clipper.defaults.json`).
 */
export interface ProfileView {
  id: string;
  displayName: string;
  parent: string | null;
  level: string;
  /** The profile's file, relative to the profiles folder. */
  file: string;
  /** Root first. */
  path: string[];
  events: {
    name: string;
    source: string;
    weight: number;
    baseConfidence: number;
    /** `index` is what `thresholds` in an edit takes. */
    conditions: { index: number; list: "when" | "whenAny"; signal: string; kind: "rise" | "above" | "below"; value: number }[];
  }[];
  composites: { name: string; source: string; of: string; atLeast: number; withinMs: number; weight: number }[];
  clipper: {
    numbers: { field: string; value: number; source: string }[];
    components: { signal: string; weight: number; minSpread: number | null; source: string }[];
  };
  /** Signals the game's tree produces: what a clipper component may name. */
  signals: string[];
}

/**
 * Edits to one profile. Rule weights and thresholds go to the file that
 * defines the rule; clipper values are the edited profile's own overrides,
 * where null removes the override (the value is inherited again).
 */
export interface ProfileEdits {
  events: { name: string; weight?: number; thresholds?: Record<string, number> }[];
  composites: { name: string; weight: number }[];
  clipper: Record<string, number | null>;
  components: Record<string, number | null>;
}

/** What `profile-view` and `profile-save` print. `ok: false` carries the loader's message; nothing was written. */
export interface ProfileToolResult {
  ok: boolean;
  message?: string;
  directory?: string;
  profiles?: ProfileSummary[];
  view?: ProfileView | null;
  /** Files a save changed, relative to the profiles folder. */
  files?: string[];
}

/**
 * Stars for a candidate's content ("is this moment a highlight worth
 * keeping?"), not its video quality. 0 is unrated; 1 to 5 are stars.
 */
export type Rating = 0 | 1 | 2 | 3 | 4 | 5;

export interface QaCheckView {
  name: string;
  passed: boolean;
  value: number;
  detail: string;
}

export interface CandidateView {
  id: string;
  sessionId: string;
  startQpc: number;
  endQpc: number;
  peakQpc: number;
  score: number;
  modelScore: number | null;
  highlight: number;
  profile: string;
  reasons: string[];
  qa: { passed: boolean; checks: QaCheckView[] } | null;
  /** Where the recorder saved the clip, once it has. */
  savedPath: string | null;
  /** A highlight (the most exciting 10–20 s of a longer candidate): that candidate's id. Null otherwise. */
  parentId: string | null;
  rating: Rating;
  /** True once the recorder acknowledged the rating (stored behind its retention rules); false while it lives only in the shell's memory. */
  ratingStored: boolean;
  /** Why the recorder did not store the rating (its ack's message), when it refused. */
  ratingNote: string | null;
  /** A save the shell asked for and is waiting on. */
  saving: boolean;
  /** Why the recorder did not save the clip (its ack's message), when it refused. */
  saveNote: string | null;
}

/** A moment drawn on the timeline: a game event, a profile detection or a lifecycle change. */
export interface TimelineMark {
  kind: "event" | "profile" | "lifecycle";
  qpc: number;
  name: string;
  detail: string;
  confidence: number;
}

export interface SessionView {
  id: string;
  /** Arrival order; larger is newer. */
  order: number;
  title: string;
  /** "live_game", "file", "official_player" or "unspecified". */
  sourceKind: string;
  /** "ephemeral", "owned", "permissioned", "open_license" or "unspecified". */
  retention: string;
  profile: string;
  qpcFrequency: number;
  startQpc: number;
  lastQpc: number;
  ended: boolean;
  endReason: string | null;
  events: number;
  signalFrames: number;
  /** True when the shell can preview this session's candidates (footage it opened, or saved clips). */
  previewable: boolean;
}

export interface LibrarySessionSnapshot extends SessionView {
  marks: TimelineMark[];
  candidates: CandidateView[];
}

export interface LibrarySnapshot {
  revision: number;
  sessions: LibrarySessionSnapshot[];
}

/** A footage file the user opened for analysis. */
export interface FootageJob {
  id: string;
  /** File name only; the full path stays in the main process. */
  name: string;
  profile: string;
  /** "recorder": the recorder's analyze command; "testbench": lookback-testbench live into the recorder's pipe. */
  via: "recorder" | "testbench" | null;
  status: "starting" | "analysing" | "done" | "failed";
  sessionId: string | null;
  detail: string | null;
}

export interface FootageRequest {
  /** Profile id; empty uses "generic". */
  profile: string;
  retention: "owned" | "permissioned" | "open-license";
  /** Where the permission or licence is recorded; required unless owned. */
  permissionRef: string;
}

/** What the renderer needs to play a candidate: a media URL and the window in seconds of that media. */
export interface PreviewSource {
  url: string;
  start: number;
  end: number;
  /** "footage": the analysed file, seeked to the window; "clip": the saved clip file. */
  kind: "footage" | "clip";
}

/** The API the main window's preload exposes as `window.lookback`. */
/** One recorded session in the store. */
export interface StoredSession {
  id: string;
  title: string;
  profile: string;
  startedUtc: string;
  endedUtc: string | null;
  live: boolean;
  /** Kept out of the sweep. */
  keep: boolean;
  bytes: number;
  candidates: number;
  /** When its footage and unrated clips are swept; null when kept or live. */
  sweepAfterUtc: string | null;
}

/** A file in the store: a saved clip or a cached one. */
export interface StoredFile {
  path: string;
  name: string;
  bytes: number;
  createdUtc?: string;
  lastUsedUtc?: string;
  expiresUtc?: string;
  sessionId?: string | null;
  candidateId?: string | null;
}

/** Everything the recorder's store holds (the storage command). */
export interface StorageListing {
  root: string;
  sessions: StoredSession[];
  clips: StoredFile[];
  cache: StoredFile[];
  footageBytes: number;
  footageBudgetBytes: number;
  clipsBytes: number;
  clipsBudgetBytes: number;
  cacheBytes: number;
  cacheBudgetBytes: number;
  volumeFreeBytes: number;
}

/** How a backfill job on one session went (the recorder's backfill-plan). */
export interface BackfillJob {
  state: "starting" | "running" | "done" | "failed";
  message: string;
  written: number;
  total: number;
  refused: string[];
}

/** A rated session whose ratings lack the clipper's features. */
export interface BackfillSession {
  sessionId: string;
  game: string;
  /** "live-game" (read back from the store) or "file" (you choose the file). */
  sourceKind?: string;
  /** Ratings that need features; 0 once a job filled them all. */
  ratings: number;
  ratedUtc?: string;
  /** How far into the footage the latest rated window ends, in seconds. */
  windowEndSeconds?: number;
  /** The footage's fingerprint, when its ratings carry one. */
  digest?: string | null;
  /** A live recording's stored footage; null when it is gone. */
  footage?: { bytes: number; sweepUtc: string | null } | null;
  job?: BackfillJob;
}

export interface BackfillPlan {
  sessions: BackfillSession[];
  /** A job is running. */
  busy: boolean;
}

/** A trained scorer as the training environment shows it. */
export interface ModelSummary {
  /** Its folder in ml/out/history, for an earlier model. */
  name: string | null;
  trainedAt: string | null;
  rows: number | null;
  sessions: number | null;
  /** Games it scores (others keep the heuristic). */
  games: string[];
  /** Held-out AUC and log loss: the heuristic's against the calibrated model's. */
  auc: { heuristic: number | null; model: number | null };
  logLoss: { heuristic: number | null; model: number | null };
}

/** The last training run's verdict. */
export interface TrainingRun {
  at: string | null;
  /** True when it beat the heuristic and was installed. */
  promoted: boolean;
  /** True when there was not enough (or not allowed) data to train at all. */
  refused: boolean;
  reason: string;
  model: ModelSummary | null;
}

export interface TrainingStatus {
  /** False without a source checkout (packaged app). */
  available: boolean;
  /** ready: ml/.venv exists; no-environment: Python found, set up needed; missing: no Python 3.12. */
  python: "ready" | "no-environment" | "missing";
  busy: "setup" | "train" | "rollback" | null;
  log: string[];
  /** Why the last action did not run, when it did not. */
  note: string | null;
  /** What the recorder said it uses after the last reload. */
  recorderModel: string | null;
  current: ModelSummary | null;
  history: ModelSummary[];
  lastRun: TrainingRun | null;
}

export interface LookbackApi {
  getState(): Promise<RecorderState>;
  onState(listener: (state: RecorderState) => void): () => void;
  startRecorder(): Promise<RecorderState>;
  stopRecorder(): Promise<RecorderState>;
  restartRecorder(): Promise<RecorderState>;
  getLog(): Promise<string[]>;
  onLog(listener: (line: string) => void): () => void;
  getSupervision(): Promise<SupervisionState>;
  onSupervision(listener: (state: SupervisionState) => void): () => void;
  getPaths(): Promise<ShellPaths>;
  getStream(): Promise<StreamState>;
  onStream(listener: (state: StreamState) => void): () => void;
  /** The viewer's ingest state, for the overview. */
  onIngest(listener: (state: IngestState) => void): () => void;
  /** Fired when an embedded page appears or disappears, e.g. after the recorder restarts. */
  onEmbedded(listener: (available: boolean) => void): () => void;
  onResized(listener: () => void): () => void;
  getLibrary(): Promise<LibrarySnapshot>;
  onLibrary(listener: (snapshot: LibrarySnapshot) => void): () => void;
  rate(sessionId: string, candidateId: string, rating: Rating): Promise<boolean>;
  /** Asks the recorder to cut and save a candidate's clip (live sessions). */
  saveClip(sessionId: string, candidateId: string): Promise<boolean>;
  /** The recorder's storage use, or null until it has reported. */
  getStorage(): Promise<StorageState | null>;
  /** Everything in the recorder's store; null when the recorder cannot say. */
  listStorage(): Promise<StorageListing | null>;
  /** Rated sessions whose ratings lack features; null when the recorder cannot say. */
  backfillPlan(): Promise<BackfillPlan | null>;
  /** Starts recomputing one session's features (asking for the footage file when it is a file session). Null when started; otherwise why not ("" when the file dialog was cancelled). */
  backfill(sessionId: string): Promise<string | null>;
  getTraining(): Promise<TrainingStatus>;
  onTraining(listener: (status: TrainingStatus) => void): () => void;
  /** Creates ml/.venv and installs the pinned packages (a download). Null when it ran; otherwise why not. */
  setupTraining(): Promise<string | null>;
  /** Trains; installs the model only if it beats the heuristic. Null when it ran; otherwise why not. */
  train(): Promise<string | null>;
  rollbackModel(): Promise<string | null>;
  /** Deletes a session's footage, a saved clip, a cached clip, or the whole cache; the reason when refused, else null. */
  deleteStored(kind: "session" | "clip" | "cache", target: { sessionId?: string; path?: string }): Promise<string | null>;
  /** Keeps a session out of the sweep, or lets it go. */
  keepSession(sessionId: string, keep: boolean): Promise<boolean>;
  /** Shows a stored file in the system file manager. */
  revealStored(path: string): Promise<boolean>;
  onStorage(listener: (state: StorageState) => void): () => void;
  /** Opens the folder holding a saved clip. */
  openClipsFolder(): Promise<boolean>;
  /** Asks for a footage file in a dialog and analyses it; null when cancelled. */
  openFootage(request: FootageRequest): Promise<FootageJob | null>;
  getFootage(): Promise<FootageJob[]>;
  onFootage(listener: (jobs: FootageJob[]) => void): () => void;
  /** Profile ids for the footage picker. */
  profiles(): Promise<string[]>;
  preview(sessionId: string, candidateId: string): Promise<PreviewSource | null>;
  forgetSession(sessionId: string): Promise<boolean>;
  /** Show an embedded page over `bounds` (in CSS pixels of the main window), or hide it with null. */
  showView(view: EmbeddedView | null, bounds: ViewBounds | null): Promise<boolean>;
  getBrowseState(): Promise<BrowseState>;
  buildBrowse(): Promise<BrowseResult>;
  /** The profile list and, with an id, that profile resolved (Profiles tab). */
  viewProfile(id: string | null): Promise<ProfileToolResult>;
  /** Saves edits to the checkout's profiles/; the recorder picks them up from its next recording. */
  saveProfile(id: string, edits: ProfileEdits): Promise<ProfileToolResult>;
  getSettings(): Promise<Settings>;
  saveSettings(settings: Settings): Promise<Settings>;
  /** Opens the official-player viewer, optionally with a pasted link. */
  openViewer(link: string | null): Promise<boolean>;
  /** Shows a clip the recorder saved in the file manager. */
  revealClip(path: string): Promise<boolean>;
}

export type ViewerPlatform = "youtube" | "twitch";

/** A video the viewer can play through an official embedded player. */
export type EmbedSource =
  | { platform: "youtube"; videoId: string; startSeconds: number }
  | { platform: "twitch"; kind: "channel"; channel: string }
  | { platform: "twitch"; kind: "video"; videoId: string };

export type PlaybackStatus = "idle" | "loading" | "playing" | "paused" | "ended" | "error";

/** Where the embedded player is in its video, as its own API says. */
export interface PlayerPosition {
  /** Seconds into the video. */
  seconds: number;
  /** Playback rate (1 is normal speed). */
  rate: number;
  /** False for a live channel, which cannot go back to a moment. */
  seekable: boolean;
}

/** What the viewer page reports to the main process. */
export interface ViewerReport {
  status: PlaybackStatus;
  /** The player's rectangle in CSS pixels of the viewer page. */
  rect: ViewBounds | null;
  /** The player's own position, when its API is ready. */
  position: PlayerPosition | null;
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
  /** Profile ids for the picker; empty when no profiles folder is known. */
  profiles(): Promise<string[]>;
  /** The chosen source (null when cleared) and profile ("" lets the recorder decide). */
  select(source: EmbedSource | null, profile: string): void;
  report(report: ViewerReport): void;
  onIngest(listener: (state: IngestState) => void): () => void;
  /** Shows or hides the docked calibration panel. */
  togglePanel(): void;
  /** The recorder asked the player to go back to a moment (a replay from the calibration panel). */
  onSeek(listener: (seconds: number) => void): () => void;
}
