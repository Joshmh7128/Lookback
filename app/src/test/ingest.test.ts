import assert from "node:assert/strict";
import { test } from "node:test";
import { encodeVideoFrame, INGEST_HEIGHT, INGEST_WIDTH, IngestSession, startMessage, VIDEO_HEADER_BYTES } from "../main/ingest";
import { socketFactory } from "./fakes";

test("a video frame is kind 1, int64 LE microseconds, uint16 LE width and height, then BGRA", () => {
  const px = new Uint8Array(4 * 3 * 2).map((_, i) => i);
  const m = encodeVideoFrame(px, 3, 2, 1_234_567.4);
  assert.equal(m.length, 13 + px.length);
  assert.equal(VIDEO_HEADER_BYTES, 13);
  const v = new DataView(m.buffer, m.byteOffset, m.byteLength);
  assert.equal(v.getUint8(0), 1);
  assert.equal(v.getBigInt64(1, true), 1_234_567n);
  assert.equal(v.getUint16(9, true), 3);
  assert.equal(v.getUint16(11, true), 2);
  assert.deepEqual([...m.subarray(13)], [...px]);
});

test("frame encoding rejects a wrong size and clamps negative times to zero", () => {
  assert.throws(() => encodeVideoFrame(new Uint8Array(10), 3, 2, 0), RangeError);
  assert.throws(() => encodeVideoFrame(new Uint8Array(0), 0, 0, 0), RangeError);
  assert.throws(() => encodeVideoFrame(new Uint8Array(4 * 70000), 70000, 1, 0), RangeError);
  const m = encodeVideoFrame(new Uint8Array(4), 1, 1, -5);
  assert.equal(new DataView(m.buffer).getBigInt64(1, true), 0n);
});

test("the start message omits a blank profile and carries nothing else", () => {
  assert.deepEqual(JSON.parse(startMessage({ platform: "youtube", videoRef: "abc", profile: "" })), { type: "start", platform: "youtube", videoRef: "abc" });
  assert.deepEqual(JSON.parse(startMessage({ platform: "twitch", videoRef: "channel:x", profile: "cs2" })), { type: "start", platform: "twitch", videoRef: "channel:x", profile: "cs2" });
});

test("a session sends start on open, frames with times since start, and stop on close", () => {
  const f = socketFactory();
  const session = new IngestSession("ws://127.0.0.1:5000/ingest", "TOKEN0123456789abcdef", { platform: "youtube", videoRef: "dQw4w9WgXcQ", profile: "cs2" }, f.create);
  const states: string[] = [];
  session.on("state", (s) => states.push(s.status));
  session.open();
  const socket = f.sockets[0]!;
  assert.equal(new URL(socket.url).searchParams.get("token"), "TOKEN0123456789abcdef");
  assert.equal(new URL(socket.url).pathname, "/ingest");

  // Nothing goes out before the socket opens.
  const frame = new Uint8Array(INGEST_WIDTH * INGEST_HEIGHT * 4);
  session.sendVideo(frame, INGEST_WIDTH, INGEST_HEIGHT);
  assert.equal(socket.sent.length, 0);

  socket.accept();
  assert.equal(session.streaming, true);
  assert.deepEqual(JSON.parse(socket.sent[0] as string), { type: "start", platform: "youtube", videoRef: "dQw4w9WgXcQ", profile: "cs2" });
  const t0 = performance.now();
  session.sendVideo(frame, INGEST_WIDTH, INGEST_HEIGHT, t0 + 100);
  const sent = socket.sent[1] as Uint8Array;
  const micros = Number(new DataView(sent.buffer).getBigInt64(1, true));
  assert.ok(micros >= 99_000 && micros <= 101_000 + 50_000, `micros ${micros}`);
  assert.equal(session.state.framesSent, 1);

  session.close();
  assert.deepEqual(JSON.parse(socket.sent[2] as string), { type: "stop" });
  assert.equal(socket.closedWith, 1000);
  assert.equal(session.state.status, "idle");
  assert.deepEqual(states, ["streaming", "idle"]);
});

test("frames are dropped, not queued, while the socket is backed up", () => {
  const f = socketFactory();
  const session = new IngestSession("ws://127.0.0.1:5000/ingest", "TOKEN0123456789abcdef", { platform: "youtube", videoRef: "x", profile: "" }, f.create);
  session.open();
  const socket = f.sockets[0]!;
  socket.accept();
  socket.bufferedAmount = 64 * 1024 * 1024;
  session.sendVideo(new Uint8Array(4), 1, 1);
  assert.equal(session.state.framesDropped, 1);
  assert.equal(socket.sent.length, 1); // just the start message
});

test("a refused or broken connection reports an error with no token in it", () => {
  const f = socketFactory();
  const session = new IngestSession("ws://127.0.0.1:5000/ingest", "SECRETTOKEN0123456789", { platform: "youtube", videoRef: "x", profile: "" }, f.create);
  session.open();
  f.sockets[0]!.fail();
  assert.equal(session.state.status, "error");
  assert.ok(!JSON.stringify(session.state).includes("SECRETTOKEN"));
});
