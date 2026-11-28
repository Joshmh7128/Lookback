// The main window: overview, clip library with ratings, live timeline, the
// recorder's profiler page, the dataset browser, the profile editor and settings. Everything
// comes from the main process through `window.lookback`; this page opens no
// sockets and stores nothing.
import type { CandidateView, EmbeddedView, FootageJob, FootageRequest, IngestState, LibrarySessionSnapshot, LibrarySnapshot, LookbackApi, ProfileEdits, ProfileSummary, ProfileView, Rating, RecorderState, Settings, StorageState, StreamState, SupervisionState, TimelineMark } from "../shared/api";

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
  if (name === "profiles" && !peLoaded) peLoadList(null);
  if (name === "storage") void stLoad();
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

const STAR_MEANING = ["unrated", "not a highlight", "weak", "decent", "good", "a must-keep"];

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
  if (s.sourceKind === "file" && /[\\/]/.test(s.title)) return s.title.split(/[\\/]/).pop() ?? s.title;
  return s.title || (s.sourceKind === "official_player" ? "Official player" : s.id.slice(0, 18));
}

function currentSession(): LibrarySessionSnapshot | undefined {
  return library.sessions.find((x) => x.id === selectedSession);
}

/** The candidates the table shows, in its order. */
function visibleCandidates(s: LibrarySessionSnapshot | undefined): CandidateView[] {
  const filter = $<HTMLSelectElement>("lib-filter").value;
  const sort = $<HTMLSelectElement>("lib-sort").value;
  const keep = (c: CandidateView): boolean => {
    switch (filter) {
      case "unrated":
        return c.rating === 0;
      case "rated":
        return c.rating > 0;
      case "high":
        return c.rating >= 4;
      case "mid":
        return c.rating === 3;
      case "low":
        return c.rating === 1 || c.rating === 2;
      case "qa":
        return c.qa?.passed === true;
      case "saved":
        return c.savedPath !== null;
      default:
        return true;
    }
  };
  return (s?.candidates ?? []).filter(keep).sort((a, b) => (sort === "score" ? b.score - a.score : sort === "stars" ? b.rating - a.rating || a.peakQpc - b.peakQpc : a.peakQpc - b.peakQpc));
}

/** A 1-5 star control; clicking the current star (or ×) clears it. */
function stars(c: CandidateView, s: LibrarySessionSnapshot, big = false): HTMLDivElement {
  if (s.sourceKind === "official_player") {
    // A watched video's moments are rated in the viewer's panel, where the rating is saved as numbers only.
    return el("div", { class: `stars-note${big ? " big" : ""}`, title: "Ratings of videos you watch are saved from the viewer's calibration panel (section 4), as numbers only" }, "rate in the viewer's panel");
  }

  const box = el("div", { class: `stars${big ? " big" : ""}`, role: "radiogroup", "aria-label": "Stars" });
  for (let n = 1 as Rating; n <= 5; n = (n + 1) as Rating) {
    const value = n;
    const b = el("button", { class: c.rating >= value ? "on" : "", title: `${value} star${value > 1 ? "s" : ""}: ${STAR_MEANING[value]}`, role: "radio", "aria-checked": String(c.rating === value) }, "★");
    b.addEventListener("click", (e) => {
      e.stopPropagation();
      rate(s.id, c.id, c.rating === value ? 0 : value);
    });
    box.append(b);
  }

  if (c.rating > 0) {
    const clear = el("button", { class: "clear", title: "Clear the rating" }, "×");
    clear.addEventListener("click", (e) => {
      e.stopPropagation();
      rate(s.id, c.id, 0);
    });
    const label = c.ratingStored ? "stored" : c.ratingNote ? "not stored" : "in memory";
    const tip = c.ratingStored ? "The recorder stored this rating" : (c.ratingNote ?? "Kept in the shell's memory only");
    box.append(clear, el("span", { class: `stored${c.ratingNote ? " refused" : ""}`, title: tip }, label));
  }

  return box;
}

function rate(sessionId: string, candidateId: string, rating: Rating): void {
  void api.rate(sessionId, candidateId, rating);
}

