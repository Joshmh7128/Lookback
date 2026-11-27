import { EventEmitter } from "node:events";
import type { StreamState } from "../shared/api";

interface StreamEvents {
  state: [StreamState];
  envelope: [unknown];
}

export interface StreamClientOptions {
  /** Delay before the first reconnect; doubles up to `maxRetryMs`. */
  retryMs?: number;
  maxRetryMs?: number;
  create?: (url: string) => WebSocket;
}

/**
 * The main process's read-only connection to the recorder's /events stream.
 * Each text frame is one protobuf-JSON envelope. Reconnects with backoff
 * while a URL is set; `disconnect` stops it. The URL holds the token, so it
 * is never logged or put into a state.
 */
export class StreamClient extends EventEmitter<StreamEvents> {
  private url: string | null = null;
  private socket: WebSocket | null = null;
  private timer: NodeJS.Timeout | null = null;
  private delay: number;
  private received = 0;
  private current: StreamState = { status: "off", received: 0, detail: null };
  private readonly retryMs: number;
  private readonly maxRetryMs: number;
  private readonly create: (url: string) => WebSocket;

  constructor(options: StreamClientOptions = {}) {
    super();
    this.retryMs = options.retryMs ?? 500;
    this.maxRetryMs = options.maxRetryMs ?? 5000;
    this.create = options.create ?? ((url) => new WebSocket(url));
    this.delay = this.retryMs;
  }

  get state(): StreamState {
    return { ...this.current, received: this.received };
  }

  connect(url: string): void {
    this.disconnect();
    this.url = url;
    this.delay = this.retryMs;
    this.open();
  }

  disconnect(): void {
    this.url = null;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }

    const socket = this.socket;
    this.socket = null;
    socket?.close();
    this.set("off", null);
  }

  private open(): void {
    const url = this.url;
    if (!url) {
      return;
    }

    this.set("connecting", null);
    let socket: WebSocket;
    try {
      socket = this.create(url);
    } catch {
      this.retry("could not open the events socket");
      return;
    }

    this.socket = socket;
    socket.addEventListener("open", () => {
      if (this.socket === socket) {
        this.delay = this.retryMs;
        this.set("open", null);
      }
    });
    socket.addEventListener("message", (e: MessageEvent) => {
      if (this.socket !== socket || typeof e.data !== "string") {
        return;
      }

      let envelope: unknown;
      try {
        envelope = JSON.parse(e.data);
      } catch {
        return;
      }

      this.received++;
      this.emit("envelope", envelope);
    });
    socket.addEventListener("close", (e: CloseEvent) => {
      if (this.socket === socket) {
        this.socket = null;
        this.retry(e.code === 1000 ? "the recorder closed the stream" : `the stream closed (${e.code})`);
      }
    });
  }

  private retry(detail: string): void {
    if (!this.url) {
      return;
    }

    this.set("retrying", detail);
    this.timer = setTimeout(() => {
      this.timer = null;
      this.open();
    }, this.delay);
    this.delay = Math.min(this.maxRetryMs, this.delay * 2);
  }

  private set(status: StreamState["status"], detail: string | null): void {
    if (this.current.status === status && this.current.detail === detail) {
      return;
    }

    this.current = { status, received: this.received, detail };
    this.emit("state", this.state);
  }
}
