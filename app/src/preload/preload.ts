// Preload for the main window. Sandboxed, so it is one self-contained
// CommonJS file that imports only types. It exposes `window.lookback` and
// nothing else: no ipcRenderer, no Node.
import { contextBridge, ipcRenderer, type IpcRendererEvent } from "electron";
import type { EmbeddedView, FootageJob, FootageRequest, IngestState, LibrarySnapshot, LookbackApi, PreviewSource, ProfileEdits, ProfileToolResult, Rating, RecorderState, Settings, ShellPaths, StorageState, StreamState, SupervisionState, ViewBounds } from "../shared/api";

function subscribe<T>(channel: string, listener: (value: T) => void): () => void {
  const wrapped = (_e: IpcRendererEvent, value: T): void => listener(value);
  ipcRenderer.on(channel, wrapped);
  return () => ipcRenderer.removeListener(channel, wrapped);
}

const api: LookbackApi = {
  getState: () => ipcRenderer.invoke("lookback:get-state") as Promise<RecorderState>,
  onState: (listener) => subscribe<RecorderState>("lookback:state", listener),
  startRecorder: () => ipcRenderer.invoke("lookback:start") as Promise<RecorderState>,
  stopRecorder: () => ipcRenderer.invoke("lookback:stop") as Promise<RecorderState>,
  restartRecorder: () => ipcRenderer.invoke("lookback:restart") as Promise<RecorderState>,
  getLog: () => ipcRenderer.invoke("lookback:get-log") as Promise<string[]>,
  onLog: (listener) => subscribe<string>("lookback:log", listener),
  getSupervision: () => ipcRenderer.invoke("lookback:get-supervision") as Promise<SupervisionState>,
  onSupervision: (listener) => subscribe<SupervisionState>("lookback:supervision", listener),
  getPaths: () => ipcRenderer.invoke("lookback:get-paths") as Promise<ShellPaths>,
  getStream: () => ipcRenderer.invoke("lookback:get-stream") as Promise<StreamState>,
  onStream: (listener) => subscribe<StreamState>("lookback:stream", listener),
  onIngest: (listener) => subscribe<IngestState>("lookback:ingest", listener),
  onEmbedded: (listener) => subscribe<boolean>("lookback:embedded", listener),
  onResized: (listener) => subscribe<null>("lookback:resized", () => listener()),
  getLibrary: () => ipcRenderer.invoke("lookback:get-library") as Promise<LibrarySnapshot>,
  onLibrary: (listener) => subscribe<LibrarySnapshot>("lookback:library", listener),
  rate: (sessionId: string, candidateId: string, rating: Rating) => ipcRenderer.invoke("lookback:rate", sessionId, candidateId, rating) as Promise<boolean>,
  saveClip: (sessionId: string, candidateId: string) => ipcRenderer.invoke("lookback:save", sessionId, candidateId) as Promise<boolean>,
  getStorage: () => ipcRenderer.invoke("lookback:get-storage") as Promise<StorageState | null>,
  onStorage: (listener) => subscribe<StorageState>("lookback:storage", listener),
  openClipsFolder: () => ipcRenderer.invoke("lookback:open-clips") as Promise<boolean>,
  openFootage: (request: FootageRequest) => ipcRenderer.invoke("lookback:open-footage", request) as Promise<FootageJob | null>,
  getFootage: () => ipcRenderer.invoke("lookback:get-footage") as Promise<FootageJob[]>,
  onFootage: (listener) => subscribe<FootageJob[]>("lookback:footage", listener),
  profiles: () => ipcRenderer.invoke("lookback:profiles") as Promise<string[]>,
  preview: (sessionId: string, candidateId: string) => ipcRenderer.invoke("lookback:preview", sessionId, candidateId) as Promise<PreviewSource | null>,
  forgetSession: (sessionId: string) => ipcRenderer.invoke("lookback:forget", sessionId) as Promise<boolean>,
  showView: (view: EmbeddedView | null, bounds: ViewBounds | null) => ipcRenderer.invoke("lookback:show-view", view, bounds) as Promise<boolean>,
  getBrowseState: () => ipcRenderer.invoke("lookback:browse-state"),
  buildBrowse: () => ipcRenderer.invoke("lookback:build-browse"),
  viewProfile: (id: string | null) => ipcRenderer.invoke("lookback:profile-view", id) as Promise<ProfileToolResult>,
  saveProfile: (id: string, edits: ProfileEdits) => ipcRenderer.invoke("lookback:profile-save", id, edits) as Promise<ProfileToolResult>,
  getSettings: () => ipcRenderer.invoke("lookback:get-settings") as Promise<Settings>,
  saveSettings: (settings: Settings) => ipcRenderer.invoke("lookback:save-settings", settings) as Promise<Settings>,
  openViewer: (link: string | null) => ipcRenderer.invoke("lookback:open-viewer", link) as Promise<boolean>,
  revealClip: (path: string) => ipcRenderer.invoke("lookback:reveal", path) as Promise<boolean>,
};

contextBridge.exposeInMainWorld("lookback", api);
window.addEventListener("load", () => ipcRenderer.send("lookback:rendered"));