function renderLibrary(): void {
  const sessions = library.sessions;
  if (!selectedSession || !sessions.some((s) => s.id === selectedSession)) {
    selectedSession = sessions[0]?.id ?? null;
  }

  $("lib-sessions").replaceChildren(
    ...(sessions.length
      ? sessions.map((s) => {
          const rated = s.candidates.filter((c) => c.rating > 0).length;
          const b = el(
            "button",
            { class: s.id === selectedSession ? "active" : "", title: s.title },
            el("span", { class: "title" }, sessionLabel(s)),
            el("span", { class: "meta" }, `${s.sourceKind.replace("_", " ")} · ${s.retention} · ${s.profile || "no profile"}`),
            el("span", { class: "meta" }, `${s.candidates.length} candidates · ${rated} rated · ${s.ended ? "ended" : "live"}${s.previewable ? " · preview" : ""}`),
          );
          b.addEventListener("click", () => {
            selectedSession = s.id;
            selectedCandidate = null;
            renderLibrary();
          });
          return b;
        })
      : [el("p", { class: "muted" }, "No sessions yet. Open footage above, or feed the recorder (a game, the testbench, or the viewer).")]),
  );

  const s = currentSession();
  $<HTMLButtonElement>("lib-forget").disabled = !s;
  const rows = visibleCandidates(s);
  if (selectedCandidate && !rows.some((c) => c.id === selectedCandidate)) selectedCandidate = null;
  if (!selectedCandidate && rows[0]) selectedCandidate = rows[0].id;
  $("lib-count").textContent = s ? `${rows.length} of ${s.candidates.length}` : "";
  $("lib-rows").replaceChildren(...(s ? rows.map((c) => candidateRow(s, c)) : []));
  renderSelected();
  renderSummary();
  syncTimelineSessions();
  if (activeTab === "timeline") drawTimeline();
}

function candidateRow(s: LibrarySessionSnapshot, c: CandidateView): HTMLTableRowElement {
  const clip = clipCell(s, c);

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
    el("td", {}, stars(c, s)),
  );
  row.addEventListener("click", () => selectCandidate(c.id));
  return row;
}

/** Saved clip: where it is. Live session: a Save button (or why saving failed). Otherwise nothing. */
function clipCell(s: LibrarySessionSnapshot, c: CandidateView): HTMLElement {
  if (c.savedPath) {
    const b = el("button", { title: c.savedPath }, "Show file");
    b.addEventListener("click", (e) => {
      e.stopPropagation();
      void api.revealClip(c.savedPath ?? "");
    });
    return el("div", {}, b, el("span", { class: "muted save-note", title: c.savedPath }, c.savedPath.split(/[\\/]/).pop() ?? ""));
  }

  if (s.sourceKind !== "live_game") {
    return el("span", { class: "muted", title: s.sourceKind === "file" ? "Footage files are already on disk" : "Nothing is saved from this source" }, "—");
  }

  const full = storage?.clipsFull === true;
  const b = el("button", { title: full ? "Saved clips are at their storage limit" : "Cut this moment from the recording and save it (S)" }, c.saving ? "Saving…" : "Save");
  b.disabled = c.saving || full;
  b.addEventListener("click", (e) => {
    e.stopPropagation();
    save(s.id, c.id);
  });
  return el("div", {}, b, ...(c.saveNote ? [el("span", { class: "save-note", title: c.saveNote }, c.saveNote)] : []));
}

function save(sessionId: string, candidateId: string): void {
  void api.saveClip(sessionId, candidateId);
}

function selectCandidate(id: string | null): void {
  if (id === selectedCandidate) return;
  selectedCandidate = id;
  for (const tr of document.querySelectorAll<HTMLTableRowElement>("#lib-rows tr")) {
    tr.classList.toggle("selected", tr.dataset["id"] === id);
  }

  document.querySelector(`#lib-rows tr.selected`)?.scrollIntoView({ block: "nearest" });
  renderSelected();
}

// The preview: the candidate's window of the analysed file (or the saved
// clip), played in a loop. Streamed from the file by the main process.
const video = $<HTMLVideoElement>("preview-video");
let previewFor: string | null = null;
let window_: { start: number; end: number } | null = null;

video.addEventListener("loadedmetadata", () => {
  if (window_) video.currentTime = window_.start;
});
video.addEventListener("timeupdate", () => {
  if (window_ && video.currentTime >= window_.end) video.currentTime = window_.start;
});

