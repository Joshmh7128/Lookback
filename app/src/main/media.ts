import { createReadStream, statSync } from "node:fs";
import { randomBytes } from "node:crypto";
import { extname } from "node:path";
import { Readable } from "node:stream";

// Candidate previews: the main window's <video> plays footage the user opened,
// or a clip the recorder saved, straight from its file over a private
// `lookback-media://` scheme. Nothing is copied or transcoded. Only files
// registered here are served, under random ids; the URL never names the path.

export const MEDIA_SCHEME = "lookback-media";

const TYPES: Readonly<Record<string, string>> = {
  ".mp4": "video/mp4",
  ".m4v": "video/mp4",
  ".mov": "video/mp4",
  ".webm": "video/webm",
  ".mkv": "video/x-matroska",
};

/** Video files the preview can open, for the file dialog. */
export const VIDEO_EXTENSIONS = Object.keys(TYPES).map((e) => e.slice(1));

export class MediaRegistry {
  private readonly byId = new Map<string, string>();
  private readonly byPath = new Map<string, string>();

  /** The URL for a file, registering it on first use. */
  urlFor(path: string): string {
    let id = this.byPath.get(path);
    if (!id) {
      id = randomBytes(12).toString("hex");
      this.byId.set(id, path);
      this.byPath.set(path, id);
    }

    return `${MEDIA_SCHEME}://media/${id}${extname(path).toLowerCase()}`;
  }

  pathFor(url: string): string | null {
    let u: URL;
    try {
      u = new URL(url);
    } catch {
      return null;
    }

    if (u.protocol !== `${MEDIA_SCHEME}:` || u.host !== "media") {
      return null;
    }

    const id = /^\/([0-9a-f]{24})(\.[a-z0-9]+)?$/.exec(u.pathname)?.[1];
    return id ? (this.byId.get(id) ?? null) : null;
  }
}

/** Serves a registered file, honouring a single HTTP byte range (what <video> asks for when seeking). */
export function serveMedia(request: Request, registry: MediaRegistry): Response {
  if (request.method !== "GET" && request.method !== "HEAD") {
    return new Response(null, { status: 405 });
  }

  const path = registry.pathFor(request.url);
  if (!path) {
    return new Response(null, { status: 404 });
  }

  let size: number;
  try {
    const st = statSync(path);
    if (!st.isFile()) return new Response(null, { status: 404 });
    size = st.size;
  } catch {
    return new Response(null, { status: 404 });
  }

  const headers: Record<string, string> = {
    "Content-Type": TYPES[extname(path).toLowerCase()] ?? "application/octet-stream",
    "Accept-Ranges": "bytes",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff",
  };
  const range = parseRange(request.headers.get("range"), size);
  if (range === "invalid") {
    return new Response(null, { status: 416, headers: { ...headers, "Content-Range": `bytes */${size}` } });
  }

  const [start, end] = range ?? [0, size - 1];
  const length = size === 0 ? 0 : end - start + 1;
  headers["Content-Length"] = String(length);
  if (range) {
    headers["Content-Range"] = `bytes ${start}-${end}/${size}`;
  }

  const body = request.method === "HEAD" || length === 0 ? null : (Readable.toWeb(createReadStream(path, { start, end })) as ReadableStream<Uint8Array>);
  return new Response(body, { status: range ? 206 : 200, headers });
}

/** `bytes=a-b`, `bytes=a-` or `bytes=-n`; null for no header; "invalid" when unsatisfiable. Multiple ranges are not supported. */
export function parseRange(header: string | null, size: number): [number, number] | null | "invalid" {
  if (!header) {
    return null;
  }

  const m = /^bytes=(\d*)-(\d*)$/.exec(header.trim());
  if (!m || (m[1] === "" && m[2] === "")) {
    return "invalid";
  }

  let start: number;
  let end: number;
  if (m[1] === "") {
    const n = Number(m[2]);
    if (n === 0) return "invalid";
    start = Math.max(0, size - n);
    end = size - 1;
  } else {
    start = Number(m[1]);
    end = m[2] === "" ? size - 1 : Math.min(Number(m[2]), size - 1);
  }

  return start <= end && start < size ? [start, end] : "invalid";
}
