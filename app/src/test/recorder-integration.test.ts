import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { eventsUrl } from "../main/handshake";
import { IngestSession } from "../main/ingest";
import { Library } from "../main/library";
import { locateTool, RECORDER } from "../main/locate";
import { RecorderHost } from "../main/recorder";
import { StreamClient } from "../main/stream";
import { delay, until } from "./fakes";

// Runs the shell's recorder host, /events client and /ingest sender against
// the real Lookback.Recorder from this checkout. Skipped when the recorder
// has not been built (`dotnet build Lookback.slnx -c Release`).

const repoRoot = join(__dirname, "..", "..", "..");
const command = locateTool(RECORDER, "", { repoRoot, resourcesDir: null, platform: process.platform, env: process.env, exists: existsSync }, ["--Recorder:PipeName=lookback.apptest", "--Recorder:WebSocketPort=0"]);

/** A moving bright square on a dark frame, so the signals have something to measure. */
function frame(i: number, w = 384, h = 216): Uint8Array {
  const px = new Uint8Array(w * h * 4);
  for (let p = 0; p < w * h; p++) {
    px[p * 4] = 30;
    px[p * 4 + 1] = 20;
    px[p * 4 + 2] = 40;
    px[p * 4 + 3] = 255;
  }

  const x0 = (i * 6) % (w - 40);
  for (let y = 80; y < 120; y++) {
    for (let x = x0; x < x0 + 40; x++) {
      px.fill(240, (y * w + x) * 4, (y * w + x) * 4 + 3);
    }
  }

  return px;
}

test("frames sent to /ingest become an ephemeral official-player session on /events", { skip: command ? false : "Lookback.Recorder is not built", timeout: 90_000 }, async () => {
  const host = new RecorderHost({ readyTimeoutMs: 60_000 });
  host.start(command!);
  const stream = new StreamClient();
  const library = new Library();
  stream.on("envelope", (e) => library.apply(e));
  try {
    await until(() => host.state.status === "ready", 60_000, "recorder ready");
    const info = host.connection!;
    assert.ok(info.ingest, "the recorder offers /ingest");
    assert.ok(info.calibrate, "the recorder offers /calibrate");
    stream.connect(eventsUrl(info));
    await until(() => stream.state.status === "open", 10_000, "events open");

    // Node's WebSocket is what the Electron main process uses.
    const ingest = new IngestSession(info.ingest!, info.token, { platform: "youtube", videoRef: "M7lc1UVf-VE", profile: "" });
    ingest.open();
    await until(() => ingest.streaming, 10_000, "ingest streaming");
    for (let i = 0; i < 90; i++) {
      ingest.sendVideo(frame(i), 384, 216, performance.now());
      await delay(33);
    }

    const official = (): ReturnType<Library["snapshot"]>["sessions"][number] | undefined => library.snapshot().sessions.find((s) => s.sourceKind === "official_player");
    await until(() => (official()?.signalFrames ?? 0) >= 60, 15_000, "signal frames for the player session");
    const s = official()!;
    assert.equal(s.retention, "ephemeral");
    assert.equal(s.title, "YouTube embedded player");
    // The video id is not named anywhere the shell received.
    assert.ok(!JSON.stringify(library.snapshot()).includes("M7lc1UVf-VE"));
    assert.equal(ingest.state.framesSent, 90);

    ingest.close();
    await until(() => official()?.ended === true, 10_000, "session ended after stop");
  } finally {
    stream.disconnect();
    await host.stop();
  }
});

test("/ingest refuses a wrong token", { skip: command ? false : "Lookback.Recorder is not built", timeout: 90_000 }, async () => {
  const host = new RecorderHost({ readyTimeoutMs: 60_000 });
  host.start(command!);
  try {
    await until(() => host.state.status === "ready", 60_000, "recorder ready");
    const ingest = new IngestSession(host.connection!.ingest!, "WRONGTOKEN0123456789", { platform: "youtube", videoRef: "x", profile: "" });
    ingest.open();
    await until(() => ingest.state.status === "error" || (ingest.state.status === "idle" && ingest.state.detail !== null), 10_000, "refusal");
    assert.equal(ingest.streaming, false);
  } finally {
    await host.stop();
  }
});
