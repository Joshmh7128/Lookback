import type { EmbedSource } from "../shared/api";

const YT_ID = /^[A-Za-z0-9_-]{11}$/;
const TWITCH_CHANNEL = /^[A-Za-z0-9_]{3,25}$/;
const TWITCH_VIDEO = /^\d{1,15}$/;
const YT_HOSTS = new Set(["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com"]);
const TWITCH_HOSTS = new Set(["twitch.tv", "www.twitch.tv", "m.twitch.tv"]);
// Paths on twitch.tv that are not channels.
const TWITCH_RESERVED = new Set(["directory", "videos", "settings", "downloads", "jobs", "p", "search", "subscriptions", "inventory", "wallet", "drops", "turbo"]);

/**
 * Parses a pasted YouTube or Twitch link into something the official
 * embedded players can open. Only the ids are kept; the link itself is never
 * loaded, so nothing but the official player URL is ever fetched.
 */
export function parseEmbedSource(input: string): EmbedSource | null {
  const text = input.trim();
  if (text.length === 0 || text.length > 2048) {
    return null;
  }

  let url: URL;
  try {
    url = new URL(/^[a-z]+:\/\//i.test(text) ? text : `https://${text}`);
  } catch {
    return null;
  }

  if (url.protocol !== "https:" && url.protocol !== "http:") {
    return null;
  }

  const host = url.hostname.toLowerCase();
  const parts = url.pathname.split("/").filter((p) => p.length > 0);

  if (host === "youtu.be") {
    return youtube(parts[0], url);
  }

  if (YT_HOSTS.has(host)) {
    if (parts[0] === "watch") {
      return youtube(url.searchParams.get("v") ?? undefined, url);
    }

    if (parts[0] === "embed" || parts[0] === "shorts" || parts[0] === "live" || parts[0] === "v") {
      return youtube(parts[1], url);
    }

    return null;
  }

  if (TWITCH_HOSTS.has(host)) {
    if (parts[0] === "videos" && parts[1] && TWITCH_VIDEO.test(parts[1])) {
      return { platform: "twitch", kind: "video", videoId: parts[1] };
    }

    const channel = parts[0];
    if (channel && parts.length === 1 && TWITCH_CHANNEL.test(channel) && !TWITCH_RESERVED.has(channel.toLowerCase())) {
      return { platform: "twitch", kind: "channel", channel: channel.toLowerCase() };
    }

    return null;
  }

  return null;
}

function youtube(id: string | undefined, url: URL): EmbedSource | null {
  if (!id || !YT_ID.test(id)) {
    return null;
  }

  return { platform: "youtube", videoId: id, startSeconds: parseStart(url.searchParams.get("t") ?? url.searchParams.get("start")) };
}

/** "90", "90s", "1m30s", "1h2m3s" to seconds; anything else is 0. */
export function parseStart(value: string | null): number {
  if (!value) {
    return 0;
  }

  if (/^\d{1,6}$/.test(value)) {
    return Number(value);
  }

  const m = /^(?:(\d{1,3})h)?(?:(\d{1,4})m)?(?:(\d{1,6})s)?$/.exec(value);
  if (!m || value.length === 0) {
    return 0;
  }

  return Number(m[1] ?? 0) * 3600 + Number(m[2] ?? 0) * 60 + Number(m[3] ?? 0);
}

/** Checks a source that came back from a renderer, which is untrusted input. */
export function validateEmbedSource(value: unknown): EmbedSource | null {
  if (typeof value !== "object" || value === null) {
    return null;
  }

  const o = value as Record<string, unknown>;
  if (o["platform"] === "youtube" && typeof o["videoId"] === "string" && YT_ID.test(o["videoId"])) {
    const start = o["startSeconds"];
    return { platform: "youtube", videoId: o["videoId"], startSeconds: typeof start === "number" && Number.isInteger(start) && start >= 0 && start < 1e7 ? start : 0 };
  }

  if (o["platform"] === "twitch" && o["kind"] === "channel" && typeof o["channel"] === "string" && TWITCH_CHANNEL.test(o["channel"])) {
    return { platform: "twitch", kind: "channel", channel: o["channel"].toLowerCase() };
  }

  if (o["platform"] === "twitch" && o["kind"] === "video" && typeof o["videoId"] === "string" && TWITCH_VIDEO.test(o["videoId"])) {
    return { platform: "twitch", kind: "video", videoId: o["videoId"] };
  }

  return null;
}

/** The `videoRef` sent to the ingest: a stable, human-readable id for the source. */
export function videoRef(source: EmbedSource): string {
  switch (source.platform) {
    case "youtube":
      return source.videoId;
    case "twitch":
      return source.kind === "channel" ? `channel:${source.channel}` : `video:${source.videoId}`;
  }
}
