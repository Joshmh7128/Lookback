import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { MediaRegistry, parseRange, serveMedia } from "../main/media";

const dir = mkdtempSync(join(tmpdir(), "lookback-media-test-"));
after(() => rmSync(dir, { recursive: true, force: true }));
const file = join(dir, "clip.mp4");
const bytes = Buffer.from(Array.from({ length: 1000 }, (_, i) => i % 256));
writeFileSync(file, bytes);

test("byte ranges", () => {
  assert.equal(parseRange(null, 1000), null);
  assert.deepEqual(parseRange("bytes=0-", 1000), [0, 999]);
  assert.deepEqual(parseRange("bytes=100-199", 1000), [100, 199]);
  assert.deepEqual(parseRange("bytes=900-5000", 1000), [900, 999]);
  assert.deepEqual(parseRange("bytes=-100", 1000), [900, 999]);
  for (const bad of ["bytes=1000-", "bytes=5-2", "bytes=-", "bytes=0-1,5-6", "items=0-1", "bytes=-0"]) assert.equal(parseRange(bad, 1000), "invalid", bad);
});

test("only registered files are served, under ids that do not name the path", () => {
  const reg = new MediaRegistry();
  const url = reg.urlFor(file);
  assert.match(url, /^lookback-media:\/\/media\/[0-9a-f]{24}\.mp4$/);
  assert.ok(!url.includes("clip"));
  assert.equal(reg.urlFor(file), url);
  assert.equal(reg.pathFor(url), file);
  assert.equal(reg.pathFor("lookback-media://media/000000000000000000000000.mp4"), null);
  assert.equal(reg.pathFor(`lookback-media://media/${encodeURIComponent(file)}`), null);
  assert.equal(reg.pathFor("app://lookback/index.html"), null);
});

test("a whole file, a range and an unsatisfiable range", async () => {
  const reg = new MediaRegistry();
  const url = reg.urlFor(file);
  const whole = serveMedia(new Request(url), reg);
  assert.equal(whole.status, 200);
  assert.equal(whole.headers.get("content-type"), "video/mp4");
  assert.equal(whole.headers.get("accept-ranges"), "bytes");
  assert.deepEqual(Buffer.from(await whole.arrayBuffer()), bytes);

  const part = serveMedia(new Request(url, { headers: { Range: "bytes=10-19" } }), reg);
  assert.equal(part.status, 206);
  assert.equal(part.headers.get("content-range"), "bytes 10-19/1000");
  assert.equal(part.headers.get("content-length"), "10");
  assert.deepEqual(Buffer.from(await part.arrayBuffer()), bytes.subarray(10, 20));

  const bad = serveMedia(new Request(url, { headers: { Range: "bytes=2000-" } }), reg);
  assert.equal(bad.status, 416);
  assert.equal(bad.headers.get("content-range"), "bytes */1000");

  assert.equal(serveMedia(new Request("lookback-media://media/ffffffffffffffffffffffff.mp4"), reg).status, 404);
  assert.equal(serveMedia(new Request(url, { method: "POST", body: "x" }), reg).status, 405);
});

test("a registered file that disappeared is a 404", () => {
  const reg = new MediaRegistry();
  const gone = join(dir, "gone.mp4");
  writeFileSync(gone, "x");
  const url = reg.urlFor(gone);
  rmSync(gone);
  assert.equal(serveMedia(new Request(url), reg).status, 404);
});
