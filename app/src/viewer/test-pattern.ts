// A synthetic "player" for the smoke test: an animated canvas in the same
// 16:9 box the real viewer uses, so the main process's capture, crop, scale
// and ingest path runs end to end without the network or a real video.
import type { IngestState, ViewerApi } from "../shared/api";

declare global {
  interface Window {
    lookbackViewer: ViewerApi;
  }
}

const api = window.lookbackViewer;
const box = document.getElementById("player-box") as HTMLDivElement;
const canvas = document.getElementById("canvas") as HTMLCanvasElement;
const ctx = canvas.getContext("2d")!;
const status = document.getElementById("status") as HTMLElement;
const badge = document.getElementById("ingest") as HTMLElement;

function rect(): { x: number; y: number; width: number; height: number } {
  const r = box.getBoundingClientRect();
  return { x: r.left, y: r.top, width: r.width, height: r.height };
}

let frame = 0;
function draw(): void {
  frame++;
  const t = frame / 60;
  ctx.fillStyle = `hsl(${(frame * 2) % 360} 40% 18%)`;
  ctx.fillRect(0, 0, 640, 360);
  ctx.fillStyle = "#f2f2f2";
  ctx.fillRect(320 + Math.sin(t) * 240 - 30, 150 + Math.cos(t * 1.3) * 100, 60, 60);
  // A static "HUD" block in the corner.
  ctx.fillStyle = "#3cc";
  ctx.fillRect(20, 300, 140, 40);
  requestAnimationFrame(draw);
}

api.select({ platform: "youtube", videoId: "testpattern", startSeconds: 0 }, "");
requestAnimationFrame(draw);
api.report({ status: "playing", rect: rect() });
window.setInterval(() => api.report({ status: "playing", rect: rect() }), 1000);
status.textContent = "playing";
api.onIngest((s: IngestState) => {
  badge.className = `ingest ${s.status}`;
  badge.textContent = `ingest ${s.status} ${s.framesSent}`;
});
