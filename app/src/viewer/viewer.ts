// The official-player viewer page. It plays a video through the platform's
// own embedded player (YouTube IFrame Player API, Twitch embed) and tells the
// main process what is playing and where the player is. It never reads the
// player's pixels itself: the main process captures the page.
import type { EmbedSource, IngestState, PlaybackStatus, ViewerApi } from "../shared/api";

declare global {
  interface Window {
    lookbackViewer: ViewerApi;
    onYouTubeIframeAPIReady?: () => void;
    YT?: YouTubeNamespace;
    Twitch?: { Player: TwitchPlayerClass };
  }
}

// The small parts of the two player APIs this page uses.
interface YouTubePlayer {
  destroy(): void;
}
interface YouTubeNamespace {
  Player: new (el: HTMLElement, options: Record<string, unknown>) => YouTubePlayer;
}
interface TwitchPlayer {
  addEventListener(event: string, listener: () => void): void;
  setVideo?(id: string, t: number): void;
}
interface TwitchPlayerClass {
  new (el: HTMLElement | string, options: Record<string, unknown>): TwitchPlayer;
  READY: string;
  PLAYING: string;
  PLAY: string;
  PAUSE: string;
  ENDED: string;
  OFFLINE: string;
}

const api = window.lookbackViewer;
const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;
const box = $<HTMLDivElement>("player-box");
const hint = $<HTMLParagraphElement>("hint");
const statusLine = $<HTMLElement>("status");
const profileSelect = $<HTMLSelectElement>("profile");
const ingestBadge = $<HTMLSpanElement>("ingest");

let current: EmbedSource | null = null;
let status: PlaybackStatus = "idle";
let teardown: (() => void) | null = null;
let heartbeat: number | null = null;

function rect(): { x: number; y: number; width: number; height: number } | null {
  if (!current) return null;
  const r = box.getBoundingClientRect();
  return r.width > 0 && r.height > 0 ? { x: r.left, y: r.top, width: r.width, height: r.height } : null;
}

function setStatus(next: PlaybackStatus, text?: string): void {
  status = next;
  statusLine.textContent = text ?? next;
  api.report({ status, rect: rect() });
  // While playing, report about once a second so the shell picks up layout
  // changes and can reconnect the ingest if the recorder restarted.
  if (status === "playing" && heartbeat === null) {
    heartbeat = window.setInterval(() => api.report({ status, rect: rect() }), 1000);
  } else if (status !== "playing" && heartbeat !== null) {
    window.clearInterval(heartbeat);
    heartbeat = null;
  }
}

function loadScript(src: string): Promise<void> {
  return new Promise((resolve, reject) => {
    if (document.querySelector(`script[src="${src}"]`)) {
      resolve();
      return;
    }

    const s = document.createElement("script");
    s.src = src;
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => reject(new Error(`could not load ${new URL(src).host}`));
    document.head.append(s);
  });
}

let youTubeReady: Promise<YouTubeNamespace> | null = null;
function youTubeApi(): Promise<YouTubeNamespace> {
  youTubeReady ??= new Promise<YouTubeNamespace>((resolve, reject) => {
    if (window.YT?.Player) {
      resolve(window.YT);
      return;
    }

    window.onYouTubeIframeAPIReady = () => (window.YT ? resolve(window.YT) : reject(new Error("YouTube player API missing")));
    loadScript("https://www.youtube.com/iframe_api").catch(reject);
  });
  return youTubeReady;
}

function freshMount(): HTMLDivElement {
  teardown?.();
  teardown = null;
  box.querySelector("#player")?.remove();
  box.querySelectorAll("iframe").forEach((f) => f.remove());
  const mount = document.createElement("div");
  mount.id = "player";
  box.prepend(mount);
  return mount;
}

async function play(source: EmbedSource): Promise<void> {
  current = source;
  hint.hidden = true;
  api.select(source, profileSelect.value);
  const mount = freshMount();
  setStatus("loading", `loading ${source.platform === "youtube" ? "YouTube" : "Twitch"} player…`);
  try {
    if (source.platform === "youtube") {
      await playYouTube(mount, source);
    } else {
      await playTwitch(mount, source);
    }
  } catch (err) {
    setStatus("error", err instanceof Error ? err.message : String(err));
  }
}

async function playYouTube(mount: HTMLDivElement, source: Extract<EmbedSource, { platform: "youtube" }>): Promise<void> {
  const YT = await youTubeApi();
  const player = new YT.Player(mount, {
    videoId: source.videoId,
    width: "100%",
    height: "100%",
    playerVars: { autoplay: 1, start: source.startSeconds, playsinline: 1, rel: 0, origin: location.origin },
    events: {
      onStateChange: (e: { data: number }) => {
        // -1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued
        const map: Record<number, PlaybackStatus> = { [-1]: "loading", 0: "ended", 1: "playing", 2: "paused", 3: "loading", 5: "paused" };
        setStatus(map[e.data] ?? "loading", e.data === 3 ? "buffering" : undefined);
      },
      onError: (e: { data: number }) => setStatus("error", `the YouTube player reported error ${e.data}`),
    },
  });
  teardown = () => player.destroy();
}

async function playTwitch(mount: HTMLDivElement, source: Extract<EmbedSource, { platform: "twitch" }>): Promise<void> {
  await loadScript("https://player.twitch.tv/js/embed/v1.js");
  const Player = window.Twitch?.Player;
  if (!Player) throw new Error("Twitch player API missing");
  const options: Record<string, unknown> = { width: "100%", height: "100%", parent: [location.hostname], autoplay: true, muted: false };
  if (source.kind === "channel") options["channel"] = source.channel;
  else options["video"] = source.videoId;
  const player = new Player(mount, options);
  player.addEventListener(Player.PLAYING, () => setStatus("playing"));
  player.addEventListener(Player.PLAY, () => setStatus("playing"));
  player.addEventListener(Player.PAUSE, () => setStatus("paused"));
  player.addEventListener(Player.ENDED, () => setStatus("ended"));
  player.addEventListener(Player.OFFLINE, () => setStatus("ended", "the channel is offline"));
  teardown = () => mount.replaceChildren();
}

function stop(): void {
  freshMount();
  current = null;
  hint.hidden = false;
  api.select(null, profileSelect.value);
  setStatus("idle");
}

$<HTMLFormElement>("open-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const text = $<HTMLInputElement>("link").value;
  if (!text.trim()) {
    stop();
    return;
  }

  void api.parse(text).then((source) => {
    if (source) void play(source);
    else statusLine.textContent = "That is not a YouTube video or a Twitch channel or video link.";
  });
});

profileSelect.addEventListener("change", () => {
  // A profile change starts a new analysis session on the next frame.
  api.select(current, profileSelect.value);
  if (status === "playing") api.report({ status, rect: rect() });
});
$<HTMLButtonElement>("panel").addEventListener("click", () => api.togglePanel());
window.addEventListener("resize", () => api.report({ status, rect: rect() }));
api.onOpen((source) => void play(source));
api.onIngest((state: IngestState) => {
  ingestBadge.className = `ingest ${state.status}`;
  ingestBadge.textContent =
    state.status === "streaming" ? `ingest ${state.framesSent} frames${state.framesDropped ? `, ${state.framesDropped} dropped` : ""}` : `ingest ${state.status}${state.detail ? `: ${state.detail}` : ""}`;
});

void api.profiles().then((ids) => {
  for (const id of ids) {
    const o = document.createElement("option");
    o.value = id;
    o.textContent = id;
    profileSelect.append(o);
  }
});
void api.initialSource().then((source) => {
  if (source) void play(source);
});
