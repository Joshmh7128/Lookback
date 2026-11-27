// The main window: overview, clip library with ratings, live timeline, the
// recorder's profiler page, the dataset browser and settings. Everything
// comes from the main process through `window.lookback`; this page opens no
// sockets and stores nothing.
import type { CandidateView, EmbeddedView, IngestState, LibrarySessionSnapshot, LibrarySnapshot, LookbackApi, Rating, RecorderState, Settings, StreamState, TimelineMark } from "../shared/api";

declare global {
  interface Window {
    lookback: LookbackApi;
  }
}

const api = window.lookback;
const $ = <T extends HTMLElement = HTMLElement>(id: string): T => document.getElementById(id) as T;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<Record<string, string>> = {}, ...children: (Node | string)[]): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined) continue;
    if (k === "class") node.className = v;
    else node.setAttribute(k, v);
  }

  node.append(...children);
  return node;
}

let library: LibrarySnapshot = { revision: 0, sessions: [] };
let selectedSession: string | null = null;
let selectedCandidate: string | null = null;
let activeTab = "overview";

// ------------------------------------------------------------------- tabs

const embeds: Record<string, { view: EmbeddedView; host: string }> = {
  profiler: { view: "profiler", host: "profiler-host" },
  datasets: { view: "browse", host: "browse-host" },
};

function showTab(name: string): void {
  activeTab = name;
  for (const b of document.querySelectorAll<HTMLButtonElement>("#tabs button[data-tab]")) {
    b.classList.toggle("active", b.dataset["tab"] === name);
  }

  for (const s of document.querySelectorAll<HTMLElement>(".tab")) {
    s.classList.toggle("active", s.id === `tab-${name}`);
  }

  placeEmbed();
  if (name === "timeline") drawTimeline();
}

function placeEmbed(): void {
  const embed = embeds[activeTab];
  if (!embed) {
    void api.showView(null, null);
    return;
  }

  const r = $(embed.host).getBoundingClientRect();
  void api.showView(embed.view, { x: r.left, y: r.top, width: r.width, height: r.height }).then((shown) => {
    $(embed.host).style.visibility = shown ? "hidden" : "visible";
  });
}

for (const b of document.querySelectorAll<HTMLButtonElement>("#tabs button[data-tab]")) {
  b.addEventListener("click", () => showTab(b.dataset["tab"] ?? "overview"));
}

window.addEventListener("resize", () => {
  placeEmbed();
  if (activeTab === "timeline") drawTimeline();
});
api.onResized(() => placeEmbed());
api.onEmbedded(() => placeEmbed());

// --------------------------------------------------------------- overview

function renderRecorder(state: RecorderState): void {
  const pill = $("pill-recorder");
  pill.textContent = `recorder ${state.status}`;
  pill.className = `pill ${state.status === "ready" ? "good" : state.status === "starting" ? "warn" : state.status === "stopped" ? "" : "bad"}`;
  const facts: [string, string][] = [
    ["Status", state.status + (state.detail ? ` — ${state.detail}` : "")],
    ["Command", state.command ?? "—"],
    ["PID", state.pid?.toString() ?? "—"],
    ["Pipe", state.pipe ?? "—"],
    ["Events", state.endpoint ?? "—"],
    ["Pages", [state.profiler ? "profiler" : "", state.calibrate ? "calibration" : "", state.ingest ? "ingest" : ""].filter(Boolean).join(", ") || "—"],
    ["Up since", state.startedAt && state.status === "ready" ? new Date(state.startedAt).toLocaleTimeString() : "—"],
  ];
  $("recorder-facts").replaceChildren(...facts.flatMap(([k, v]) => [el("dt", {}, k), el("dd", {}, v)]));
  const running = state.status === "ready" || state.status === "starting";
  $<HTMLButtonElement>("rec-start").disabled = running;
  $<HTMLButtonElement>("rec-stop").disabled = !running;
  if (activeTab === "profiler") placeEmbed();
}

