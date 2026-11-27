import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import type { ChildProcess } from "node:child_process";
import type { SessionView } from "../shared/api";
import type { Ack } from "../main/control";
import { FALLBACK_SPEED, fileName, FootageManager, type FootageDeps } from "../main/footage";
import type { Command } from "../main/locate";

class FakeChild extends EventEmitter {
  stdout = new PassThrough();
  stderr = new PassThrough();
  killed = false;
  kill(): boolean {
    this.killed = true;
    return true;
  }
}

function session(id: string, title: string, extra: Partial<SessionView> = {}): SessionView {
  return { id, order: 1, title, sourceKind: "file", retention: "owned", profile: "", qpcFrequency: 1e7, startQpc: 0, lastQpc: 0, ended: false, endReason: null, events: 0, signalFrames: 0, previewable: false, ...extra };
}

function setup(ack: Ack | null, options: { testbench?: boolean; pipe?: string | null } = {}) {
  const sent: string[] = [];
  const spawned: Command[] = [];
  const children: FakeChild[] = [];
  const sessions: SessionView[] = [];
  const linked: string[] = [];
  const deps: FootageDeps = {
    request: async (_id, text) => {
      sent.push(text);
      return ack && { ...ack, id: JSON.parse(text).id };
    },
    pipe: () => (options.pipe === undefined ? "lookback.events" : options.pipe),
    testbench: (args) => (options.testbench === false ? null : { file: "lookback-testbench.exe", args, cwd: ".", display: "lookback-testbench.exe" }),
    sessionsByTitle: (title) => sessions.filter((s) => s.title === title),
    linked: (id) => linked.push(id),
    log: () => undefined,
    spawn: (c) => {
      spawned.push(c);
      const child = new FakeChild();
      children.push(child);
      return child as unknown as ChildProcess;
    },
  };
  return { manager: new FootageManager(deps), sent, spawned, children, sessions, linked };
}

const FILE = "D:\\videos\\my match.mp4";
const ack = (ok: boolean, extra: Partial<Ack> = {}): Ack => ({ id: "", ok, error: null, message: null, stored: false, sessionId: null, path: null, ...extra });

test("the recorder's analyze command is tried first, and its session is linked by title", async () => {
  const t = setup(ack(true));
  const job = await t.manager.open(FILE, { profile: "cs2", retention: "owned", permissionRef: "" });
  assert.equal(job.via, "recorder");
  assert.equal(job.status, "analysing");
  assert.equal(job.name, "my match.mp4");
  assert.equal(JSON.parse(t.sent[0]!).type, "analyze");
  assert.equal(JSON.parse(t.sent[0]!).path, FILE);
  assert.equal(t.spawned.length, 0);

  t.sessions.push(session("other", "D:\\videos\\else.mp4"), session("s1", FILE));
  t.manager.link();
  assert.deepEqual(t.linked, ["s1"]);
  assert.equal(t.manager.pathFor("s1"), FILE);
  t.sessions[1]!.ended = true;
  t.manager.link();
  assert.equal(t.manager.list[0]!.status, "done");
});

test("without an ack the shell falls back to lookback-testbench live into the recorder's pipe", async () => {
  const t = setup(null);
  const job = await t.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  assert.equal(job.via, "testbench");
  assert.equal(job.profile, "generic");
  assert.deepEqual(t.spawned[0]!.args, ["live", FILE, "--profile", "generic", "--pipe", "lookback.events", "--no-wait", "--speed", String(FALLBACK_SPEED), "--title", FILE, "--retention", "owned"]);

  // A file session with that title shows up while it plays.
  t.sessions.push(session("s9", FILE));
  t.manager.link();
  assert.equal(t.manager.pathFor("s9"), FILE);
  t.children[0]!.emit("exit", 0);
  assert.equal(t.manager.list[0]!.status, "done");
});

test("an analyze ack naming the session links it as soon as the session appears", async () => {
  const t = setup(ack(true, { sessionId: "rec-1" }));
  const job = await t.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  assert.equal(job.sessionId, "rec-1");
  assert.deepEqual(t.linked, []); // not in the library yet
  t.sessions.push(session("rec-1", FILE));
  t.manager.link();
  assert.deepEqual(t.linked, ["rec-1"]);
  assert.equal(t.manager.pathFor("rec-1"), FILE);
});

test("permissioned footage passes its reference; a failing run reports why", async () => {
  const t = setup(null);
  await t.manager.open(FILE, { profile: "overwatch", retention: "permissioned", permissionRef: "licence #12" });
  const args = t.spawned[0]!.args;
  assert.deepEqual(args.slice(-4), ["--retention", "permissioned", "--permission-ref", "licence #12"]);
  t.children[0]!.stderr.write("Unhandled exception. Lookback.Stream.SessionLog.RetentionViolationException: refused\n");
  await new Promise((r) => setImmediate(r));
  t.children[0]!.emit("exit", 2);
  const job = t.manager.list[0]!;
  assert.equal(job.status, "failed");
  assert.match(job.detail ?? "", /RetentionViolation/);
});

test("a refusal from the recorder is shown with its message and not worked around", async () => {
  const t = setup(ack(false, { error: "bad-request", message: "analyze needs \"path\", an absolute path to a video file." }));
  const job = await t.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  assert.equal(job.status, "failed");
  assert.equal(job.detail, 'analyze needs "path", an absolute path to a video file.');
  assert.equal(t.spawned.length, 0);
  const bare = setup(ack(false, { error: "unsupported" }));
  assert.equal((await bare.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" })).detail, "refused (unsupported)");
});

test("no recorder or no testbench fails clearly", async () => {
  const noPipe = setup(null, { pipe: null });
  assert.equal((await noPipe.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" })).status, "failed");
  const noTool = setup(null, { testbench: false });
  const job = await noTool.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  assert.equal(job.status, "failed");
  assert.match(job.detail ?? "", /testbench was not found/);
});

test("each session is linked to one job only; stopAll kills running analyses", async () => {
  const t = setup(null);
  await t.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  await t.manager.open(FILE, { profile: "", retention: "owned", permissionRef: "" });
  t.sessions.push(session("a", FILE));
  t.manager.link();
  t.sessions.push(session("b", FILE));
  t.manager.link();
  const ids = t.manager.list.map((j) => j.sessionId).sort();
  assert.deepEqual(ids, ["a", "b"]);
  t.manager.stopAll();
  assert.ok(t.children.every((c) => c.killed));
});

test("file names come from Windows and POSIX paths alike", () => {
  assert.equal(fileName("D:\\videos\\my match.mp4"), "my match.mp4");
  assert.equal(fileName("/home/me/videos/match.mp4"), "match.mp4");
  assert.equal(fileName("match.mp4"), "match.mp4");
});
