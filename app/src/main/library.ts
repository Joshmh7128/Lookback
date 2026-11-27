import type { CandidateView, LibrarySnapshot, Rating, SessionView, TimelineMark } from "../shared/api";

// The clip library: what the recorder's /events stream has said about each
// session, plus the user's ratings. It is built from the bus's JSON
// envelopes (protobuf JSON: camelCase names, 64-bit integers as strings,
// enums by name, default values left out).
//
// Everything here lives in memory. The shell writes no session data to disk:
// persistence of anything about a session belongs to the recorder, behind
// RetentionRules. Ratings are kept here until the recorder accepts them.

const MAX_SESSIONS = 50;
const MAX_MARKS = 5000;
const MAX_CANDIDATES = 2000;

interface SessionState {
  view: SessionView;
  marks: TimelineMark[];
  candidates: Map<string, CandidateView>;
}

type Json = Record<string, unknown>;

export class Library {
  private readonly sessions = new Map<string, SessionState>();
  private readonly ratings = new Map<string, Rating>();
  private version = 0;

  /** Bumped on every change, so a caller can tell whether to send a new snapshot. */
  get revision(): number {
    return this.version;
  }

  /** Applies one envelope from /events. Returns true when the library changed. */
  apply(envelope: unknown): boolean {
    if (typeof envelope !== "object" || envelope === null) {
      return false;
    }

    const e = envelope as Json;
    const sessionId = str(e["sessionId"]);
    if (!sessionId) {
      return false;
    }

    const qpc = num(e["qpc"]);
    if (isObject(e["sessionStarted"])) {
      this.started(sessionId, qpc, e["sessionStarted"]);
      return this.changed();
    }

    const s = this.sessions.get(sessionId) ?? this.implicit(sessionId, qpc);
    if (qpc > s.view.lastQpc) {
      s.view.lastQpc = qpc;
    }

    if (isObject(e["signalFrame"])) {
      // Counted, not stored: the profiler page shows signals. It still moves
      // the session's clock, which the live timeline follows.
      s.view.signalFrames++;
      return this.changed();
    }

    if (isObject(e["sessionEnded"])) {
      s.view.ended = true;
      s.view.endReason = str(e["sessionEnded"]["reason"]) || null;
      return this.changed();
    }

    if (isObject(e["gameEvent"])) {
      const g = e["gameEvent"];
      this.mark(s, { kind: "event", qpc: num(g["atQpc"]) || qpc, name: str(g["name"]) || "event", detail: list(g["evidence"]).join(", "), confidence: num(g["confidence"]) });
      s.view.events++;
      return this.changed();
    }

    if (isObject(e["profileDetected"])) {
      const p = e["profileDetected"];
      const id = str(p["profileId"]);
      if (id) {
        s.view.profile = id;
      }

      this.mark(s, { kind: "profile", qpc: num(p["atQpc"]) || qpc, name: id || "profile", detail: str(p["method"]).replace(/^DETECTION_METHOD_/, "").toLowerCase(), confidence: num(p["confidence"]) });
      return this.changed();
    }

    if (isObject(e["gameLifecycle"])) {
      const g = e["gameLifecycle"];
      const kind = str(g["kind"]).replace(/^KIND_/, "").toLowerCase() || "lifecycle";
      if (str(g["gameProfile"])) {
        s.view.profile = str(g["gameProfile"]);
      }

      this.mark(s, { kind: "lifecycle", qpc, name: kind, detail: str(g["exe"]), confidence: num(g["confidence"]) });
      return this.changed();
    }

    if (isObject(e["clipCandidate"])) {
      this.candidate(s, e["clipCandidate"]);
      return this.changed();
    }

    if (isObject(e["qaReport"])) {
      const q = e["qaReport"];
      const c = s.candidates.get(str(q["candidateId"]));
      if (!c) {
        return false;
      }

      c.qa = {
        passed: q["passed"] === true,
        checks: list(q["checks"]).filter(isObject).map((k) => ({ name: str(k["name"]), passed: k["passed"] === true, value: num(k["value"]), detail: str(k["detail"]) })),
      };
      return this.changed();
    }

    if (isObject(e["clipSaved"])) {
      const v = e["clipSaved"];
      const c = s.candidates.get(str(v["candidateId"]));
      const path = str(v["path"]);
      if (!c || !path) {
        return false;
      }

      c.savedPath = path;
      return this.changed();
    }

    return false;
  }

  /** Sets the user's rating of a candidate; 0 clears it. Returns false for an unknown candidate. */
  rate(sessionId: string, candidateId: string, rating: Rating): boolean {
    const c = this.sessions.get(sessionId)?.candidates.get(candidateId);
    if (!c) {
      return false;
    }

    const key = ratingKey(sessionId, candidateId);
    if (rating === 0) {
      this.ratings.delete(key);
    } else {
      this.ratings.set(key, rating);
    }

    c.rating = rating;
    return this.changed();
  }

  /** Drops a session the user no longer wants listed (memory only). */
  forget(sessionId: string): boolean {
    const s = this.sessions.get(sessionId);
    if (!s) {
      return false;
    }

    for (const id of s.candidates.keys()) {
      this.ratings.delete(ratingKey(sessionId, id));
    }

    this.sessions.delete(sessionId);
    return this.changed();
  }

