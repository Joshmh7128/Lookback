import { readFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { contentType, resolveStatic } from "./static-files";

/**
 * Policy for the official-player viewer page. It loads the official player
 * scripts (YouTube IFrame Player API, Twitch Embed) and lets their players
 * frame in; it connects to nothing itself. No other script, frame or host.
 */
export const VIEWER_CSP = [
  "default-src 'none'",
  "script-src 'self' https://www.youtube.com https://s.ytimg.com https://embed.twitch.tv",
  "frame-src https://www.youtube.com https://www.youtube-nocookie.com https://player.twitch.tv https://embed.twitch.tv",
  "style-src 'self'",
  "img-src 'self' data:",
  "connect-src 'none'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

/**
 * Serves the viewer page on loopback. The official embeds need a real web
 * origin: YouTube checks the embedding origin, and Twitch's `parent` must be
 * the page's host name and only allows plain http for `localhost`. So the
 * page is served from http://localhost:PORT rather than from app://.
 *
 * Only the viewer's own static files are served, and only to requests whose
 * Host is exactly `localhost:PORT`, which stops DNS-rebinding pages from
 * reading them through another name.
 */
export class ViewerServer {
  private server: Server | null = null;
  private port = 0;

  constructor(private readonly root: string) {}

  get origin(): string {
    return `http://localhost:${this.port}`;
  }

  url(page = "viewer.html"): string {
    return `${this.origin}/${page}`;
  }

  async start(): Promise<void> {
    if (this.server) {
      return;
    }

    const server = createServer((req, res) => {
      const host = req.headers.host;
      if (req.method !== "GET" || host !== `localhost:${this.port}`) {
        res.writeHead(req.method !== "GET" ? 405 : 421).end();
        return;
      }

      const path = new URL(req.url ?? "/", this.origin).pathname;
      const file = resolveStatic(this.root, path, "viewer.html");
      if (!file) {
        res.writeHead(404).end();
        return;
      }

      readFile(file).then(
        (body) => {
          res.writeHead(200, {
            "Content-Type": contentType(file),
            "Content-Security-Policy": VIEWER_CSP,
            "Cache-Control": "no-store",
            "X-Content-Type-Options": "nosniff",
            // The YouTube player needs to see which origin embeds it.
            "Referrer-Policy": "strict-origin-when-cross-origin",
          });
          res.end(body);
        },
        () => res.writeHead(404).end(),
      );
    });

    await new Promise<void>((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", () => resolve());
    });
    this.port = (server.address() as AddressInfo).port;
    this.server = server;
  }

  async stop(): Promise<void> {
    const server = this.server;
    this.server = null;
    if (server) {
      server.closeAllConnections();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  }
}
