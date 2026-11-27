import assert from "node:assert/strict";
import { test } from "node:test";
import { cropRect, FrameThrottle, validRect } from "../main/frames";

function count(throttle: FrameThrottle, times: number[]): number {
  return times.filter((t) => throttle.take(t)).length;
}

test("a 60 Hz source is thinned to 30 fps", () => {
  const times = Array.from({ length: 600 }, (_, i) => i * (1000 / 60));
  const n = count(new FrameThrottle(30), times);
  assert.ok(n >= 298 && n <= 301, `got ${n}`);
});

test("a 144 Hz source is thinned to 30 fps", () => {
  const times = Array.from({ length: 1440 }, (_, i) => i * (1000 / 144));
  const n = count(new FrameThrottle(30), times);
  assert.ok(n >= 298 && n <= 302, `got ${n}`);
});

test("a jittery 30 Hz source keeps nearly every frame", () => {
  let seed = 7;
  const rand = (): number => ((seed = (seed * 16807) % 2147483647) / 2147483647 - 0.5) * 6; // ±3 ms
  const times = Array.from({ length: 300 }, (_, i) => i * (1000 / 30) + rand());
  const n = count(new FrameThrottle(30), times);
  assert.ok(n >= 290, `got ${n}`);
});

test("after a stall the throttle does not burst", () => {
  const t = new FrameThrottle(30);
  assert.equal(t.take(0), true);
  assert.equal(t.take(5000), true); // first frame after a 5 s pause
  // Painting at 240 Hz right after: still about 30 fps, not a catch-up burst.
  const after = Array.from({ length: 240 }, (_, i) => 5000 + (i + 1) * (1000 / 240));
  const n = count(t, after);
  assert.ok(n >= 29 && n <= 31, `got ${n}`);
});

test("a throttle rejects a non-positive rate", () => {
  assert.throws(() => new FrameThrottle(0), RangeError);
});

test("the crop maps CSS pixels to captured pixels at the display scale", () => {
  // Page 1266 CSS px wide captured at 1.5x.
  assert.deepEqual(cropRect({ x: 70, y: 59.3, width: 1126, height: 633.4 }, 1266, 1899, 1115), { x: 105, y: 89, width: 1689, height: 950 });
  assert.deepEqual(cropRect({ x: 10, y: 10, width: 640, height: 360 }, 1000, 1000, 800), { x: 10, y: 10, width: 640, height: 360 });
});

test("the crop is clamped to the image and refuses tiny or missing players", () => {
  assert.deepEqual(cropRect({ x: -50, y: -50, width: 500, height: 300 }, 1000, 1000, 800), { x: 0, y: 0, width: 450, height: 250 });
  assert.equal(cropRect({ x: 990, y: 790, width: 500, height: 300 }, 1000, 1000, 800), null);
  assert.equal(cropRect(null, 1000, 1000, 800), null);
  assert.equal(cropRect({ x: 0, y: 0, width: 10, height: 10 }, 1000, 1000, 800), null);
  assert.equal(cropRect({ x: 0, y: 0, width: 500, height: 300 }, 0, 1000, 800), null);
});

test("rectangles from the page are validated", () => {
  assert.deepEqual(validRect({ x: 1, y: 2, width: 3, height: 4, extra: 1 }), { x: 1, y: 2, width: 3, height: 4 });
  for (const bad of [null, 5, {}, { x: 1, y: 2, width: -3, height: 4 }, { x: "1", y: 2, width: 3, height: 4 }, { x: Infinity, y: 0, width: 1, height: 1 }, { x: 0, y: 0, width: 1e9, height: 1 }]) {
    assert.equal(validRect(bad), null, JSON.stringify(bad));
  }
});
