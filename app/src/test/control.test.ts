import assert from "node:assert/strict";
import { test } from "node:test";
import { analyzeMessage, historyEnvelopes, historyMessage, parseAck, parseStorage, previewMessage, previewPath, rateMessage, refusal, saveMessage, storageDeleteMessage, storageKeepMessage, storageMessage, validFootageRequest } from "../main/control";
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

test("save messages and save acks", () => {
  assert.deepEqual(JSON.parse(saveMessage("s1", "sess", "c9")), { type: "save", id: "s1", sessionId: "sess", candidateId: "c9" });
  const ok = parseAck({ type: "ack", id: "s1", ok: true, path: "D:\\Lookback\\clips\\sess-001.mp4" });
  assert.equal(ok?.path, "D:\\Lookback\\clips\\sess-001.mp4");
  const full = parseAck({ type: "ack", id: "s2", ok: false, error: "clips-full", message: "Saved clips are at the 10 GB budget." });
  assert.equal(refusal(full!), "Saved clips are at the 10 GB budget.");
});

test("storage status: protobuf JSON with uint64 strings and defaults left out", () => {
  const s = parseStorage({ sessionId: "recorder", topic: "recorder.storage", storageStatus: { clipsBytes: "9663676416", clipsBudgetBytes: "10737418240", footageBudgetBytes: "21474836480", clipsNearLimit: true, volumeFreeBytes: "500000000000" } }, 123);
  assert.deepEqual(s, { clipsBytes: 9663676416, clipsBudgetBytes: 10737418240, footageBytes: 0, footageBudgetBytes: 21474836480, clipsNearLimit: true, clipsFull: false, volumeFreeBytes: 500000000000, at: 123 });
  assert.equal(parseStorage({ sessionId: "s", topic: "session", sessionStarted: {} }), null);
  assert.equal(parseStorage(null), null);
  assert.equal(parseStorage({ storageStatus: { clipsBytes: "-5", clipsFull: "yes" } })?.clipsBytes, 0);
});

test("acks are recognised; envelopes are not", () => {
  assert.deepEqual(parseAck({ type: "ack", id: "r1", ok: true }), { id: "r1", ok: true, error: null, message: null, stored: false, sessionId: null, path: null, data: null });
  assert.deepEqual(parseAck({ type: "ack", id: "r1", ok: true, stored: true }), { id: "r1", ok: true, error: null, message: null, stored: true, sessionId: null, path: null, data: null });
  assert.deepEqual(parseAck({ type: "ack", id: "a1", ok: true, sessionId: "20261127-x" }), { id: "a1", ok: true, error: null, message: null, stored: false, sessionId: "20261127-x", path: null, data: null });
  const refused = parseAck({ type: "ack", id: "r1", ok: false, error: "ephemeral", message: "This session is ephemeral." });
  assert.deepEqual(refused, { id: "r1", ok: false, error: "ephemeral", message: "This session is ephemeral.", stored: false, sessionId: null, path: null, data: null });
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
  assert.deepEqual(await pending, { id: "r1", ok: false, error: "ephemeral", message: null, stored: false, sessionId: null, path: null, data: null });
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

test("a preview request names the candidate, and only a previews-folder .mp4 from an ok ack is played", () => {
  assert.deepEqual(JSON.parse(previewMessage("r1", "live-overwatch-1", "c7")), { type: "preview", id: "r1", sessionId: "live-overwatch-1", candidateId: "c7" });
  const windows = ["C:", "Users", "u", "AppData", "Local", "Lookback", "store", "previews", "live-1-c7.mp4"].join("\\");
  assert.equal(previewPath({ ok: true, path: windows }), windows);
  assert.equal(previewPath({ ok: true, path: "/home/u/store/previews/live-1-c7.mp4" }), "/home/u/store/previews/live-1-c7.mp4");
  for (const bad of [null, { ok: false, path: "/s/previews/a.mp4" }, { ok: true, path: null }, { ok: true, path: "/s/clips/a.mp4" }, { ok: true, path: "/s/previews/a.exe" }]) {
    assert.equal(previewPath(bad), null, JSON.stringify(bad));
  }
});

test("history, storage and their acks: data passes through, and only envelopes with a session come back", () => {
  assert.deepEqual(JSON.parse(historyMessage("h")), { type: "history", id: "h" });
  assert.deepEqual(JSON.parse(storageMessage("s")), { type: "storage", id: "s" });
  assert.deepEqual(JSON.parse(storageDeleteMessage("d", "clip", { path: "C:/store/clips/a.mp4" })), { type: "storage-delete", id: "d", kind: "clip", path: "C:/store/clips/a.mp4" });
  assert.deepEqual(JSON.parse(storageKeepMessage("k", "live-1", true)), { type: "storage-keep", id: "k", sessionId: "live-1", keep: true });
  const ack = parseAck({ type: "ack", id: "h", ok: true, data: [{ sessionId: "live-1", sessionStarted: {} }, { noSession: true }, 7, null] });
  assert.ok(ack);
  assert.equal(historyEnvelopes(ack).length, 1);
  assert.deepEqual(historyEnvelopes({ ok: false, data: [{ sessionId: "x" }] }), []);
  assert.deepEqual(historyEnvelopes(null), []);
});
