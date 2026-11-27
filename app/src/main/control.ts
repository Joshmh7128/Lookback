import { randomUUID } from "node:crypto";
import type { FootageRequest, Rating } from "../shared/api";

// Client commands on the recorder's /events socket, as agreed with the
// live-capture thread (recorder side pending). Text JSON, same token and
// Origin checks as the stream:
//
//   {"type":"rate","id","sessionId","candidateId","stars":0..5}          (0 clears)
//   {"type":"analyze","id","path","profile"?,"retention","permissionRef"?}
//   {"type":"save","id","sessionId","candidateId"}                        (later)
//
// The recorder answers the sending client only:
//
//   {"type":"ack","id","ok":true,"stored"?:true,"sessionId"?}
//   {"type":"ack","id","ok":false,"error":"ephemeral|unknown-session|unknown-candidate|bad-request|unsupported","message"}
//
// A stored rating's ack carries stored:true, and the recorder publishes
// ClipRating (Envelope field 20, topic clip.rating) to every client; an
// analyze ack carries the new session's id. `message` is meant for the user.
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
}

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
  return { id: o["id"], ok: o["ok"], error: text("error"), message: text("message"), stored: o["stored"] === true, sessionId: text("sessionId") };
}

export function rateMessage(id: string, sessionId: string, candidateId: string, stars: Rating): string {
  return JSON.stringify({ type: "rate", id, sessionId, candidateId, stars });
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
