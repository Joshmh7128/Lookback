import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { isAbsolute, join } from "node:path";
import { after, test } from "node:test";
import type { StorageState } from "../shared/api";
import { analyzeMessage, newRequestId, parseStorage, rateMessage, saveMessage } from "../main/control";
import { eventsUrl } from "../main/handshake";
import { IngestSession } from "../main/ingest";
import { Library } from "../main/library";
import { locateTool, RECORDER, recorderArgs } from "../main/locate";
import { RecorderHost } from "../main/recorder";
import { DEFAULT_SETTINGS } from "../main/settings";
import { StreamClient } from "../main/stream";
import { until } from "./fakes";

// The shell against a recorder that handles control commands on /events
// (rate, analyze, save; parent-exit). Each test is skipped when the built
// recorder is missing or predates the commands (it never acks). Set
// LOOKBACK_TEST_FOOTAGE to an absolute path of a short video to also run
// analyze -> candidates -> a stored rating.

const repoRoot = join(__dirname, "..", "..", "..");
const ratingsDir = mkdtempSync(join(tmpdir(), "lookback-ratings-"));
after(() => rmSync(ratingsDir, { recursive: true, force: true }));
const footage = process.env["LOOKBACK_TEST_FOOTAGE"] ?? "";

function command(extra: string[] = []): ReturnType<typeof locateTool> {
  const args = [...recorderArgs({ ...DEFAULT_SETTINGS, pipeName: "lookback.apptest-control", profilerPage: false }, { datasetsDir: ratingsDir }), ...extra];
  return locateTool(RECORDER, "", { repoRoot, resourcesDir: null, platform: process.platform, env: process.env, exists: existsSync }, args);
}

interface Up {
  host: RecorderHost;
  stream: StreamClient;
  library: Library;
  control: boolean;
  down(): Promise<void>;
}

async function up(extra: string[] = []): Promise<Up | null> {
  const c = command(extra);
  if (!c) return null;
  const host = new RecorderHost({ readyTimeoutMs: 60_000 });
  const stream = new StreamClient();
  const library = new Library();
  stream.on("envelope", (e) => library.apply(e));
  host.start(c);
  await until(() => host.state.status === "ready", 60_000, "recorder ready");
  stream.connect(eventsUrl(host.connection!));
  await until(() => stream.state.status === "open", 10_000, "events open");
  const probe = newRequestId();
  const ack = await stream.request(probe, JSON.stringify({ type: "probe", id: probe }));
  const down = async (): Promise<void> => {
    stream.disconnect();
    await host.stop();
  };
  return { host, stream, library, control: ack !== null, down };
}

test("ratings on an ephemeral session are refused with a message, and unknown commands are bad requests", { timeout: 120_000 }, async (t) => {
  const r = await up();
  if (!r) return t.skip("Lookback.Recorder is not built");
  try {
    if (!r.control) return t.skip("this recorder predates control commands");
    const info = r.host.connection!;
    const ingest = new IngestSession(info.ingest!, info.token, { platform: "youtube", videoRef: "M7lc1UVf-VE", profile: "" });
    ingest.open();
    await until(() => ingest.streaming, 10_000, "ingest");
    await until(() => r.library.snapshot().sessions.some((s) => s.sourceKind === "official_player"), 10_000, "player session");
    const session = r.library.snapshot().sessions.find((s) => s.sourceKind === "official_player")!;
    const id = newRequestId();
    const ack = await r.stream.request(id, rateMessage(id, session.id, "any", 5));
    assert.equal(ack?.ok, false);
    assert.equal(ack?.error, "ephemeral");
    assert.ok((ack?.message ?? "").length > 10, "a human-readable reason");
    ingest.close();

    const bad = newRequestId();
    const nope = await r.stream.request(bad, JSON.stringify({ type: "dance", id: bad }));
    assert.equal(nope?.error, "bad-request");
    const save = newRequestId();
    // Before live capture every save is "unsupported"; with it, this one names a candidate that does not exist.
    const saved = await r.stream.request(save, JSON.stringify({ type: "save", id: save, sessionId: session.id, candidateId: "x" }), 30_000);
    assert.equal(saved?.ok, false);
    assert.ok(["unsupported", "unknown-candidate", "ephemeral"].includes(saved?.error ?? ""), saved?.error ?? "no ack");
    assert.ok(saved?.message);
  } finally {
    await r.down();
  }
});

test("analyze refuses a missing file with a message the shell can show", { timeout: 120_000 }, async (t) => {
  const r = await up();
  if (!r) return t.skip("Lookback.Recorder is not built");
  try {
    if (!r.control) return t.skip("this recorder predates control commands");
    const id = newRequestId();
    const ack = await r.stream.request(id, analyzeMessage(id, join(tmpdir(), "no-such-video-4c1d.mp4"), { profile: "", retention: "owned", permissionRef: "" }));
    assert.equal(ack?.ok, false);
    assert.ok(ack?.message, "a message for the user");
  } finally {
    await r.down();
  }
});

