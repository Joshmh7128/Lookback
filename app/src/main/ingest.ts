import { EventEmitter } from "node:events";
import { performance } from "node:perf_hooks";
import type { IngestState, ViewerPlatform } from "../shared/api";

// Wire format of the recorder's official-player ingest (ws://127.0.0.1:PORT/ingest?token=T):
//
//   text   {"type":"start","platform":"youtube"|"twitch","videoRef":"...","profile":"cs2"}
//          (profile omitted when detection should decide; videoRef is not kept by the recorder)
//   binary kind:u8 (1 = video), t:i64 LE microseconds since start (monotonic),
//          width:u16 LE, height:u16 LE, then BGRA8 pixels, tightly packed
//   text   {"type":"stop"}
//
// Frames exist only in memory, on their way to the recorder. Official-player
// sessions are ephemeral: nothing on this path touches disk.

export const FRAME_KIND_VIDEO = 1;
export const VIDEO_HEADER_BYTES = 1 + 8 + 2 + 2;
export const INGEST_WIDTH = 384;
export const INGEST_HEIGHT = 216;

export function encodeVideoFrame(bgra: Uint8Array, width: number, height: number, micros: number): Uint8Array<ArrayBuffer> {
  if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0 || width > 0xffff || height > 0xffff) {
    throw new RangeError("frame size out of range");
  }

  if (bgra.length !== width * height * 4) {
    throw new RangeError(`expected ${width * height * 4} bytes of BGRA, got ${bgra.length}`);
  }

  const out = new Uint8Array(VIDEO_HEADER_BYTES + bgra.length);
  const view = new DataView(out.buffer);
  view.setUint8(0, FRAME_KIND_VIDEO);
  view.setBigInt64(1, BigInt(Math.max(0, Math.round(micros))), true);
  view.setUint16(9, width, true);
  view.setUint16(11, height, true);
  out.set(bgra, VIDEO_HEADER_BYTES);
  return out;
}

export interface IngestStart {
  platform: ViewerPlatform;
  videoRef: string;
  /** Profile id, or empty to let detection decide. */
  profile: string;
}

export function startMessage(start: IngestStart): string {
  const msg: Record<string, string> = { type: "start", platform: start.platform, videoRef: start.videoRef };
  // A blank profile is left out, so the recorder's detection decides.
  if (start.profile) {
    msg["profile"] = start.profile;
  }

  return JSON.stringify(msg);
}

// WebSocket ready states, as numbers so a test double needs no global class.
const CONNECTING = 0;
const OPEN = 1;

/** Frames queued in the socket beyond this are dropped rather than buffered. */
const MAX_BUFFERED_BYTES = 4 * 1024 * 1024;

interface IngestEvents {
  state: [IngestState];
}

/** One official-player session streamed to the recorder's ingest. */
export class IngestSession extends EventEmitter<IngestEvents> {
  private socket: WebSocket | null = null;
  private t0 = 0;
  private sent = 0;
  private dropped = 0;
  private status: IngestState["status"] = "connecting";
  private detail: string | null = null;
  private lastEmit = 0;

  constructor(
    private readonly url: string,
    private readonly token: string,
    private readonly start: IngestStart,
    private readonly create: (url: string) => WebSocket = (url) => new WebSocket(url),
  ) {
    super();
  }

  open(): void {
    const url = new URL(this.url);
    url.searchParams.set("token", this.token);
    const socket = this.create(url.toString());
    socket.binaryType = "arraybuffer";
    this.socket = socket;
    socket.addEventListener("open", () => {
      socket.send(startMessage(this.start));
      this.t0 = performance.now();
      this.update("streaming", null);
    });
    socket.addEventListener("error", () => {
      if (this.socket === socket) this.update("error", "the ingest connection failed");
    });
    socket.addEventListener("close", (e) => {
      // After close() we no longer own the socket and have reported idle already.
      if (this.socket === socket && this.status !== "idle") {
        this.update(this.status === "error" ? "error" : "idle", e.code === 1000 ? null : `closed (${e.code})`);
      }
    });
  }

  get streaming(): boolean {
    return this.status === "streaming" && this.socket?.readyState === OPEN;
  }

  /** Sends one frame captured at `atMs` (performance.now() time; defaults to now). */
  sendVideo(bgra: Uint8Array, width: number, height: number, atMs = performance.now()): void {
    const socket = this.socket;
    if (!socket || !this.streaming) {
      return;
    }

    if (socket.bufferedAmount > MAX_BUFFERED_BYTES) {
      this.dropped++;
    } else {
      socket.send(encodeVideoFrame(bgra, width, height, (atMs - this.t0) * 1000));
      this.sent++;
    }

    // Counters go to the viewer about twice a second, not per frame.
    const now = performance.now();
    if (now - this.lastEmit > 500) {
      this.lastEmit = now;
      this.emit("state", this.state);
    }
  }

  close(): void {
    const socket = this.socket;
    this.socket = null;
    if (socket && socket.readyState === OPEN) {
      socket.send(JSON.stringify({ type: "stop" }));
      socket.close(1000, "stop");
    } else if (socket && socket.readyState === CONNECTING) {
      socket.close();
    }

    this.update("idle", null);
  }

  get state(): IngestState {
    return { status: this.status, framesSent: this.sent, framesDropped: this.dropped, detail: this.detail };
  }

  private update(status: IngestState["status"], detail: string | null): void {
    this.status = status;
    this.detail = detail;
    this.emit("state", this.state);
  }
}
