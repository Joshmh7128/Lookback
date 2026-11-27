import assert from "node:assert/strict";
import { test } from "node:test";
import { Library, validRating } from "../main/library";

const FREQ = "10000000";

function started(session: string, qpc: number, extra: Record<string, unknown> = {}): unknown {
  return {
    qpc: String(qpc),
    sessionId: session,
    topic: "session",
    sessionStarted: { sourceKind: "SOURCE_KIND_FILE", retention: "RETENTION_OWNED", title: "clip.mp4", qpcFrequency: FREQ, gameProfile: "overwatch", ...extra },
  };
}

function candidate(session: string, id: string, peak: number, score = 0.5): unknown {
  return {
    qpc: String(peak),
    sessionId: session,
    topic: "clip.candidate",
    clipCandidate: { candidateId: id, startQpc: String(peak - 50_000_000), endQpc: String(peak + 30_000_000), peakQpc: String(peak), heuristicScore: score, gameProfile: "overwatch", reasons: ["overwatch.elimination"] },
  };
}

test("a session, its events, candidates, QA and saved clip build up from envelopes", () => {
  const lib = new Library();
  assert.equal(lib.apply(started("s1", 1_000_000_000)), true);
  lib.apply({ qpc: "1010000000", sessionId: "s1", topic: "game.event", gameEvent: { name: "overwatch.elimination", atQpc: "1010000000", confidence: 0.9, evidence: ["killfeed"] } });
  lib.apply({ qpc: "1011000000", sessionId: "s1", topic: "game.profile", profileDetected: { profileId: "overwatch", method: "DETECTION_METHOD_VISUAL", confidence: 0.8, atQpc: "1011000000" } });
  lib.apply(candidate("s1", "c1", 1_100_000_000, 0.7));
  lib.apply({ qpc: "1200000000", sessionId: "s1", topic: "qa.report", qaReport: { candidateId: "c1", passed: true, checks: [{ name: "black", passed: true, value: 0.01 }] } });
  lib.apply({ qpc: "1300000000", sessionId: "s1", topic: "clip.saved", clipSaved: { candidateId: "c1", path: "D:\\clips\\c1.mp4" } });

  const s = lib.snapshot().sessions[0]!;
  assert.equal(s.title, "clip.mp4");
  assert.equal(s.sourceKind, "file");
  assert.equal(s.retention, "owned");
  assert.equal(s.qpcFrequency, 10_000_000);
  assert.equal(s.events, 1);
  assert.deepEqual(
    s.marks.map((m) => [m.kind, m.name]),
    [
      ["event", "overwatch.elimination"],
      ["profile", "overwatch"],
    ],
  );
  assert.equal(s.marks[1]!.detail, "visual");
  const c = s.candidates[0]!;
  assert.equal(c.score, 0.7);
  assert.equal(c.peakQpc, 1_100_000_000);
  assert.deepEqual(c.reasons, ["overwatch.elimination"]);
  assert.equal(c.qa?.passed, true);
  assert.equal(c.qa?.checks[0]?.name, "black");
  assert.equal(c.savedPath, "D:\\clips\\c1.mp4");
  assert.equal(lib.isSavedClip("D:\\clips\\c1.mp4"), true);
  assert.equal(lib.isSavedClip("C:\\Windows\\notepad.exe"), false);
});

test("a replayed SessionStarted does not reset the session or move its start", () => {
  const lib = new Library();
  lib.apply(started("s1", 1_000));
  lib.apply(candidate("s1", "c1", 5_000));
  lib.apply(started("s1", 9_000));
  const s = lib.snapshot().sessions[0]!;
  assert.equal(s.startQpc, 1_000);
  assert.equal(s.candidates.length, 1);
  assert.equal(lib.snapshot().sessions.length, 1);
});

test("star ratings stick, survive a re-sent candidate, and can be cleared", () => {
  const lib = new Library();
  lib.apply(started("s1", 0));
  lib.apply(candidate("s1", "c1", 100));
  assert.equal(lib.rate("s1", "c1", 4), true);
  assert.equal(lib.rate("s1", "c1", 1), true);
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.rating, 1);
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.ratingStored, false);
  lib.apply(candidate("s1", "c1", 100, 0.9)); // the clipper re-scores it
  const c = lib.snapshot().sessions[0]!.candidates[0]!;
  assert.equal(c.rating, 1);
  assert.equal(c.score, 0.9);
  assert.equal(lib.rate("s1", "c1", 0), true);
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.rating, 0);
  assert.equal(lib.rate("s1", "nope", 1), false);
  assert.equal(lib.rate("nope", "c1", 1), false);
});

test("ratings are 0 (unrated) to 5 stars", () => {
  for (const ok of [0, 1, 2, 3, 4, 5]) assert.equal(validRating(ok), true);
  for (const bad of [6, -1, 2.5, "3", null, undefined, NaN]) assert.equal(validRating(bad), false);
});