function renderSelected(): void {
  const s = currentSession();
  const c = s?.candidates.find((x) => x.id === selectedCandidate);
  const starBox = $("preview-stars");
  if (!s || !c) {
    starBox.replaceChildren();
    $("preview-info").textContent = "Select a candidate to preview it.";
    stopPreview();
    return;
  }

  starBox.replaceChildren(stars(c, s, true), ...(c.ratingNote ? [el("p", { class: "rating-note" }, `Not stored: ${c.ratingNote}`)] : []));
  const key = `${s.id}\u0000${c.id}`;
  if (previewFor === key) return;
  previewFor = key;
  $("preview-info").textContent = `${clock(seconds(s, c.startQpc))}–${clock(seconds(s, c.endQpc))} · peak ${clock(seconds(s, c.peakQpc))} · ${c.reasons.join(", ") || "no reasons"}`;
  void api.preview(s.id, c.id).then((p) => {
    if (previewFor !== key) return;
    if (!p) {
      stopPreview();
      $("preview-info").textContent +=
        s.sourceKind === "live_game"
          ? " · no preview: the recorder could not cut this moment from its footage (kept 24 h after a session, within its budget), or it is not running"
          : s.sourceKind === "official_player"
            ? " · not recorded: YouTube and Twitch moments are never kept. Replay it in the viewer with ▶ in its panel (section 4), while the video is open"
            : " · no preview: open this footage in Lookback to watch its candidates";
      return;
    }

    window_ = { start: p.start, end: p.end };
    if (video.src !== p.url) {
      video.src = p.url;
    } else {
      video.currentTime = p.start;
    }

    void video.play().catch(() => undefined);
  });
}

function stopPreview(): void {
  previewFor = null;
  window_ = null;
  video.pause();
  video.removeAttribute("src");
  video.load();
}

document.addEventListener("keydown", (e) => {
  if (activeTab !== "library" || e.ctrlKey || e.altKey || e.metaKey) return;
  const t = e.target as HTMLElement | null;
  if (t && (t.tagName === "INPUT" || t.tagName === "SELECT" || t.tagName === "TEXTAREA")) return;
  const s = currentSession();
  const rows = visibleCandidates(s);
  const at = rows.findIndex((c) => c.id === selectedCandidate);
  if (/^[0-5]$/.test(e.key) && s && at >= 0) {
    e.preventDefault();
    if (s.sourceKind !== "official_player") rate(s.id, rows[at]!.id, Number(e.key) as Rating);
  } else if ((e.key === "ArrowDown" || e.key === "j") && rows.length) {
    e.preventDefault();
    selectCandidate(rows[Math.min(rows.length - 1, at + 1)]!.id);
  } else if ((e.key === "ArrowUp" || e.key === "k") && rows.length) {
    e.preventDefault();
    selectCandidate(rows[Math.max(0, at - 1)]!.id);
  } else if ((e.key === "s" || e.key === "S") && s && at >= 0) {
    e.preventDefault();
    const c = rows[at]!;
    if (s.sourceKind === "live_game" && !c.savedPath && !c.saving) save(s.id, c.id);
  } else if (e.key === " " && t?.tagName !== "BUTTON" && t?.tagName !== "VIDEO") {
    e.preventDefault();
    if (video.paused) void video.play().catch(() => undefined);
    else video.pause();
  }
});

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

// Open footage: the user's own gameplay (or permissioned footage) analysed
// so its candidates can be watched and rated.
const retention = $<HTMLSelectElement>("footage-retention");
const permissionRef = $<HTMLInputElement>("footage-ref");
retention.addEventListener("change", () => {
  permissionRef.hidden = retention.value === "owned";
});
$<HTMLFormElement>("footage-form").addEventListener("submit", (e) => {
  e.preventDefault();
  if (retention.value !== "owned" && !permissionRef.value.trim()) {
    $("footage-status").textContent = "Someone else's footage needs a note of where the permission or licence is recorded.";
    permissionRef.focus();
    return;
  }

  $("footage-status").textContent = "";
  void api
    .openFootage({ profile: $<HTMLSelectElement>("footage-profile").value, retention: retention.value as FootageRequest["retention"], permissionRef: permissionRef.value })
    .then((job) => {
      if (job) $("footage-status").textContent = `${job.name}: ${job.status}`;
    });
});

function renderFootage(jobs: FootageJob[]): void {
  $("footage-jobs").replaceChildren(
    ...jobs.slice(0, 6).map((j) =>
      el("li", { class: j.status }, `${j.name} · ${j.profile} · ${j.status}${j.via === "testbench" ? " (via testbench)" : j.via === "recorder" ? " (recorder)" : ""}${j.detail ? ` — ${j.detail}` : ""}`),
    ),
  );
  // A freshly linked session is what the user wants to rate next.
  const latest = jobs.find((j) => j.sessionId && j.status !== "failed");
  if (latest?.sessionId && latest.sessionId !== lastFootageSession) {
    lastFootageSession = latest.sessionId;
    selectedSession = latest.sessionId;
    selectedCandidate = null;
    renderLibrary();
  }
}

