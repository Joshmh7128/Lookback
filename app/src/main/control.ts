import { randomUUID } from "node:crypto";
import type { FootageRequest, Rating, StorageState } from "../shared/api";

// Client commands on the recorder's /events socket, as agreed with the
// live-capture thread (recorder side pending). Text JSON, same token and
// Origin checks as the stream:
//
//   {"type":"rate","id","sessionId","candidateId","stars":0..5}          (0 clears)
//   {"type":"analyze","id","path","profile"?,"retention","permissionRef"?}
//   {"type":"save","id","sessionId","candidateId"}
//   {"type":"preview","id","sessionId","candidateId"}   (a temporary cut to watch, not a saved clip)
//   {"type":"history","id"}            recorded sessions' own envelopes, oldest first, in "data"
//   {"type":"storage","id"}            sessions, saved clips and the clip cache with sizes, in "data"
//   {"type":"storage-delete","id","kind":"session"|"clip"|"cache","sessionId"?,"path"?}
//   {"type":"storage-keep","id","sessionId","keep":true|false}
//
// The recorder answers the sending client only:
//
//   {"type":"ack","id","ok":true,"stored"?:true,"sessionId"?,"path"?}
//   {"type":"ack","id","ok":false,"error":"ephemeral|unknown-session|unknown-candidate|bad-request|unsupported|clips-full","message"}
//
// A stored rating's ack carries stored:true, and the recorder publishes
// ClipRating (Envelope field 20, topic clip.rating) to every client; an
// analyze ack carries the new session's id; a save ack the clip's path (and a
// clip.saved envelope follows); a preview ack the preview's path (nothing follows:
// a preview is not kept, it pins no footage and uses no clips budget). `message` is meant for the user.
// No ack within the timeout means a recorder that predates the commands (it
// ignores inbound messages): ratings stay in memory and footage goes
// through the testbench.

export const ACK_TIMEOUT_MS = 2000;

export interface Ack {
  id: string;
  ok: boolean;
  error: string | null;
  /** For the user: why it was refused. */
  message: string | null;
  /** A rating the recorder stored. */
  stored: boolean;
  /** The session an analyze started. */
  sessionId: string | null;
  /** Where a save wrote the clip. */
  path: string | null;
  /** A command's result (history, storage), as the recorder sent it. */
  data: unknown;
}

/** Saving remuxes from the recording, so it may take longer than a rating. */
export const SAVE_TIMEOUT_MS = 30_000;

/** A preview remuxes too; the user is waiting to watch, so it gives up sooner. */
export const PREVIEW_TIMEOUT_MS = 15_000;

/** What to tell the user about a refused command. */
export function refusal(ack: Ack): string {
  return ack.message ?? (ack.error ? `refused (${ack.error})` : "refused");
}

export function newRequestId(): string {
  return randomUUID();
}

/** An ack from the recorder, or null when `value` is not one. */
export function parseAck(value: unknown): Ack | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }

  const o = value as Record<string, unknown>;
  if (o["type"] !== "ack" || typeof o["id"] !== "string" || typeof o["ok"] !== "boolean") {
    return null;
  }

  const text = (k: string): string | null => (typeof o[k] === "string" ? (o[k] as string).slice(0, 1000) : null);
  return { id: o["id"], ok: o["ok"], error: text("error"), message: text("message"), stored: o["stored"] === true, sessionId: text("sessionId"), path: text("path"), data: o["data"] ?? null };
}

export function rateMessage(id: string, sessionId: string, candidateId: string, stars: Rating): string {
  return JSON.stringify({ type: "rate", id, sessionId, candidateId, stars });
}

export function saveMessage(id: string, sessionId: string, candidateId: string): string {
  return JSON.stringify({ type: "save", id, sessionId, candidateId });
}

/** Recorded sessions to rebuild the library with after a restart: a list of envelopes. */
export const HISTORY_TIMEOUT_MS = 10_000;