function renderStream(state: StreamState): void {
  const pill = $("pill-stream");
  pill.textContent = state.status === "open" ? `stream ${state.received.toLocaleString()} msgs` : `stream ${state.status}`;
  pill.className = `pill ${state.status === "open" ? "good" : state.status === "off" ? "" : "warn"}`;
  pill.title = state.detail ?? "";
}

function renderIngest(state: IngestState): void {
  $("viewer-ingest").textContent =
    state.status === "streaming" ? `streaming to the recorder: ${state.framesSent} frames sent, ${state.framesDropped} dropped` : `ingest ${state.status}${state.detail ? ` — ${state.detail}` : ""}`;
}

const logLines: string[] = [];
function addLog(line: string): void {
  logLines.push(line);
  if (logLines.length > 500) logLines.splice(0, logLines.length - 500);
  const log = $("log");
  const atBottom = log.scrollTop + log.clientHeight >= log.scrollHeight - 4;
  log.textContent = logLines.join("\n");
  if (atBottom) log.scrollTop = log.scrollHeight;
}

$("rec-start").addEventListener("click", () => void api.startRecorder().then(renderRecorder));
$("rec-stop").addEventListener("click", () => void api.stopRecorder().then(renderRecorder));
$("rec-restart").addEventListener("click", () => void api.restartRecorder().then(renderRecorder));
$("open-viewer").addEventListener("click", () => void api.openViewer(null));
$<HTMLFormElement>("viewer-form").addEventListener("submit", (e) => {
  e.preventDefault();
  const input = $<HTMLInputElement>("viewer-link");
  void api.openViewer(input.value).then((ok) => {
    $("viewer-ingest").textContent = ok ? "viewer open" : "That is not a YouTube video or a Twitch channel or video link.";
    if (ok) input.value = "";
  });
});

// ---------------------------------------------------------------- library

function seconds(s: LibrarySessionSnapshot, qpc: number): number {
  return s.qpcFrequency > 0 ? (qpc - s.startQpc) / s.qpcFrequency : 0;
}

function clock(t: number): string {
  const sign = t < 0 ? "-" : "";
  const a = Math.abs(t);
  const m = Math.floor(a / 60);
  const sec = a - m * 60;
  return `${sign}${m}:${sec.toFixed(1).padStart(4, "0")}`;
}

function sessionLabel(s: LibrarySessionSnapshot): string {
  return s.title || (s.sourceKind === "official_player" ? "Official player" : s.id.slice(0, 18));
}

function renderLibrary(): void {
  const sessions = library.sessions;
  if (!selectedSession || !sessions.some((s) => s.id === selectedSession)) {
    selectedSession = sessions[0]?.id ?? null;
  }

  $("lib-sessions").replaceChildren(
    ...(sessions.length
      ? sessions.map((s) => {
          const b = el(
            "button",
            { class: s.id === selectedSession ? "active" : "" },
            el("span", { class: "title" }, sessionLabel(s)),
            el("span", { class: "meta" }, `${s.sourceKind.replace("_", " ")} · ${s.retention} · ${s.profile || "no profile"}`),
            el("span", { class: "meta" }, `${s.candidates.length} candidates · ${s.events} events · ${s.ended ? "ended" : "live"}`),
          );
          b.addEventListener("click", () => {
            selectedSession = s.id;
            selectedCandidate = null;
            renderLibrary();
          });
          return b;
        })
      : [el("p", { class: "muted" }, "No sessions yet. Start the recorder and feed it (a game, the testbench, or the viewer).")]),
  );

  const s = sessions.find((x) => x.id === selectedSession);
  $<HTMLButtonElement>("lib-forget").disabled = !s;
  const filter = $<HTMLSelectElement>("lib-filter").value;
  const sort = $<HTMLSelectElement>("lib-sort").value;
  const rows = (s?.candidates ?? [])
    .filter((c) =>
      filter === "unrated" ? c.rating === 0 : filter === "up" ? c.rating === 1 : filter === "down" ? c.rating === -1 : filter === "qa" ? c.qa?.passed === true : filter === "saved" ? c.savedPath !== null : true,
    )
    .sort((a, b) => (sort === "score" ? b.score - a.score : a.peakQpc - b.peakQpc));
  $("lib-count").textContent = s ? `${rows.length} of ${s.candidates.length}` : "";
  $("lib-rows").replaceChildren(...(s ? rows.map((c) => candidateRow(s, c)) : []));
  renderSummary();
  syncTimelineSessions();
  if (activeTab === "timeline") drawTimeline();
}