let lastFootageSession: string | null = null;

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
        g.fillStyle = c.rating >= 4 ? "#5fc27a" : c.rating === 3 ? "#f2c94c" : c.rating > 0 ? "#e06b6b" : lane.color;
        g.globalAlpha = 0.35 + 0.6 * Math.min(1, Math.max(0, c.score));
        g.fillRect(xa, y + 6, xb - xa, laneH - 12);
        g.globalAlpha = 1;
        if (c.id === selectedCandidate) {
          g.strokeStyle = "#e6e9ee";
          g.strokeRect(xa, y + 6, xb - xa, laneH - 12);
        }

        hits.push({ x0: xa, x1: xb, y0: y, y1: y + laneH, text: `candidate ${clock(a)}–${clock(b)} · score ${c.score.toFixed(2)}${c.rating ? ` · ${"★".repeat(c.rating)}` : ""} · ${c.reasons.join(", ")}`, candidate: c.id });
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
    previewFor = null;
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

// --------------------------------------------------------------- profiles

// The profile editor: resolved values with their source, edited in place and
// saved through the testbench into the checkout's profiles/ (main process).
// Pending changes live only in the inputs until Save.
let peProfiles: ProfileSummary[] = [];
let peView: ProfileView | null = null;
let peLoaded = false;
/** Clipper overrides and components marked for reset (`clip:<field>`, `comp:<signal>`). */
const peResets = new Set<string>();
const MS_FIELDS = new Set(["preMs", "postMs", "refractoryMs"]);
const CLIP_LABELS: Record<string, string> = {
  high: "Peak starts at (high)",
  low: "Peak ends below (low)",
  preMs: "Kept before a peak (ms)",
  postMs: "Kept after a peak (ms)",
  refractoryMs: "Peaks merged within (ms)",
  peakScore: "Score of a peak at high",
};

const fmt = (v: number): string => String(Number(v.toFixed(4)));

function peStatus(text: string | null, kind: "good" | "bad" | "" = ""): void {
  const s = $("pe-status");
  s.hidden = !text;
  s.textContent = text ?? "";
  s.className = `pe-status ${kind}`;
}

function source(src: string): HTMLElement {
  const own = peView !== null && src === peView.id;
  return el("span", { class: own ? "src own" : "src", title: own ? "set in this profile's file" : src === "defaults" ? "the clipper's defaults (clipper.defaults.json)" : `inherited from ${src}` }, own ? "this profile" : src);
}

function numInput(kind: string, original: number | null, attrs: Record<string, string> = {}): HTMLInputElement {
  const input = el("input", { class: "num", type: "number", step: "any", min: "0", "data-kind": kind, "data-original": original === null ? "" : fmt(original), ...attrs });
  input.value = original === null ? "" : fmt(original);
  input.addEventListener("input", peRefresh);
  return input;
}

function peLoadList(select: string | null): void {
  void api.viewProfile(select).then((r) => {
    if (!r.ok) {
      $("pe-list").replaceChildren(el("p", { class: "muted" }, "Profiles are unavailable."));
      peStatus(r.message ?? "The profiles could not be read.", "bad");
      return;
    }

    peLoaded = true;
    peProfiles = r.profiles ?? [];
    renderProfileList();
    if (r.view) showProfile(r.view);
  });
}

function renderProfileList(): void {
  $("pe-list").replaceChildren(...peProfiles.map((p) => {
    const b = el("button", { title: p.id, class: peView?.id === p.id ? "active" : undefined, "data-id": p.id }, `${" ".repeat(p.depth)}${p.displayName}`, el("span", { class: "lvl" }, p.level));
    b.addEventListener("click", () => {
      if (peView?.id === p.id) return;
      if (peDirty() && !window.confirm("Discard the unsaved changes to this profile?")) return;
      peStatus(null);
      void api.viewProfile(p.id).then((r) => (r.ok && r.view ? showProfile(r.view) : peStatus(r.message ?? "The profile could not be read.", "bad")));
    });
    return b;
  }));
}

