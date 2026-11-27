// Test doubles shared by the suites.

/** A WebSocket stand-in: records what is sent and lets a test drive its events. */
export class FakeSocket extends EventTarget {
  readyState = 0;
  bufferedAmount = 0;
  binaryType = "blob";
  readonly sent: (string | Uint8Array)[] = [];
  closedWith: number | null = null;

  constructor(readonly url: string) {
    super();
  }

  send(data: string | Uint8Array): void {
    if (this.readyState !== 1) throw new Error("not open");
    this.sent.push(data);
  }

  close(code = 1000): void {
    if (this.readyState === 3) return;
    this.closedWith = code;
    this.readyState = 3;
    this.dispatchEvent(Object.assign(new Event("close"), { code }));
  }

  // Driving the socket from the test.
  accept(): void {
    this.readyState = 1;
    this.dispatchEvent(new Event("open"));
  }

  receive(data: string): void {
    this.dispatchEvent(Object.assign(new Event("message"), { data }));
  }

  drop(code: number): void {
    this.readyState = 3;
    this.dispatchEvent(Object.assign(new Event("close"), { code }));
  }

  fail(): void {
    this.dispatchEvent(new Event("error"));
    this.drop(1006);
  }
}

/** A factory that keeps every socket it made. */
export function socketFactory(): { sockets: FakeSocket[]; create: (url: string) => WebSocket } {
  const sockets: FakeSocket[] = [];
  return {
    sockets,
    create: (url: string) => {
      const s = new FakeSocket(url);
      sockets.push(s);
      return s as unknown as WebSocket;
    },
  };
}

export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Waits until `check` is true, or fails after `timeoutMs`. */
export async function until(check: () => boolean, timeoutMs = 10_000, what = "condition"): Promise<void> {
  const start = Date.now();
  while (!check()) {
    if (Date.now() - start > timeoutMs) throw new Error(`timed out waiting for ${what}`);
    await delay(20);
  }
}