function candidateRow(s: LibrarySessionSnapshot, c: CandidateView): HTMLTableRowElement {
  const rate = (r: Rating): void => void api.rate(s.id, c.id, c.rating === r ? 0 : r);
  const up = el("button", { class: `up ${c.rating === 1 ? "on" : ""}`, title: "Keep: a good clip" }, "▲");
  const down = el("button", { class: `down ${c.rating === -1 ? "on" : ""}`, title: "Not a highlight" }, "▼");
  up.addEventListener("click", () => rate(1));
  down.addEventListener("click", () => rate(-1));
  let clip: Node = el("span", { class: "muted" }, "—");
  if (c.savedPath) {
    const b = el("button", { title: c.savedPath }, "Show file");
    b.addEventListener("click", () => void api.revealClip(c.savedPath ?? ""));
    clip = b;
  }

  const qa = c.qa
    ? el("span", { class: c.qa.passed ? "qa-pass" : "qa-fail", title: c.qa.checks.map((k) => `${k.passed ? "✓" : "✗"} ${k.name} ${k.value.toFixed(2)} ${k.detail}`).join("\n") }, c.qa.passed ? "pass" : "fail")
    : el("span", { class: "muted" }, "—");
  const row = el(
    "tr",
    { class: c.id === selectedCandidate ? "selected" : "", "data-id": c.id },
    el("td", { class: "num" }, clock(seconds(s, c.peakQpc))),
    el("td", { class: "num" }, `${(s.qpcFrequency ? (c.endQpc - c.startQpc) / s.qpcFrequency : 0).toFixed(1)} s`),
    el("td", { class: "num", title: c.modelScore !== null ? `model ${c.modelScore.toFixed(2)}` : "heuristic" }, c.score.toFixed(2)),
    el("td", {}, ...c.reasons.map((r) => el("span", { class: "tag" }, r))),
    el("td", {}, qa),
    el("td", {}, clip),
    el("td", {}, el("div", { class: "rate" }, up, down)),
  );
  return row;
}

function renderSummary(): void {
  const live = library.sessions.filter((s) => !s.ended);
  const total = library.sessions.reduce((n, s) => n + s.candidates.length, 0);
  const rated = library.sessions.reduce((n, s) => n + s.candidates.filter((c) => c.rating !== 0).length, 0);
  $("session-summary").textContent = library.sessions.length
    ? `${library.sessions.length} sessions (${live.length} live), ${total} clip candidates, ${rated} rated.`
    : "No sessions yet.";
}

$("lib-filter").addEventListener("change", renderLibrary);
$("lib-sort").addEventListener("change", renderLibrary);
$("lib-forget").addEventListener("click", () => {
  if (selectedSession) void api.forgetSession(selectedSession);
});

// --------------------------------------------------------------- timeline

const tlCanvas = $<HTMLCanvasElement>("tl-canvas");
const tlSelect = $<HTMLSelectElement>("tl-session");
let tlFollow = true;
let hits: { x0: number; x1: number; y0: number; y1: number; text: string; candidate?: string }[] = [];

function syncTimelineSessions(): void {
  const ids = library.sessions.map((s) => s.id).join("|");
  if (tlSelect.dataset["ids"] === ids) return;
  const keep = tlSelect.value;
  tlSelect.dataset["ids"] = ids;
  tlSelect.replaceChildren(...library.sessions.map((s) => el("option", { value: s.id }, `${sessionLabel(s)} (${s.ended ? "ended" : "live"})`)));
  if (keep && library.sessions.some((s) => s.id === keep) && !tlFollow) tlSelect.value = keep;
  else if (library.sessions[0]) tlSelect.value = library.sessions[0].id;
}