export function historyMessage(id: string): string {
  return JSON.stringify({ type: "history", id });
}

export function storageMessage(id: string): string {
  return JSON.stringify({ type: "storage", id });
}

export function storageDeleteMessage(id: string, kind: "session" | "clip" | "cache", target: { sessionId?: string; path?: string }): string {
  return JSON.stringify({ type: "storage-delete", id, kind, ...target });
}

export function storageKeepMessage(id: string, sessionId: string, keep: boolean): string {
  return JSON.stringify({ type: "storage-keep", id, sessionId, keep });
}

/** The envelopes of a history ack: objects with a session id; anything else is dropped. */
export function historyEnvelopes(ack: { ok: boolean; data: unknown } | null): Record<string, unknown>[] {
  if (!ack?.ok || !Array.isArray(ack.data)) return [];
  return ack.data.filter((e): e is Record<string, unknown> => typeof e === "object" && e !== null && typeof (e as Record<string, unknown>)["sessionId"] === "string");
}

export function previewMessage(id: string, sessionId: string, candidateId: string): string {
  return JSON.stringify({ type: "preview", id, sessionId, candidateId });
}

/** A preview path from the recorder's ack: an .mp4 in the store's previews folder, or null. */
export function previewPath(ack: { ok: boolean; path: string | null } | null): string | null {
  const p = ack?.ok ? ack.path : null;
  if (!p || !/\.mp4$/i.test(p)) return null;
  const parts = p.split(/[\\/]/);
  return parts.length >= 2 && parts[parts.length - 2]!.toLowerCase() === "previews" ? p : null;
}

/** A `recorder.storage` envelope's StorageStatus, or null when `envelope` is not one. */
export function parseStorage(envelope: unknown, now = Date.now()): StorageState | null {
  if (typeof envelope !== "object" || envelope === null) {
    return null;
  }

  const s = (envelope as Record<string, unknown>)["storageStatus"];
  if (typeof s !== "object" || s === null) {
    return null;
  }

  const o = s as Record<string, unknown>;
  // Protobuf JSON: uint64 as strings, defaults left out.
  const n = (k: string): number => {
    const v = typeof o[k] === "string" ? Number(o[k]) : typeof o[k] === "number" ? (o[k] as number) : 0;
    return Number.isFinite(v) && v >= 0 ? v : 0;
  };
  return {
    clipsBytes: n("clipsBytes"),
    clipsBudgetBytes: n("clipsBudgetBytes"),
    footageBytes: n("footageBytes"),
    footageBudgetBytes: n("footageBudgetBytes"),
    clipsNearLimit: o["clipsNearLimit"] === true,
    clipsFull: o["clipsFull"] === true,
    volumeFreeBytes: n("volumeFreeBytes"),
    at: now,
  };
}

export function analyzeMessage(id: string, path: string, request: FootageRequest): string {
  const msg: Record<string, string> = { type: "analyze", id, path, retention: request.retention };
  if (request.profile) {
    msg["profile"] = request.profile;
  }

  if (request.retention !== "owned") {
    msg["permissionRef"] = request.permissionRef;
  }

  return JSON.stringify(msg);
}

/** Checks a footage request that came from the renderer. */
export function validFootageRequest(value: unknown): FootageRequest | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }

  const o = value as Record<string, unknown>;
  const profile = typeof o["profile"] === "string" ? o["profile"].trim() : "";
  const retention = o["retention"];
  const ref = typeof o["permissionRef"] === "string" ? o["permissionRef"].trim() : "";
  if (!/^[A-Za-z0-9._-]{0,128}$/.test(profile)) {
    return null;
  }

  if (retention !== "owned" && retention !== "permissioned" && retention !== "open-license") {
    return null;
  }

  // Someone else's footage needs a recorded permission or licence (RetentionRules says the same).
  if (retention !== "owned" && (ref.length === 0 || ref.length > 512)) {
    return null;
  }

  return { profile, retention, permissionRef: retention === "owned" ? "" : ref };
}
