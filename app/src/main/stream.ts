import { EventEmitter } from "node:events";
import type { StreamState } from "../shared/api";
import { ACK_TIMEOUT_MS, parseAck, type Ack } from "./control";

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
  private readonly pending = new Map<string, { resolve: (ack: Ack | null) => void; timer: NodeJS.Timeout }>();

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

  /**
   * Sends a client command (text JSON with an `id`) and resolves with the
   * recorder's ack, or null when the stream is not open or no ack arrives in
   * time. Never rejects.
   */
  request(id: string, text: string, timeoutMs = ACK_TIMEOUT_MS): Promise<Ack | null> {
    const socket = this.socket;
    if (!socket || this.current.status !== "open") {
      return Promise.resolve(null);
    }

    return new Promise((resolve) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        resolve(null);
      }, timeoutMs);
      this.pending.set(id, { resolve, timer });
      try {
        socket.send(text);
      } catch {
        clearTimeout(timer);
        this.pending.delete(id);
        resolve(null);
      }
    });
  }

  disconnect(): void {
    this.settlePending();
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

      const ack = parseAck(envelope);
      if (ack) {
        const waiting = this.pending.get(ack.id);
        if (waiting) {
          clearTimeout(waiting.timer);
          this.pending.delete(ack.id);
          waiting.resolve(ack);
        }

        return;
      }

      this.received++;
      this.emit("envelope", envelope);
    });
    socket.addEventListener("close", (e: CloseEvent) => {
      if (this.socket === socket) {
        this.socket = null;
        this.settlePending();
        this.retry(e.code === 1000 ? "the recorder closed the stream" : `the stream closed (${e.code})`);
      }
    });
  }

  private settlePending(): void {
    for (const { resolve, timer } of this.pending.values()) {
      clearTimeout(timer);
      resolve(null);
    }

    this.pending.clear();
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