tlSelect.addEventListener("change", () => {
  tlFollow = tlSelect.value === library.sessions[0]?.id;
  drawTimeline();
});
$("tl-window").addEventListener("change", drawTimeline);

const LANES: { key: TimelineMark["kind"] | "candidate"; label: string; color: string }[] = [
  { key: "lifecycle", label: "game", color: "#9aa4b1" },
  { key: "profile", label: "profile", color: "#8fa8ff" },
  { key: "event", label: "events", color: "#f2b84b" },
  { key: "candidate", label: "candidates", color: "#4fb3a9" },
];

function drawTimeline(): void {
  const dpr = window.devicePixelRatio || 1;
  const w = tlCanvas.clientWidth;
  const h = tlCanvas.clientHeight;
  if (w === 0) return;
  tlCanvas.width = Math.round(w * dpr);
  tlCanvas.height = Math.round(h * dpr);
  const g = tlCanvas.getContext("2d");
  if (!g) return;
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  hits = [];
  const s = library.sessions.find((x) => x.id === tlSelect.value);
  if (!s || s.qpcFrequency <= 0) {
    g.fillStyle = "#8b95a3";
    g.font = "13px system-ui";
    g.fillText(s ? "Waiting for the session's clock…" : "No session yet.", 16, 28);
    $("tl-info").textContent = "";
    return;
  }

  const end = seconds(s, s.lastQpc);
  const span = Number($<HTMLSelectElement>("tl-window").value);
  const t0 = span > 0 ? Math.max(0, end - span) : 0;
  const t1 = Math.max(t0 + 10, span > 0 ? t0 + span : end);
  const left = 90;
  const right = w - 12;
  const x = (t: number): number => left + ((t - t0) / (t1 - t0)) * (right - left);
  const laneH = (h - 30) / LANES.length;
  $("tl-info").textContent = `${clock(end)} elapsed · ${s.marks.length} marks · ${s.candidates.length} candidates`;

  // Axis
  g.font = "11px system-ui";
  g.strokeStyle = "#2a313b";
  g.fillStyle = "#8b95a3";
  const step = niceStep((t1 - t0) / 8);
  for (let t = Math.ceil(t0 / step) * step; t <= t1; t += step) {
    g.beginPath();
    g.moveTo(x(t), 4);
    g.lineTo(x(t), h - 22);
    g.stroke();
    g.fillText(clock(t), x(t) - 14, h - 8);
  }

  LANES.forEach((lane, i) => {
    const y = 6 + i * laneH;
    g.fillStyle = "#8b95a3";
    g.fillText(lane.label, 8, y + laneH / 2 + 4);
    g.fillStyle = lane.color;
    if (lane.key === "candidate") {
      for (const c of s.candidates) {
        const a = seconds(s, c.startQpc);
        const b = seconds(s, c.endQpc);
        if (b < t0 || a > t1) continue;
        const xa = Math.max(left, x(a));
        const xb = Math.max(xa + 2, Math.min(right, x(b)));
        g.fillStyle = c.rating === 1 ? "#5fc27a" : c.rating === -1 ? "#e06b6b" : lane.color;
        g.globalAlpha = 0.35 + 0.6 * Math.min(1, Math.max(0, c.score));
        g.fillRect(xa, y + 6, xb - xa, laneH - 12);
        g.globalAlpha = 1;
        if (c.id === selectedCandidate) {
          g.strokeStyle = "#e6e9ee";
          g.strokeRect(xa, y + 6, xb - xa, laneH - 12);
        }

        hits.push({ x0: xa, x1: xb, y0: y, y1: y + laneH, text: `candidate ${clock(a)}–${clock(b)} · score ${c.score.toFixed(2)} · ${c.reasons.join(", ")}`, candidate: c.id });
      }

      return;
    }

    for (const m of s.marks) {
      if (m.kind !== lane.key) continue;
      const t = seconds(s, m.qpc);
      if (t < t0 || t > t1) continue;
      const xm = x(t);
      g.fillRect(xm - 1, y + 4, 3, laneH - 8);
      hits.push({ x0: xm - 4, x1: xm + 4, y0: y, y1: y + laneH, text: `${clock(t)} ${m.name}${m.detail ? ` (${m.detail})` : ""}${m.confidence ? ` · ${m.confidence.toFixed(2)}` : ""}` });
    }
  });
}

