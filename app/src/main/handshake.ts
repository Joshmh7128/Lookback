// Parses the recorder's stdout handshake. The recorder writes, on stdout only:
//
//   LOOKBACK_READY {"pipe":"lookback.events","ws":"ws://127.0.0.1:45165/events","token":"8BC3..."}
//   LOOKBACK_PROFILER http://127.0.0.1:45165/profiler#token=8BC3...   (with --Recorder:Profiler=true)
//
// The ready JSON may also carry "ingest" (ws:// official-player frame ingest)
// and "calibrate" (http:// calibration panel), both on the same token. Other
// extra fields are ignored, so the recorder can add more without breaking us.
//
// Everything else on stdout is passed through as an ordinary log line. The
// token is a secret: nothing here puts it into an error message.

export interface ReadyInfo {
  pipe: string;
  /** ws:// URL of /events, without the token. */
  ws: string;
  token: string;
  /** Optional ws:// URL of the official-player frame ingest, when the recorder offers one. */
  ingest: string | null;
  /** Optional http:// URL of the calibration panel, served on the same token. */
  calibrate: string | null;
}

export type RecorderLine =
  | { kind: "ready"; info: ReadyInfo }
  | { kind: "profiler"; url: string }
  | { kind: "invalid"; reason: string }
  | { kind: "log"; text: string };

export const READY_PREFIX = "LOOKBACK_READY ";
export const PROFILER_PREFIX = "LOOKBACK_PROFILER ";

const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]"]);
const TOKEN = /^[A-Za-z0-9_-]{16,256}$/;
const PIPE = /^[A-Za-z0-9._-]{1,128}$/;

export function parseRecorderLine(raw: string): RecorderLine {
  const line = raw.replace(/\r$/, "");
  if (line.startsWith(READY_PREFIX)) {
    return parseReady(line.slice(READY_PREFIX.length));
  }

  if (line.startsWith(PROFILER_PREFIX)) {
    const url = line.slice(PROFILER_PREFIX.length).trim();
    return isLoopbackUrl(url, "http:") ? { kind: "profiler", url } : { kind: "invalid", reason: "profiler link is not a loopback http URL" };
  }

  return { kind: "log", text: line };
}

function parseReady(json: string): RecorderLine {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    return { kind: "invalid", reason: "ready line is not JSON" };
  }

  if (typeof value !== "object" || value === null) {
    return { kind: "invalid", reason: "ready line is not a JSON object" };
  }

  const o = value as Record<string, unknown>;
  const { pipe, ws, token, ingest, calibrate } = o;
  if (typeof pipe !== "string" || !PIPE.test(pipe)) {
    return { kind: "invalid", reason: "ready line has no valid pipe name" };
  }

  if (typeof ws !== "string" || !isLoopbackUrl(ws, "ws:")) {
    return { kind: "invalid", reason: "ready line's ws address is not a loopback ws:// URL" };
  }

  if (typeof token !== "string" || !TOKEN.test(token)) {
    return { kind: "invalid", reason: "ready line has no valid token" };
  }

  let ingestUrl: string | null = null;
  if (ingest !== undefined && ingest !== null) {
    if (typeof ingest !== "string" || !isLoopbackUrl(ingest, "ws:")) {
      return { kind: "invalid", reason: "ready line's ingest address is not a loopback ws:// URL" };
    }

    ingestUrl = ingest;
  }

  let calibrateUrl: string | null = null;
  if (calibrate !== undefined && calibrate !== null) {
    if (typeof calibrate !== "string" || !isLoopbackUrl(calibrate, "http:")) {
      return { kind: "invalid", reason: "ready line's calibrate address is not a loopback http:// URL" };
    }

    calibrateUrl = calibrate;
  }

  return { kind: "ready", info: { pipe, ws, token, ingest: ingestUrl, calibrate: calibrateUrl } };
}

export function isLoopbackUrl(text: string, protocol: "ws:" | "http:"): boolean {
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    return false;
  }

  return url.protocol === protocol && LOOPBACK_HOSTS.has(url.hostname) && url.username === "" && url.password === "";
}

/** The /events URL with the token as a query parameter, which is what the bridge checks. */
export function eventsUrl(info: ReadyInfo): string {
  const url = new URL(info.ws);
  url.searchParams.set("token", info.token);
  return url.toString();
}

/** A page served by the recorder, with the token in the URL fragment, which never reaches a server. */
export function pageUrl(page: string, token: string): string {
  const url = new URL(page);
  url.hash = `token=${token}`;
  return url.toString();
}

/**
 * Splits a byte stream into lines. Lines longer than `maxLine` characters
 * are cut, so a runaway child cannot grow the buffer without bound.
 */
export class LineSplitter {
  private buffer = "";

  constructor(
    private readonly onLine: (line: string) => void,
    private readonly maxLine = 64 * 1024,
  ) {}

  push(chunk: string): void {
    this.buffer += chunk;
    let nl: number;
    while ((nl = this.buffer.indexOf("\n")) >= 0) {
      this.onLine(this.buffer.slice(0, nl));
      this.buffer = this.buffer.slice(nl + 1);
    }

    if (this.buffer.length > this.maxLine) {
      this.onLine(this.buffer.slice(0, this.maxLine));
      this.buffer = "";
    }
  }

  flush(): void {
    if (this.buffer.length > 0) {
      this.onLine(this.buffer);
      this.buffer = "";
    }
  }
}