function showProfile(v: ProfileView): void {
  peView = v;
  peResets.clear();
  for (const b of document.querySelectorAll<HTMLButtonElement>("#pe-list button")) b.classList.toggle("active", b.dataset["id"] === v.id);
  $("pe-title").replaceChildren(el("strong", {}, v.displayName), ` · ${v.id} · ${v.level} · `, el("code", {}, `profiles/${v.file}`), v.path.length > 1 ? ` · inherits from ${v.path.slice(0, -1).join(" → ")}` : "");

  const events = el("table", {},
    el("thead", {}, el("tr", {}, el("th", {}, "Event"), el("th", {}, "From"), el("th", {}, "Weight"), el("th", {}, "Thresholds"))),
    el("tbody", {}, ...v.events.map((e) => el("tr", {},
      el("td", {}, el("code", {}, e.name)),
      el("td", {}, source(e.source)),
      el("td", {}, numInput("event-weight", e.weight, { "data-name": e.name })),
      el("td", {}, ...e.conditions.map((c) => el("span", { class: "cond", title: c.list === "whenAny" ? "any one of these" : "all of these" },
        el("code", {}, c.signal), c.kind, numInput("event-threshold", c.value, { "data-name": e.name, "data-index": String(c.index), ...(c.kind === "rise" ? {} : { min: "" }) }))))))));

  const composites = v.composites.length === 0 ? el("p", { class: "muted" }, "No composite events.") : el("table", {},
    el("thead", {}, el("tr", {}, el("th", {}, "Composite"), el("th", {}, "Pattern"), el("th", {}, "From"), el("th", {}, "Weight"))),
    el("tbody", {}, ...v.composites.map((c) => el("tr", {},
      el("td", {}, el("code", {}, c.name)),
      el("td", { class: "muted" }, `${c.atLeast}× ${c.of} within ${c.withinMs / 1000} s`),
      el("td", {}, source(c.source)),
      el("td", {}, numInput("composite-weight", c.weight, { "data-name": c.name }))))));

  const numbers = el("table", {},
    el("thead", {}, el("tr", {}, el("th", {}, "Setting"), el("th", {}, "Value"), el("th", {}, "From"), el("th", {}))),
    el("tbody", {}, ...v.clipper.numbers.map((n) => clipperRow(n.field, n.value, n.source))));

  const components = el("tbody", { id: "pe-components" }, ...v.clipper.components.map((c) => componentRow(c.signal, c.weight, c.minSpread, c.source)));
  const used = new Set(v.clipper.components.map((c) => c.signal));
  const addSelect = el("select", { id: "pe-add-signal" }, el("option", { value: "" }, "add a signal…"), ...v.signals.filter((s) => !used.has(s)).map((s) => el("option", { value: s }, s)));
  const addButton = el("button", { type: "button" }, "Add");
  addButton.addEventListener("click", () => {
    const signal = addSelect.value;
    if (!signal) return;
    addSelect.querySelector(`option[value="${CSS.escape(signal)}"]`)?.remove();
    addSelect.value = "";
    $("pe-components").append(componentRow(signal, null, null, null));
    peRefresh();
  });

  $("pe-body").replaceChildren(
    el("div", { class: "card" }, el("h2", {}, "Events"), events),
    el("div", { class: "card" }, el("h2", {}, "Composite events"), composites),
    el("div", { class: "card" }, el("h2", {}, "Clipper: excitement curve"), numbers),
    el("div", { class: "card" }, el("h2", {}, "Clipper: curve components"),
      el("table", {}, el("thead", {}, el("tr", {}, el("th", {}, "Signal"), el("th", {}, "Weight"), el("th", {}, "minSpread"), el("th", {}, "From"), el("th", {}))), components),
      el("div", { class: "row add" }, addSelect, addButton, el("span", { class: "muted" }, "Only signals Lookback already produces for this game; weight 0 switches one off."))),
  );
  peRefresh();
}

function resetButton(key: string, own: boolean, row: HTMLElement): HTMLElement {
  if (!own) return el("span");
  const b = el("button", { type: "button", title: "Remove this profile's override: inherit the value again" }, "Reset");
  b.addEventListener("click", () => {
    if (peResets.has(key)) peResets.delete(key);
    else peResets.add(key);
    row.classList.toggle("reset", peResets.has(key));
    b.textContent = peResets.has(key) ? "Undo" : "Reset";
    peRefresh();
  });
  return b;
}

function clipperRow(field: string, value: number, src: string): HTMLElement {
  const row = el("tr", {});
  const input = numInput("clip", value, { "data-field": field, ...(MS_FIELDS.has(field) ? { step: "100" } : {}) });
  row.append(el("td", {}, CLIP_LABELS[field] ?? field, " ", el("code", { class: "muted" }, field)), el("td", {}, input), el("td", {}, source(src)), el("td", {}, resetButton(`clip:${field}`, src === peView?.id, row)));
  return row;
}

function componentRow(signal: string, weight: number | null, minSpread: number | null, src: string | null): HTMLElement {
  const row = el("tr", { class: weight === 0 ? "off" : undefined });
  const input = numInput("component", weight, { "data-signal": signal });
  input.addEventListener("input", () => row.classList.toggle("off", Number(input.value) === 0));
  row.append(
    el("td", {}, el("code", {}, signal)),
    el("td", {}, input),
    el("td", { class: "muted" }, minSpread === null ? "default" : fmt(minSpread)),
    el("td", {}, src === null ? el("span", { class: "src own" }, "new") : source(src)),
    el("td", {}, src === null ? el("span") : resetButton(`comp:${signal}`, src === peView?.id, row)));
  return row;
}

