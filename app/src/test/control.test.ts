import assert from "node:assert/strict";
import { test } from "node:test";
import { analyzeMessage, parseAck, rateMessage, refusal, validFootageRequest } from "../main/control";
import { StreamClient } from "../main/stream";
import { socketFactory } from "./fakes";

test("rate and analyze messages match the agreed /events contract", () => {
  assert.deepEqual(JSON.parse(rateMessage("r1", "s1", "c1", 4)), { type: "rate", id: "r1", sessionId: "s1", candidateId: "c1", stars: 4 });
  assert.deepEqual(JSON.parse(rateMessage("r2", "s1", "c1", 0)), { type: "rate", id: "r2", sessionId: "s1", candidateId: "c1", stars: 0 });
  assert.deepEqual(JSON.parse(analyzeMessage("a1", "D:\\v\\m.mp4", { profile: "cs2", retention: "owned", permissionRef: "ignored" })), { type: "analyze", id: "a1", path: "D:\\v\\m.mp4", profile: "cs2", retention: "owned" });
  assert.deepEqual(JSON.parse(analyzeMessage("a2", "x.mp4", { profile: "", retention: "permissioned", permissionRef: "email 2026-11-01" })), {
    type: "analyze",
    id: "a2",
    path: "x.mp4",
    retention: "permissioned",
    permissionRef: "email 2026-11-01",
  });
});

test("acks are recognised; envelopes are not", () => {
  assert.deepEqual(parseAck({ type: "ack", id: "r1", ok: true }), { id: "r1", ok: true, error: null, message: null, stored: false, sessionId: null });
  assert.deepEqual(parseAck({ type: "ack", id: "r1", ok: true, stored: true }), { id: "r1", ok: true, error: null, message: null, stored: true, sessionId: null });
  assert.deepEqual(parseAck({ type: "ack", id: "a1", ok: true, sessionId: "20261127-x" }), { id: "a1", ok: true, error: null, message: null, stored: false, sessionId: "20261127-x" });
  const refused = parseAck({ type: "ack", id: "r1", ok: false, error: "ephemeral", message: "This session is ephemeral." });
  assert.deepEqual(refused, { id: "r1", ok: false, error: "ephemeral", message: "This session is ephemeral.", stored: false, sessionId: null });
  assert.equal(refusal(refused!), "This session is ephemeral.");
  for (const no of [null, {}, { type: "ack", id: 1, ok: true }, { type: "ack", id: "x" }, { sessionId: "s", topic: "session" }]) assert.equal(parseAck(no), null);
});

test("footage requests: someone else's footage needs a permission reference", () => {
  assert.deepEqual(validFootageRequest({ profile: " cs2 ", retention: "owned", permissionRef: "x" }), { profile: "cs2", retention: "owned", permissionRef: "" });
  assert.deepEqual(validFootageRequest({ profile: "", retention: "open-license", permissionRef: "CC BY 4.0, link" }), { profile: "", retention: "open-license", permissionRef: "CC BY 4.0, link" });
  assert.equal(validFootageRequest({ profile: "", retention: "permissioned", permissionRef: "  " }), null);
  assert.equal(validFootageRequest({ profile: "", retention: "ephemeral", permissionRef: "" }), null);
  assert.equal(validFootageRequest({ profile: "../x", retention: "owned" }), null);
  assert.equal(validFootageRequest("owned"), null);
});

test("a request resolves with the matching ack and acks are not envelopes", async () => {
  const f = socketFactory();
  const client = new StreamClient({ create: f.create });
  const envelopes: unknown[] = [];
  client.on("envelope", (e) => envelopes.push(e));
  client.connect("ws://127.0.0.1:1/events?token=T");
  const s = f.sockets[0]!;
  s.accept();
  const pending = client.request("r1", rateMessage("r1", "s1", "c1", 5));
  assert.deepEqual(JSON.parse(s.sent[0] as string), { type: "rate", id: "r1", sessionId: "s1", candidateId: "c1", stars: 5 });
  s.receive(JSON.stringify({ type: "ack", id: "other", ok: true }));
  s.receive(JSON.stringify({ type: "ack", id: "r1", ok: false, error: "ephemeral" }));
  assert.deepEqual(await pending, { id: "r1", ok: false, error: "ephemeral", message: null, stored: false, sessionId: null });
  assert.deepEqual(envelopes, []);
  assert.equal(client.state.received, 0);
});

test("no ack in time, a closed stream, or no stream at all resolve null", async () => {
  const f = socketFactory();
  const client = new StreamClient({ create: f.create });
  assert.equal(await client.request("x", "{}"), null); // not connected
  client.connect("ws://127.0.0.1:1/events?token=T");
  f.sockets[0]!.accept();
  assert.equal(await client.request("slow", "{}", 30), null); // an old recorder ignores inbound messages
  const dropped = client.request("d", "{}", 5000);
  f.sockets[0]!.drop(1006);
  assert.equal(await dropped, null);
  client.disconnect();
});