function niceStep(raw: number): number {
  for (const s of [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600]) {
    if (s >= raw) return s;
  }

  return 7200;
}

function hitAt(e: MouseEvent): (typeof hits)[number] | undefined {
  const r = tlCanvas.getBoundingClientRect();
  const px = e.clientX - r.left;
  const py = e.clientY - r.top;
  return hits.find((hh) => px >= hh.x0 && px <= hh.x1 && py >= hh.y0 && py <= hh.y1);
}

tlCanvas.addEventListener("mousemove", (e) => {
  const hit = hitAt(e);
  $("tl-detail").textContent = hit ? hit.text : "Hover a mark or candidate; click a candidate to open it in the library.";
});
tlCanvas.addEventListener("click", (e) => {
  const hit = hitAt(e);
  if (hit?.candidate) {
    selectedSession = tlSelect.value;
    selectedCandidate = hit.candidate;
    renderLibrary();
    showTab("library");
    document.querySelector(`#lib-rows tr[data-id="${CSS.escape(hit.candidate)}"]`)?.scrollIntoView({ block: "center" });
  }
});

// --------------------------------------------------------------- datasets

$("browse-build").addEventListener("click", () => {
  const b = $<HTMLButtonElement>("browse-build");
  b.disabled = true;
  $("browse-status").textContent = "building…";
  void api.buildBrowse().then((r) => {
    b.disabled = false;
    $("browse-status").textContent = r.ok ? "built" : r.message;
    placeEmbed();
  });
});

// --------------------------------------------------------------- settings

const form = $<HTMLFormElement>("settings-form");
function fillSettings(s: Settings): void {
  for (const [k, v] of Object.entries(s)) {
    const input = form.elements.namedItem(k) as HTMLInputElement | null;
    if (!input) continue;
    if (input.type === "checkbox") input.checked = Boolean(v);
    else input.value = String(v);
  }
}

form.addEventListener("submit", (e) => {
  e.preventDefault();
  const v = (k: string): HTMLInputElement => form.elements.namedItem(k) as HTMLInputElement;
  const next: Settings = {
    recorderPath: v("recorderPath").value,
    testbenchPath: v("testbenchPath").value,
    datasetsDir: v("datasetsDir").value,
    profilesDir: v("profilesDir").value,
    pipeName: v("pipeName").value,
    webSocketPort: Number(v("webSocketPort").value) || 0,
    profilerPage: v("profilerPage").checked,
    autoStart: v("autoStart").checked,
  };
  void api.saveSettings(next).then((saved) => {
    fillSettings(saved);
    $("settings-status").textContent = "saved";
  });
});

// ------------------------------------------------------------------- boot

api.onState(renderRecorder);
api.onLog(addLog);
api.onStream(renderStream);
api.onIngest(renderIngest);
api.onLibrary((snap) => {
  library = snap;
  renderLibrary();
});
void api.getState().then(renderRecorder);
void api.getStream().then(renderStream);
void api.getLog().then((lines) => lines.forEach(addLog));
void api.getLibrary().then((snap) => {
  library = snap;
  renderLibrary();
});
void api.getSettings().then(fillSettings);
void api.getBrowseState().then((b) => {
  $("browse-status").textContent = b.available ? `built ${b.builtAt ? new Date(b.builtAt).toLocaleString() : ""}` : "not built yet";
});
// The live window keeps moving even when no new events arrive.
window.setInterval(() => {
  if (activeTab === "timeline") drawTimeline();
}, 1000);
