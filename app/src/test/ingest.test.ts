import assert from "node:assert/strict";
import { test } from "node:test";
import { encodeVideoFrame, INGEST_HEIGHT, INGEST_WIDTH, IngestSession, parseSeek, positionMessage, startMessage, validPosition, VIDEO_HEADER_BYTES } from "../main/ingest";
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

test("a position message carries the player's own time, rate and whether it can seek", () => {
  assert.deepEqual(JSON.parse(positionMessage(1_500_000.4, { seconds: 61.23456, rate: 1.5, seekable: true }, true)), {
    type: "position",
    t: 1_500_000,
    video: 61.235,
    rate: 1.5,
    playing: true,
    seekable: true,
  });
  assert.equal(JSON.parse(positionMessage(-3, { seconds: 0, rate: 1, seekable: false }, false)).t, 0);
});

test("positions from the page are checked before they go anywhere", () => {
  assert.deepEqual(validPosition({ seconds: 12.5, rate: 1, seekable: false }), { seconds: 12.5, rate: 1, seekable: false });
  for (const bad of [null, "x", {}, { seconds: -1, rate: 1, seekable: true }, { seconds: Number.NaN, rate: 1, seekable: true },
    { seconds: 1, rate: 0, seekable: true }, { seconds: 1, rate: 99, seekable: true }, { seconds: 1, rate: 1, seekable: "yes" }]) {
    assert.equal(validPosition(bad), null, JSON.stringify(bad));
  }
});

test("only a well-formed seek from the recorder becomes a seek", () => {
  assert.equal(parseSeek('{"type":"seek","seconds":42.5}'), 42.5);
  for (const bad of ['{"type":"seek","seconds":-1}', '{"type":"seek"}', '{"type":"stop","seconds":3}', "not json", '{"type":"seek","seconds":"3"}', 7]) {
    assert.equal(parseSeek(bad), null, String(bad));
  }
});

test("a session sends positions while streaming and passes the recorder's seeks on", () => {
  const f = socketFactory();
  const session = new IngestSession("ws://127.0.0.1:5000/ingest", "TOKEN0123456789abcdef", { platform: "youtube", videoRef: "abc", profile: "" }, f.create);
  const seeks: number[] = [];
  session.on("seek", (s) => seeks.push(s));
  session.open();
  const socket = f.sockets[0]!;
  session.sendPosition({ seconds: 5, rate: 1, seekable: true }, true);
  assert.equal(socket.sent.length, 0); // nothing before the socket opens

  socket.accept();
  const t0 = performance.now();
  session.sendPosition({ seconds: 5, rate: 1, seekable: true }, true, t0 + 250);
  const p = JSON.parse(socket.sent[1] as string);
  assert.equal(p.type, "position");
  assert.equal(p.video, 5);
  assert.ok(p.t >= 249_000 && p.t <= 251_000 + 50_000, `t ${p.t}`);

  socket.receive('{"type":"seek","seconds":30.5}');
  socket.receive("garbage");
  assert.deepEqual(seeks, [30.5]);
  session.close();
  socket.receive('{"type":"seek","seconds":1}'); // after close: not ours any more
  assert.deepEqual(seeks, [30.5]);
});
