import assert from "node:assert/strict";
import { test } from "node:test";
import { eventsUrl, isLoopbackUrl, LineSplitter, pageUrl, parseRecorderLine } from "../main/handshake";

const TOKEN = "8BC3F00DBA5EBA11CAFE0123456789AB";

test("the ready line gives pipe, ws, token, ingest and calibrate", () => {
  const line = parseRecorderLine(
    `LOOKBACK_READY {"pipe":"lookback.events","ws":"ws://127.0.0.1:45165/events","token":"${TOKEN}","ingest":"ws://127.0.0.1:45165/ingest","calibrate":"http://127.0.0.1:45165/calibrate"}\r`,
  );
  assert.equal(line.kind, "ready");
  if (line.kind !== "ready") return;
  assert.deepEqual(line.info, { pipe: "lookback.events", ws: "ws://127.0.0.1:45165/events", token: TOKEN, ingest: "ws://127.0.0.1:45165/ingest", calibrate: "http://127.0.0.1:45165/calibrate" });
});

test("ingest and calibrate are optional and extra fields are ignored", () => {
  const line = parseRecorderLine(`LOOKBACK_READY {"pipe":"p","ws":"ws://localhost:1/events","token":"${TOKEN}","future":42}`);
  assert.equal(line.kind, "ready");
  if (line.kind === "ready") {
    assert.equal(line.info.ingest, null);
    assert.equal(line.info.calibrate, null);
  }
});

test("malformed or non-loopback ready lines are refused without echoing the token", () => {
  const bad = [
    `LOOKBACK_READY not json ${TOKEN}`,
    `LOOKBACK_READY {"pipe":"p","ws":"ws://example.com/events","token":"${TOKEN}"}`,
    `LOOKBACK_READY {"pipe":"p","ws":"ws://127.0.0.1:1/events","token":"short"}`,
    `LOOKBACK_READY {"pipe":"bad pipe!","ws":"ws://127.0.0.1:1/events","token":"${TOKEN}"}`,
    `LOOKBACK_READY {"pipe":"p","ws":"ws://127.0.0.1:1/events","token":"${TOKEN}","ingest":"ws://10.0.0.1/ingest"}`,
    `LOOKBACK_READY {"pipe":"p","ws":"ws://127.0.0.1:1/events","token":"${TOKEN}","calibrate":"https://127.0.0.1/calibrate"}`,
    `LOOKBACK_READY ["${TOKEN}"]`,
  ];
  for (const raw of bad) {
    const line = parseRecorderLine(raw);
    assert.equal(line.kind, "invalid", raw);
    assert.ok(!JSON.stringify(line).includes(TOKEN), raw);
  }
});

test("profiler lines must be loopback http; anything else is a log line", () => {
  assert.deepEqual(parseRecorderLine(`LOOKBACK_PROFILER http://127.0.0.1:5/profiler#token=${TOKEN}`), { kind: "profiler", url: `http://127.0.0.1:5/profiler#token=${TOKEN}` });
  assert.equal(parseRecorderLine("LOOKBACK_PROFILER http://evil.example/profiler").kind, "invalid");
  assert.deepEqual(parseRecorderLine("info: hello"), { kind: "log", text: "info: hello" });
});

test("loopback URL checks", () => {
  assert.equal(isLoopbackUrl("ws://[::1]:9/x", "ws:"), true);
  assert.equal(isLoopbackUrl("ws://user:pw@127.0.0.1/x", "ws:"), false);
  assert.equal(isLoopbackUrl("http://127.0.0.1/x", "ws:"), false);
  assert.equal(isLoopbackUrl("ws://127.0.0.1.example.com/x", "ws:"), false);
});

test("the events URL carries the token as a query; pages carry it in the fragment", () => {
  const info = { pipe: "p", ws: "ws://127.0.0.1:7/events", token: TOKEN, ingest: null, calibrate: null };
  assert.equal(eventsUrl(info), `ws://127.0.0.1:7/events?token=${TOKEN}`);
  assert.equal(pageUrl("http://127.0.0.1:7/calibrate", TOKEN), `http://127.0.0.1:7/calibrate#token=${TOKEN}`);
});

test("the line splitter handles chunks, CRLF and runaway lines", () => {
  const lines: string[] = [];
  const s = new LineSplitter((l) => lines.push(l), 8);
  s.push("ab");
  s.push("c\r\nde\nf");
  assert.deepEqual(lines, ["abc\r", "de"]);
  s.push("0123456789");
  assert.equal(lines[2], "f0123456");
  s.push("x");
  s.flush();
  assert.equal(lines.at(-1), "x");
});
