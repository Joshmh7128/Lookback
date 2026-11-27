// Preload for the official-player viewer page. Sandboxed and self-contained
// (type-only imports). It exposes `window.lookbackViewer`; the embedded
// players' own frames never see it (preloads run in the main frame only).
import { contextBridge, ipcRenderer, type IpcRendererEvent } from "electron";
import type { EmbedSource, IngestState, ViewerApi, ViewerReport } from "../shared/api";

function subscribe<T>(channel: string, listener: (value: T) => void): () => void {
  const wrapped = (_e: IpcRendererEvent, value: T): void => listener(value);
  ipcRenderer.on(channel, wrapped);
  return () => ipcRenderer.removeListener(channel, wrapped);
}

const api: ViewerApi = {
  parse: (input: string) => ipcRenderer.invoke("viewer:parse", input) as Promise<EmbedSource | null>,
  initialSource: () => ipcRenderer.invoke("viewer:initial") as Promise<EmbedSource | null>,
  onOpen: (listener) => subscribe<EmbedSource>("viewer:open", listener),
  profiles: () => ipcRenderer.invoke("viewer:profiles") as Promise<string[]>,
  select: (source: EmbedSource | null, profile: string) => ipcRenderer.send("viewer:select", source, profile),
  report: (report: ViewerReport) => ipcRenderer.send("viewer:report", report),
  onIngest: (listener) => subscribe<IngestState>("viewer:ingest", listener),
  togglePanel: () => ipcRenderer.send("viewer:toggle-panel"),
};

contextBridge.exposeInMainWorld("lookbackViewer", api);