  /** Whether a path is one the recorder reported as a saved clip. */
  isSavedClip(path: string): boolean {
    for (const s of this.sessions.values()) {
      for (const c of s.candidates.values()) {
        if (c.savedPath === path) {
          return true;
        }
      }
    }

    return false;
  }

  snapshot(): LibrarySnapshot {
    const sessions = [...this.sessions.values()]
      .sort((a, b) => b.view.order - a.view.order)
      .map((s) => ({
        ...s.view,
        marks: s.marks.map((m) => ({ ...m })),
        candidates: [...s.candidates.values()].map((c) => ({ ...c, reasons: [...c.reasons], qa: c.qa ? { passed: c.qa.passed, checks: c.qa.checks.map((k) => ({ ...k })) } : null })),
      }));
    return { revision: this.version, sessions };
  }

  private started(id: string, qpc: number, p: Json): void {
    const existing = this.sessions.get(id);
    const view: SessionView = existing?.view ?? emptyView(id, qpc, this.nextOrder());
    view.title = str(p["title"]) || view.title;
    view.sourceKind = str(p["sourceKind"]).replace(/^SOURCE_KIND_/, "").toLowerCase() || view.sourceKind;
    view.retention = str(p["retention"]).replace(/^RETENTION_/, "").toLowerCase() || view.retention;
    view.qpcFrequency = num(p["qpcFrequency"]) || view.qpcFrequency;
    view.profile = str(p["gameProfile"]) || view.profile;
    // A replayed SessionStarted (the bridge resends it to new clients) must
    // not move the session's start forward.
    if (!existing || view.startQpc === 0 || (qpc > 0 && qpc < view.startQpc)) {
      view.startQpc = qpc;
    }

    if (!existing) {
      this.add({ view, marks: [], candidates: new Map() });
    }
  }

  private implicit(id: string, qpc: number): SessionState {
    const s: SessionState = { view: emptyView(id, qpc, this.nextOrder()), marks: [], candidates: new Map() };
    this.add(s);
    return s;
  }

  private add(s: SessionState): void {
    this.sessions.set(s.view.id, s);
    while (this.sessions.size > MAX_SESSIONS) {
      // Oldest first; Map keeps insertion order.
      const oldest = this.sessions.keys().next().value as string;
      this.forget(oldest);
    }
  }

  private candidate(s: SessionState, c: Json): void {
    const id = str(c["candidateId"]);
    if (!id) {
      return;
    }

    const existing = s.candidates.get(id);
    const view: CandidateView = {
      id,
      sessionId: s.view.id,
      startQpc: num(c["startQpc"]),
      endQpc: num(c["endQpc"]),
      peakQpc: num(c["peakQpc"]),
      score: num(c["heuristicScore"]),
      modelScore: c["modelScore"] === undefined ? null : num(c["modelScore"]),
      highlight: num(c["highlightProbability"]),
      profile: str(c["gameProfile"]),
      reasons: list(c["reasons"]).map(str).filter((r) => r.length > 0),
      qa: existing?.qa ?? null,
      savedPath: existing?.savedPath ?? null,
      rating: this.ratings.get(ratingKey(s.view.id, id)) ?? 0,
    };
    s.candidates.set(id, view);
    if (s.candidates.size > MAX_CANDIDATES) {
      const oldest = s.candidates.keys().next().value as string;
      s.candidates.delete(oldest);
      this.ratings.delete(ratingKey(s.view.id, oldest));
    }
  }

  private mark(s: SessionState, m: Omit<TimelineMark, "detail" | "confidence"> & { detail: string; confidence: number }): void {
    s.marks.push(m);
    if (s.marks.length > MAX_MARKS) {
      s.marks.splice(0, s.marks.length - MAX_MARKS);
    }
  }

  private orderCounter = 0;

  private nextOrder(): number {
    return ++this.orderCounter;
  }

  private changed(): boolean {
    this.version++;
    return true;
  }
}

export function ratingKey(sessionId: string, candidateId: string): string {
  return `${sessionId}\u0000${candidateId}`;
}

/** Checks a rating that came from a renderer. */
export function validRating(value: unknown): value is Rating {
  return value === -1 || value === 0 || value === 1;
}

function emptyView(id: string, qpc: number, order: number): SessionView {
  return { id, order, title: "", sourceKind: "unspecified", retention: "unspecified", profile: "", qpcFrequency: 0, startQpc: qpc, lastQpc: qpc, ended: false, endReason: null, events: 0, signalFrames: 0 };
}

function isObject(v: unknown): v is Json {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function str(v: unknown): string {
  return typeof v === "string" ? v : "";
}

/** Protobuf JSON writes 64-bit integers as strings and floats as numbers (or "NaN"). */
function num(v: unknown): number {
  const n = typeof v === "number" ? v : typeof v === "string" ? Number(v) : 0;
  return Number.isFinite(n) ? n : 0;
}

function list(v: unknown): unknown[] {
  return Array.isArray(v) ? v : [];
}
