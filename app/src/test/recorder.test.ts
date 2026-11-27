import assert from "node:assert/strict";
import { test } from "node:test";
import type { RecorderState } from "../shared/api";
import type { Command } from "../main/locate";
import { RecorderHost } from "../main/recorder";
import { until } from "./fakes";

const TOKEN = "FAKE0123456789ABCDEF0123456789AB";

/** A stand-in recorder: a Node child that prints the handshake and then waits. */
function fakeRecorder(script: string): Command {
  return { file: process.execPath, args: ["-e", script], cwd: process.cwd(), display: "fake-recorder" };
}

const READY = `process.stdout.write('info on stdout\\nLOOKBACK_READY {"pipe":"lookback.test","ws":"ws://127.0.0.1:4100/events","token":"${TOKEN}","ingest":"ws://127.0.0.1:4100/ingest","calibrate":"http://127.0.0.1:4100/calibrate"}\\nLOOKBACK_PROFILER http://127.0.0.1:4100/profiler#token=${TOKEN}\\n'); process.stderr.write('a log line\\n'); setInterval(() => {}, 1000);`;

test("the host reads the handshake, reports ready, and never exposes the token in state or log", async () => {
  const host = new RecorderHost();
  const states: RecorderState[] = [];
  host.on("state", (s) => states.push(s));
  host.start(fakeRecorder(READY));
  await until(() => host.state.status === "ready" && host.profiler !== null, 10_000, "ready");
  const s = host.state;
  assert.equal(s.pipe, "lookback.test");
  assert.equal(s.endpoint, "ws://127.0.0.1:4100/events");
  assert.equal(s.ingest, true);
  assert.equal(s.calibrate, true);
  assert.equal(s.profiler, true);
  assert.equal(host.connection?.token, TOKEN);
  assert.equal(host.calibrate, `http://127.0.0.1:4100/calibrate#token=${TOKEN}`);
  await until(() => host.logTail.includes("a log line"), 5000, "stderr log");
  assert.ok(!JSON.stringify(states).includes(TOKEN));
  assert.ok(!host.logTail.join("\n").includes(TOKEN));

  await host.stop();
  assert.equal(host.state.status, "stopped");
  assert.equal(host.connection, null);
  assert.equal(host.calibrate, null);
});

test("an unexpected exit is reported as exited", async () => {
  const host = new RecorderHost();
  host.start(fakeRecorder(`${READY.replace("setInterval(() => {}, 1000);", "")} setTimeout(() => process.exit(3), 200);`));
  await until(() => host.state.status === "exited", 10_000, "exit");
  assert.match(host.state.detail ?? "", /code 3/);
});

test("no handshake within the timeout fails and kills the child", async () => {
  const host = new RecorderHost({ readyTimeoutMs: 300 });
  host.start(fakeRecorder("setInterval(() => {}, 1000);"));
  await until(() => host.state.status === "failed", 10_000, "failed");
  await until(() => host.state.pid === null, 10_000, "child gone");
});

test("a recorder that cannot be spawned fails cleanly", async () => {
  const host = new RecorderHost();
  host.start({ file: "Z:\\does\\not\\exist.exe", args: [], cwd: process.cwd(), display: "missing" });
  await until(() => host.state.status === "failed", 10_000, "failed");
  host.reportFailure("not found");
  assert.equal(host.state.detail, "not found");
});