test("an acked rating is marked stored; a newer rating supersedes the ack", () => {
  const lib = new Library();
  lib.apply(started("s1", 0));
  lib.apply(candidate("s1", "c1", 100));
  lib.rate("s1", "c1", 3);
  lib.rate("s1", "c1", 5);
  assert.equal(lib.markStored("s1", "c1", 3), false); // the ack for the old value arrives late
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.ratingStored, false);
  assert.equal(lib.markStored("s1", "c1", 5), true);
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.ratingStored, true);
  lib.rate("s1", "c1", 2);
  assert.equal(lib.snapshot().sessions[0]!.candidates[0]!.ratingStored, false);
});

test("a ClipRating from the recorder (field 20, clip.rating) sets stars as stored", () => {
  const lib = new Library();
  lib.apply(started("s1", 0));
  lib.apply(candidate("s1", "c1", 100));
  assert.equal(lib.apply({ qpc: "200", sessionId: "s1", topic: "clip.rating", sourceId: "shell", clipRating: { candidateId: "c1", sessionId: "s1", stars: 4 } }), true);
  const c = lib.snapshot().sessions[0]!.candidates[0]!;
  assert.equal(c.rating, 4);
  assert.equal(c.ratingStored, true);
  assert.equal(lib.apply({ qpc: "201", sessionId: "s1", topic: "clip.rating", clipRating: { candidateId: "c1", sessionId: "s1", stars: 9 } }), false);
  assert.equal(lib.apply({ qpc: "202", sessionId: "s1", topic: "clip.rating", clipRating: { candidateId: "zz", sessionId: "s1", stars: 2 } }), false);
});

test("sessions opened as footage are previewable, and so are sessions with saved clips", () => {
  const lib = new Library();
  lib.apply(started("s1", 0, { title: "D:\\videos\\match.mp4" }));
  lib.apply(started("s2", 0));
  assert.deepEqual(lib.sessionsByTitle("D:\\videos\\match.mp4").map((s) => s.id), ["s1"]);
  assert.equal(lib.setPreviewable("s1", true), true);
  assert.equal(lib.setPreviewable("s1", true), false);
  lib.apply(candidate("s2", "c1", 100));
  lib.apply({ qpc: "300", sessionId: "s2", topic: "clip.saved", clipSaved: { candidateId: "c1", path: "D:\\clips\\c1.mp4" } });
  const byId = Object.fromEntries(lib.snapshot().sessions.map((s) => [s.id, s.previewable]));
  assert.deepEqual(byId, { s1: true, s2: true });
  assert.equal(lib.candidateOf("s2", "c1")?.savedPath, "D:\\clips\\c1.mp4");
  assert.equal(lib.sessionOf("nope"), null);
});

test("signal frames are counted, not stored, and move the session clock", () => {
  const lib = new Library();
  lib.apply(started("s1", 0));
  for (let i = 1; i <= 100; i++) lib.apply({ qpc: String(i * 333_333), sessionId: "s1", topic: "signals.video", signalFrame: { schemaId: 1, values: [1, 2, 3] } });
  const s = lib.snapshot().sessions[0]!;
  assert.equal(s.signalFrames, 100);
  assert.equal(s.lastQpc, 33_333_300);
  assert.equal(s.marks.length, 0);
});

test("an official-player session is listed like any other and ends", () => {
  const lib = new Library();
  lib.apply(started("p1", 0, { sourceKind: "SOURCE_KIND_OFFICIAL_PLAYER", retention: "RETENTION_EPHEMERAL", title: "YouTube embedded player" }));
  lib.apply({ qpc: "10", sessionId: "p1", topic: "session", sessionEnded: { reason: "stopped" } });
  const s = lib.snapshot().sessions[0]!;
  assert.equal(s.sourceKind, "official_player");
  assert.equal(s.retention, "ephemeral");
  assert.equal(s.ended, true);
  assert.equal(s.endReason, "stopped");
});

test("events before SessionStarted create the session; junk is ignored", () => {
  const lib = new Library();
  assert.equal(lib.apply(candidate("late", "c1", 100)), true);
  assert.equal(lib.snapshot().sessions[0]!.id, "late");
  for (const junk of [null, 1, "x", [], {}, { sessionId: "" }, { sessionId: "s", qaReport: { candidateId: "missing" } }]) {
    assert.equal(lib.apply(junk), false, JSON.stringify(junk));
  }
});

test("newest sessions come first; the oldest are dropped past the cap; forget removes one", () => {
  const lib = new Library();
  for (let i = 0; i < 55; i++) lib.apply(started(`s${i}`, i));
  const ids = lib.snapshot().sessions.map((s) => s.id);
  assert.equal(ids.length, 50);
  assert.equal(ids[0], "s54");
  assert.ok(!ids.includes("s0"));
  assert.equal(lib.forget("s54"), true);
  assert.equal(lib.forget("s54"), false);
  assert.equal(lib.snapshot().sessions[0]!.id, "s53");
});

test("the revision moves on every change, so the shell sends snapshots only when needed", () => {
  const lib = new Library();
  const r0 = lib.revision;
  lib.apply(started("s1", 0));
  assert.ok(lib.revision > r0);
  const r1 = lib.revision;
  lib.apply(null);
  assert.equal(lib.revision, r1);
});