test("analyze, then a rating the recorder stores and broadcasts", { timeout: 180_000 }, async (t) => {
  if (!footage || !isAbsolute(footage) || !existsSync(footage)) return t.skip("set LOOKBACK_TEST_FOOTAGE to an absolute path of a short video");
  const r = await up();
  if (!r) return t.skip("Lookback.Recorder is not built");
  try {
    if (!r.control) return t.skip("this recorder predates control commands");
    const id = newRequestId();
    const ack = await r.stream.request(id, analyzeMessage(id, footage, { profile: "overwatch", retention: "owned", permissionRef: "" }));
    assert.equal(ack?.ok, true, ack?.message ?? "");
    assert.ok(ack?.sessionId);
    const sid = ack!.sessionId!;
    await until(() => (r.library.snapshot().sessions.find((s) => s.id === sid)?.candidates.length ?? 0) > 0, 120_000, "a candidate");
    const s = r.library.snapshot().sessions.find((x) => x.id === sid)!;
    assert.equal(s.title, footage, "the session title is the exact path");
    assert.equal(s.sourceKind, "file");
    assert.equal(s.signalFrames, 0, "no signal frames for analyze sessions");
    const c = s.candidates[0]!;
    r.library.rate(sid, c.id, 4);
    const rid = newRequestId();
    const rated = await r.stream.request(rid, rateMessage(rid, sid, c.id, 4));
    assert.equal(rated?.ok, true, rated?.message ?? "");
    assert.equal(rated?.stored, true);
    await until(() => r.library.candidateOf(sid, c.id)?.ratingStored === true, 10_000, "clip.rating broadcast");
    const files = readdirSync(ratingsDir, { recursive: true }).map(String).filter((f) => f.endsWith("ratings.jsonl"));
    assert.equal(files.length, 1, "ratings land in the datasets folder the shell passed");
    assert.match(readFileSync(join(ratingsDir, files[0]!), "utf8"), new RegExp(c.id));
  } finally {
    await r.down();
  }
});

test("the recorder exits when the process named by ParentProcessId does", { timeout: 120_000 }, async (t) => {
  // A stand-in parent that lives for a few seconds.
  const parent = spawn(process.execPath, ["-e", "setTimeout(() => {}, 4000)"], { stdio: "ignore" });
  const r = await up([`--Recorder:ParentProcessId=${parent.pid}`]);
  if (!r) {
    parent.kill();
    return t.skip("Lookback.Recorder is not built");
  }

  try {
    if (!r.control) return t.skip("this recorder predates parent-exit");
    await until(() => r.host.state.status !== "ready", 30_000, "the recorder to stop after its parent");
    assert.notEqual(r.host.state.status, "ready");
  } finally {
    parent.kill();
    await r.down();
  }
});

test("the recorder reports storage with both budgets, and refuses to save a footage file's candidate", { timeout: 180_000 }, async (t) => {
  const r = await up();
  if (!r) return t.skip("Lookback.Recorder is not built");
  try {
    if (!r.control) return t.skip("this recorder predates control commands");
    const states: StorageState[] = [];
    r.stream.on("envelope", (e) => {
      const s = parseStorage(e);
      if (s) states.push(s);
    });
    await until(() => states.length > 0, 15_000, "a recorder.storage envelope").catch(() => undefined);
    if (states.length === 0) return t.skip("this recorder does not report storage (before live capture)");
    const s = states[0]!;
    assert.ok(s.clipsBudgetBytes > 0 && s.footageBudgetBytes > 0, JSON.stringify(s));
    assert.equal(r.library.snapshot().sessions.some((x) => x.id === "recorder"), false, "storage is not a session");

    if (!footage || !isAbsolute(footage) || !existsSync(footage)) return;
    const id = newRequestId();
    const ack = await r.stream.request(id, analyzeMessage(id, footage, { profile: "overwatch", retention: "owned", permissionRef: "" }));
    const sid = ack!.sessionId!;
    await until(() => (r.library.snapshot().sessions.find((x) => x.id === sid)?.candidates.length ?? 0) > 0, 120_000, "a candidate");
    const c = r.library.snapshot().sessions.find((x) => x.id === sid)!.candidates[0]!;
    const save = newRequestId();
    const saved = await r.stream.request(save, saveMessage(save, sid, c.id), 30_000);
    assert.equal(saved?.ok, false);
    assert.equal(saved?.error, "unsupported");
    assert.ok(saved?.message);
  } finally {
    await r.down();
  }
});
