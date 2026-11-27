import assert from "node:assert/strict";
import { test } from "node:test";
import { StreamClient } from "../main/stream";
import { socketFactory, until } from "./fakes";

test("the stream parses envelopes, counts them and ignores junk", () => {
  const f = socketFactory();
  const client = new StreamClient({ create: f.create });
  const got: unknown[] = [];
  client.on("envelope", (e) => got.push(e));
  client.connect("ws://127.0.0.1:1/events?token=SECRET");
  const s = f.sockets[0]!;
  assert.equal(client.state.status, "connecting");
  s.accept();
  assert.equal(client.state.status, "open");
  s.receive('{"sessionId":"a","topic":"session"}');
  s.receive("not json");
  assert.deepEqual(got, [{ sessionId: "a", topic: "session" }]);
  assert.equal(client.state.received, 1);
  assert.ok(!JSON.stringify(client.state).includes("SECRET"));
  client.disconnect();
  assert.equal(client.state.status, "off");
});

test("a dropped stream reconnects with backoff until disconnected", async () => {
  const f = socketFactory();
  const client = new StreamClient({ create: f.create, retryMs: 10, maxRetryMs: 40 });
  client.connect("ws://127.0.0.1:1/events?token=T");
  f.sockets[0]!.accept();
  f.sockets[0]!.drop(1006);
  assert.equal(client.state.status, "retrying");
  await until(() => f.sockets.length === 2, 2000, "reconnect");
  f.sockets[1]!.drop(1006);
  await until(() => f.sockets.length === 3, 2000, "second reconnect");
  f.sockets[2]!.accept();
  assert.equal(client.state.status, "open");
  client.disconnect();
  const n = f.sockets.length;
  await new Promise((r) => setTimeout(r, 100));
  assert.equal(f.sockets.length, n);
});

test("events from a replaced socket are ignored", () => {
  const f = socketFactory();
  const client = new StreamClient({ create: f.create });
  const got: unknown[] = [];
  client.on("envelope", (e) => got.push(e));
  client.connect("ws://127.0.0.1:1/events?token=A");
  const old = f.sockets[0]!;
  old.accept();
  client.connect("ws://127.0.0.1:2/events?token=B");
  old.receive('{"sessionId":"stale"}');
  assert.deepEqual(got, []);
});