/** The pending edits from the inputs, or null when an input is not a valid number. */
function peEdits(): ProfileEdits | null {
  const edits: ProfileEdits = { events: [], composites: [], clipper: {}, components: {} };
  const events = new Map<string, ProfileEdits["events"][number]>();
  const ev = (name: string): ProfileEdits["events"][number] => {
    let e = events.get(name);
    if (!e) events.set(name, (e = { name }));
    return e;
  };

  let valid = true;
  for (const input of document.querySelectorAll<HTMLInputElement>("#pe-body input.num")) {
    const kind = input.dataset["kind"] ?? "";
    const original = input.dataset["original"] ?? "";
    const value = input.value.trim() === "" ? NaN : Number(input.value);
    const ms = kind === "clip" && MS_FIELDS.has(input.dataset["field"] ?? "");
    const negativeOk = kind === "event-threshold" && input.min === "";
    const bad = !Number.isFinite(value) || (value < 0 && !negativeOk) || (ms && !Number.isInteger(value));
    const changed = !bad && (original === "" || Math.abs(value - Number(original)) > 1e-9);
    input.classList.toggle("invalid", bad);
    input.classList.toggle("changed", changed);
    if (bad) {
      valid = false;
      continue;
    }

    if (!changed) continue;
    const name = input.dataset["name"] ?? "";
    if (kind === "event-weight") ev(name).weight = value;
    else if (kind === "event-threshold") (ev(name).thresholds ??= {})[input.dataset["index"] ?? ""] = value;
    else if (kind === "composite-weight") edits.composites.push({ name, weight: value });
    else if (kind === "clip") edits.clipper[input.dataset["field"] ?? ""] = value;
    else if (kind === "component") edits.components[input.dataset["signal"] ?? ""] = value;
  }

  // A reset wins over an edit of the same value: the override goes.
  for (const key of peResets) {
    const [what, name] = [key.slice(0, key.indexOf(":")), key.slice(key.indexOf(":") + 1)];
    if (what === "clip") edits.clipper[name] = null;
    else edits.components[name] = null;
  }

  edits.events = [...events.values()];
  return valid ? edits : null;
}

function peDirty(): boolean {
  const e = peEdits();
  return e === null || e.events.length > 0 || e.composites.length > 0 || Object.keys(e.clipper).length > 0 || Object.keys(e.components).length > 0;
}

function peRefresh(): void {
  const dirty = peView !== null && peDirty();
  $<HTMLButtonElement>("pe-save").disabled = !dirty || peEdits() === null;
  $<HTMLButtonElement>("pe-discard").disabled = !dirty;
}

$("pe-discard").addEventListener("click", () => {
  if (peView) showProfile(peView);
  peStatus(null);
});

