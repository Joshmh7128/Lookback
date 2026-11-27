import type { ViewBounds } from "../shared/api";

// Pure helpers for the viewer's frame path: which frames to keep, and which
// part of the captured page is the player. The pixels themselves are cropped
// and scaled by Chromium (NativeImage.crop/resize) and stay in memory.

/** Frames per second sent to the recorder's ingest. */
export const INGEST_FPS = 30;

/**
 * Keeps at most `fps` frames per second from a source that paints faster
 * (or irregularly). Due times advance by whole intervals so the average rate
 * holds at `fps` even when paints jitter, and never fall more than one
 * interval behind, so a stall does not cause a burst afterwards.
 */
export class FrameThrottle {
  private readonly interval: number;
  private next = Number.NEGATIVE_INFINITY;

  constructor(fps = INGEST_FPS) {
    if (!(fps > 0)) {
      throw new RangeError("fps must be positive");
    }

    this.interval = 1000 / fps;
  }

  /** True when a frame painted at `nowMs` should be sent. */
  take(nowMs: number): boolean {
    // A quarter-interval of slack, so a source painting at exactly `fps`
    // with a little jitter is not halved.
    if (nowMs < this.next - this.interval / 4) {
      return false;
    }

    const due = this.next + this.interval;
    this.next = due < nowMs ? nowMs + this.interval : due;
    return true;
  }

  reset(): void {
    this.next = Number.NEGATIVE_INFINITY;
  }
}

/**
 * The player's rectangle in captured-image pixels. `rect` is in CSS pixels
 * of the page, `viewWidth` is the page width in CSS pixels and `imageWidth`
 * and `imageHeight` the captured image size (device pixels). Returns null
 * when the player is not on screen or is too small to be worth sending.
 */
export function cropRect(rect: ViewBounds | null, viewWidth: number, imageWidth: number, imageHeight: number): ViewBounds | null {
  if (!rect || !(viewWidth > 0) || imageWidth <= 0 || imageHeight <= 0) {
    return null;
  }

  const scale = imageWidth / viewWidth;
  const x0 = clamp(Math.round(rect.x * scale), 0, imageWidth);
  const y0 = clamp(Math.round(rect.y * scale), 0, imageHeight);
  const x1 = clamp(Math.round((rect.x + rect.width) * scale), 0, imageWidth);
  const y1 = clamp(Math.round((rect.y + rect.height) * scale), 0, imageHeight);
  const width = x1 - x0;
  const height = y1 - y0;
  if (width < 32 || height < 18) {
    return null;
  }

  return { x: x0, y: y0, width, height };
}

/** Checks a rectangle that came from the viewer page, which is untrusted input. */
export function validRect(value: unknown): ViewBounds | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }

  const o = value as Record<string, unknown>;
  const n = (k: string): number | null => {
    const v = o[k];
    return typeof v === "number" && Number.isFinite(v) && Math.abs(v) < 100_000 ? v : null;
  };
  const x = n("x");
  const y = n("y");
  const width = n("width");
  const height = n("height");
  if (x === null || y === null || width === null || height === null || width <= 0 || height <= 0) {
    return null;
  }

  return { x, y, width, height };
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}
