import { extname, join, normalize, relative, sep } from "node:path";

export const CONTENT_TYPES: Readonly<Record<string, string>> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".map": "application/json; charset=utf-8",
};

/**
 * Maps a request path onto a file under `root`, or null when the path
 * escapes the root, names a hidden file, or has a type we do not serve.
 */
export function resolveStatic(root: string, pathname: string, index = "index.html"): string | null {
  let decoded: string;
  try {
    decoded = decodeURIComponent(pathname);
  } catch {
    return null;
  }

  if (decoded.includes("\0") || decoded.includes("\\")) {
    return null;
  }

  const rel = decoded.replace(/^\/+/, "") || index;
  if (rel.split("/").some((part) => part.startsWith("."))) {
    return null;
  }

  const full = normalize(join(root, rel));
  const back = relative(root, full);
  if (back === "" || back.startsWith("..") || back.split(sep).includes("..")) {
    return null;
  }

  return CONTENT_TYPES[extname(full).toLowerCase()] ? full : null;
}

export function contentType(file: string): string {
  return CONTENT_TYPES[extname(file).toLowerCase()] ?? "application/octet-stream";
}

/**
 * Content-Security-Policy of the shell's own pages: our scripts and styles
 * only, and sockets only to the recorder on loopback.
 */
export const SHELL_CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data:",
  "connect-src ws://127.0.0.1:* ws://localhost:*",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

/**
 * The generated dataset browser is one self-contained page with inline
 * script and style and its data embedded. It may not load or connect to
 * anything.
 */
export const BROWSE_CSP = [
  "default-src 'none'",
  "script-src 'unsafe-inline'",
  "style-src 'unsafe-inline'",
  "img-src data:",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");