$("pe-save").addEventListener("click", () => {
  const edits = peEdits();
  if (!peView || !edits) return;
  const save = $<HTMLButtonElement>("pe-save");
  save.disabled = true;
  peStatus("Saving…");
  void api.saveProfile(peView.id, edits).then((r) => {
    if (r.ok && r.view) {
      showProfile(r.view);
      const files = r.files ?? [];
      peStatus(files.length === 0 ? "Nothing changed." : `Saved ${files.map((f) => `profiles/${f}`).join(", ")}. Live recording uses this from the next game it records; a recording in progress keeps its settings.`, "good");
    } else {
      peStatus(`Not saved: ${r.message ?? "the edit was refused."}`, "bad");
      peRefresh();
    }
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

// ---------------------------------------------------------------- storage

// The recorder's clip and footage budgets (recorder.storage). A banner when
// saved clips near their budget; a blocking notice when they reach it.
let storage: StorageState | null = null;
let fullShown = false;

function gb(bytes: number): string {
  const g = bytes / 1024 ** 3;
  return g >= 10 ? g.toFixed(0) : g.toFixed(1);
}

function renderStorage(): void {
  const s = storage;
  const banner = $("storage-banner");
  if (!s) {
    banner.hidden = true;
    $("storage-settings").textContent = "Storage: the recorder has not reported yet.";
    return;
  }

  const clips = `${gb(s.clipsBytes)} of ${gb(s.clipsBudgetBytes)} GB`;
  if (s.clipsFull || s.clipsNearLimit) {
    banner.hidden = false;
    banner.className = `banner${s.clipsFull ? " bad" : ""}`;
    $("storage-text").textContent = s.clipsFull ? `Saved clips are full (${clips}): new clips cannot be saved until you delete some.` : `Saved clips are at ${clips}. Delete clips you do not need before the limit is reached.`;
  } else {
    banner.hidden = true;
  }

  const dialog = $<HTMLDialogElement>("clips-full");
  if (s.clipsFull && !fullShown) {
    fullShown = true;
    $("clips-full-text").textContent = `Saved clips use ${clips}, the whole clips budget.`;
    if (!dialog.open) dialog.showModal();
  } else if (!s.clipsFull) {
    fullShown = false;
    if (dialog.open) dialog.close();
  }

  $("storage-settings").textContent = `Storage: saved clips ${clips}; recordings ${gb(s.footageBytes)} of ${gb(s.footageBudgetBytes)} GB; ${gb(s.volumeFreeBytes)} GB free on the drive. Budgets are set with the recorder's ClipsBudgetGB and FootageBudgetGB options.`;
}

for (const id of ["storage-open", "clips-full-open"]) {
  $(id).addEventListener("click", () => void api.openClipsFolder());
}

api.onStorage((s) => {
  const wasFull = storage?.clipsFull;
  storage = s;
  renderStorage();
  if (wasFull !== s.clipsFull) renderLibrary();
});
void api.getStorage().then((s) => {
  storage = s;
  renderStorage();
});

// ----------------------------------------------------------- supervision

// Shown when the recorder stopped unexpectedly: what it said, and when the
// shell restarts it (or that it stopped trying).
let supervision: SupervisionState | null = null;
let showCrashLog = false;
function renderSupervision(): void {
  const s = supervision;
  const banner = $("banner");
  const crash = s?.lastCrash;
  const recent = crash !== null && crash !== undefined && Date.now() - crash.at < 120_000;
  if (!s || (!s.gaveUp && s.nextRestartAt === null && !recent)) {
    banner.hidden = true;
    return;
  }

  banner.hidden = false;
  banner.className = `banner${s.gaveUp ? " bad" : ""}`;
  const why = crash ? (crash.fatal ? `stopped on purpose: ${crash.fatal}` : crash.detail) : "stopped";
  let text: string;
  if (s.gaveUp) {
    text = `The recorder ${why}. It stopped ${s.restarts >= s.maxRestarts ? `${s.restarts} times in a minute` : "and could not be started again"}, so Lookback is no longer restarting it.`;
  } else if (s.nextRestartAt !== null) {
    const secs = Math.max(0, Math.ceil((s.nextRestartAt - Date.now()) / 1000));
    text = `The recorder ${why}. Restarting in ${secs} s (restart ${s.restarts + 1} of ${s.maxRestarts} this minute).`;
  } else {
    text = `The recorder ${why}. Lookback restarted it.`;
  }

  $("banner-text").textContent = text;
  $<HTMLButtonElement>("banner-start").hidden = !s.gaveUp;
  const log = $("banner-log");
  log.hidden = !showCrashLog || !crash;
  log.textContent = crash ? crash.stderr.join("\n") || "(nothing on stderr)" : "";
}

$("banner-details").addEventListener("click", () => {
  showCrashLog = !showCrashLog;
  renderSupervision();
});
$("banner-start").addEventListener("click", () => void api.startRecorder().then(renderRecorder));
api.onSupervision((s) => {
  supervision = s;
  renderSupervision();
});
void api.getSupervision().then((s) => {
  supervision = s;
  renderSupervision();
});
window.setInterval(renderSupervision, 500);
void api.getPaths().then((p) => {
  $("ratings-path").textContent = p.ratingsInCheckout
    ? `Ratings are stored by the recorder in ${p.ratings} (the checkout's datasets folder, where ml/ trains).`
    : `Ratings are stored by the recorder in ${p.ratings} (its default). Set a datasets folder above to store them in a checkout.`;
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
api.onFootage(renderFootage);
void api.getFootage().then(renderFootage);
void api.profiles().then((ids) => {
  const select = $<HTMLSelectElement>("footage-profile");
  for (const id of ids.filter((x) => x !== "generic")) select.append(el("option", { value: id }, id));
});
void api.getBrowseState().then((b) => {
  $("browse-status").textContent = b.available ? `built ${b.builtAt ? new Date(b.builtAt).toLocaleString() : ""}` : "not built yet";
});
// The live window keeps moving even when no new events arrive.
window.setInterval(() => {
  if (activeTab === "timeline") drawTimeline();
}, 1000);

// ---------------------------------------------------------------- storage

function stWhen(iso: string | null | undefined): string {
  if (!iso) return "–";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "–" : d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function stSize(bytes: number): string {
  return bytes >= 1024 ** 3 ? `${gb(bytes)} GB` : `${Math.max(0.1, bytes / 1024 ** 2).toFixed(bytes >= 100 * 1024 ** 2 ? 0 : 1)} MB`;
}

function stButton(label: string, onClick: () => void, danger = false): HTMLButtonElement {
  const b = el("button", danger ? { class: "danger" } : {}, label);
  b.addEventListener("click", onClick);
  return b;
}

function stSay(text: string | null): void {
  const p = $("st-status");
  p.hidden = !text;
  p.textContent = text ?? "";
}

async function stDelete(kind: "session" | "clip" | "cache", target: { sessionId?: string; path?: string }, what: string): Promise<void> {
  if (!window.confirm(`Delete ${what}? This cannot be undone.`)) return;
  const refused = await api.deleteStored(kind, target);
  stSay(refused);
  await stLoad();
}

async function stLoad(): Promise<void> {
  const listing = await api.listStorage();
  if (!listing) {
    $("st-summary").textContent = "The recorder's store is not available (is the recorder running, with live capture on?).";
    return;
  }

  $("st-summary").textContent =
    `Footage ${gb(listing.footageBytes)} of ${gb(listing.footageBudgetBytes)} GB · ` +
    `saved clips ${gb(listing.clipsBytes)} of ${gb(listing.clipsBudgetBytes)} GB · ` +
    `cache ${gb(listing.cacheBytes)} of ${gb(listing.cacheBudgetBytes)} GB · ` +
    `${gb(listing.volumeFreeBytes)} GB free · ${listing.root}`;

  const titles = new Map(listing.sessions.map((s) => [s.id, s.title || s.id]));
  const sessions = $("st-sessions");
  sessions.replaceChildren();
  for (const s of listing.sessions) {
    const keep = el("label", { title: "Keep this session's footage: the sweep leaves it alone" });
    const box = el("input", { type: "checkbox" });
    box.checked = s.keep;
    box.disabled = s.live;
    box.addEventListener("change", () => void api.keepSession(s.id, box.checked).then(() => stLoad()));
    keep.append(box, " keep");
    const open = stButton("Open in Library", () => {
      selectedSession = s.id;
      showTab("library");
      renderLibrary();
    });
    const actions = el("td", { class: "actions" }, keep, open);
    if (!s.live) actions.append(stButton("Delete", () => void stDelete("session", { sessionId: s.id }, `the session "${s.title || s.id}" (its footage and cached clips; saved clips stay)`), true));
    sessions.append(el("tr", {},
      el("td", { class: "name" }, s.title || s.id, s.live ? " · recording" : ""),
      el("td", {}, s.profile || "–"),
      el("td", {}, stWhen(s.startedUtc)),
      el("td", {}, String(s.candidates)),
      el("td", {}, stSize(s.bytes)),
      el("td", {}, s.live ? "recording" : s.keep ? "kept" : stWhen(s.sweepAfterUtc)),
      actions));
  }

  if (!listing.sessions.length) sessions.append(el("tr", {}, el("td", { colspan: "7", class: "muted" }, "No recorded sessions.")));

  const clips = $("st-clips");
  clips.replaceChildren();
  for (const f of listing.clips) {
    clips.append(el("tr", {},
      el("td", { class: "name" }, f.name),
      el("td", {}, f.sessionId ? (titles.get(f.sessionId) ?? f.sessionId) : "–"),
      el("td", {}, stWhen(f.createdUtc)),
      el("td", {}, stSize(f.bytes)),
      el("td", { class: "actions" },
        stButton("Show file", () => void api.revealStored(f.path)),
        stButton("Delete", () => void stDelete("clip", { path: f.path }, `the saved clip ${f.name}`), true))));
  }

  if (!listing.clips.length) clips.append(el("tr", {}, el("td", { colspan: "5", class: "muted" }, "No saved clips. Rate a candidate, or press Save, to keep one.")));

  const cache = $("st-cache");
  cache.replaceChildren();
  for (const f of listing.cache) {
    cache.append(el("tr", {},
      el("td", { class: "name" }, f.name),
      el("td", {}, stSize(f.bytes)),
      el("td", {}, stWhen(f.expiresUtc)),
      el("td", { class: "actions" },
        stButton("Show file", () => void api.revealStored(f.path)),
        stButton("Delete", () => void stDelete("cache", { path: f.path }, `the cached clip ${f.name}`), true))));
  }

  if (!listing.cache.length) cache.append(el("tr", {}, el("td", { colspan: "4", class: "muted" }, "The cache is empty.")));
}

$("st-refresh").addEventListener("click", () => void stLoad());
$("st-clear-cache").addEventListener("click", () => void stDelete("cache", {}, "every cached clip (saved clips stay)"));
