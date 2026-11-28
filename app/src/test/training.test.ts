import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import type { ChildProcess } from "node:child_process";
import { describeModel, reloadModelMessage } from "../main/control";
import type { Command } from "../main/locate";
import { parseTrainLine, readModels, TrainingManager, trainingPaths } from "../main/training";

class FakeChild extends EventEmitter {
  stdout = new PassThrough();
  stderr = new PassThrough();
  /** Prints `lines` and exits with `code`. */
  finish(lines: string[], code: number): void {
    for (const l of lines) this.stdout.write(`${l}\n`);
    setImmediate(() => this.emit("close", code));
  }
}

const OVERALL = { heuristic: { auc: 0.6, log_loss: 0.7 }, calibrated: { auc: 0.8, log_loss: 0.5 } };

function manifest(trainedAt: string, rows: number): string {
  return JSON.stringify({ games: ["cs2"], training: { trainedAt, rows, sessions: 4, overall: OVERALL } });
}

test("the train verdict line parses, with the held-out numbers", () => {
  const run = parseTrainLine(`LOOKBACK_TRAIN ${JSON.stringify({ at: "2026-11-28T08:00:00+00:00", promoted: true, refused: false, reason: "it beats the heuristic", trainedAt: "2026-11-28T08:00:00+00:00", rows: 50, sessions: 5, games: ["cs2"], overall: OVERALL })}`);
  assert.ok(run);
  assert.equal(run.promoted, true);
  assert.deepEqual(run.model?.auc, { heuristic: 0.6, model: 0.8 });
  assert.deepEqual(run.model?.games, ["cs2"]);
  assert.equal(parseTrainLine("model    out/model.onnx"), null);
  assert.equal(parseTrainLine("LOOKBACK_TRAIN {broken"), null);
  const refused = parseTrainLine(`LOOKBACK_TRAIN ${JSON.stringify({ promoted: false, refused: true, reason: "refusing to train: 38 usable ratings, need at least 40" })}`);
  assert.equal(refused?.refused, true);
  assert.equal(refused?.model, null);
});

test("installed models are read from the checkout's ml/out", () => {
  const root = mkdtempSync(join(tmpdir(), "lookback-training-"));
  try {
    const paths = trainingPaths(root, "win32");
    assert.equal(paths.python, join(root, "ml", ".venv", "Scripts", "python.exe"));
    assert.deepEqual(readModels(paths), { current: null, history: [], lastRun: null });

    mkdirSync(paths.out, { recursive: true });
    writeFileSync(join(paths.out, "model.json"), manifest("2026-11-29T00:00:00+00:00", 60));
    for (const [name, rows] of [["20261127-000000", 30], ["20261128-000000", 40]] as const) {
      mkdirSync(join(root, "ml", "out", "history", name), { recursive: true });
      writeFileSync(join(root, "ml", "out", "history", name, "model.json"), manifest("x", rows));
    }

    mkdirSync(join(root, "ml", "out", "runs", "latest"), { recursive: true });
    writeFileSync(join(root, "ml", "out", "runs", "latest", "verdict.json"), JSON.stringify({ at: "t", promoted: false, refused: false, reason: "it ranks worse", overall: OVERALL, rows: 61 }));
    const models = readModels(paths);
    assert.equal(models.current?.rows, 60);
    assert.deepEqual(models.history.map((m) => [m.name, m.rows]), [["20261128-000000", 40], ["20261127-000000", 30]]);
    assert.equal(models.lastRun?.promoted, false);
    assert.equal(models.lastRun?.model?.rows, 61);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

function manager(options: { python?: boolean; base?: boolean; exits: Array<{ lines: string[]; code: number }> }) {
  const spawned: Command[] = [];
  const reloads: number[] = [];
  const paths = trainingPaths("/repo", "linux");
  let n = 0;
  const m = new TrainingManager({
    paths,
    basePython: () => (options.base === false ? null : { file: "python3.12", args: [], cwd: "/repo", display: "python3.12" }),
    reload: async () => {
      reloads.push(1);
      return "the model trained t (games: cs2)";
    },
    log: () => undefined,
    exists: (p) => p === paths.python && options.python !== false,
    spawn: (c) => {
      spawned.push(c);
      const child = new FakeChild();
      const exit = options.exits[n++] ?? { lines: [], code: 0 };
      setImmediate(() => child.finish(exit.lines, exit.code));
      return child as unknown as ChildProcess;
    },
  });
  return { m, spawned, reloads };
}

test("a promoted model makes the recorder reload; one that is not installed does not", async () => {
  const promoted = `LOOKBACK_TRAIN ${JSON.stringify({ promoted: true, refused: false, reason: "it beats the heuristic" })}`;
  const a = manager({ exits: [{ lines: ["# Learned clip scorer: evaluation", promoted], code: 0 }] });
  assert.equal(await a.m.train(), null);
  assert.deepEqual(a.spawned[0]?.args, ["-m", "lookback_ml.train", "--promote"]);
  assert.equal(a.spawned[0]?.cwd, join("/repo", "ml"));
  assert.equal(a.reloads.length, 1);
  const status = a.m.status();
  assert.equal(status.busy, null);
  assert.equal(status.recorderModel, "the model trained t (games: cs2)");
  assert.ok(status.log.some((l) => l.includes("Installed")));
  assert.ok(!status.log.some((l) => l.startsWith("LOOKBACK_TRAIN")));

  const kept = `LOOKBACK_TRAIN ${JSON.stringify({ promoted: false, refused: false, reason: "it ranks worse than the heuristic" })}`;
  const b = manager({ exits: [{ lines: [kept], code: 3 }] });
  assert.equal(await b.m.train(), null);
  assert.equal(b.reloads.length, 0);
  assert.ok(b.m.status().log.some((l) => l.includes("Kept the model in use")));

  const c = manager({ exits: [{ lines: ["Traceback (most recent call last):"], code: 1 }] });
  assert.match((await c.m.train()) ?? "", /without a verdict/);
});

test("set up creates the environment and installs the pinned packages; training waits for it", async () => {
  const none = manager({ python: false, base: false, exits: [] });
  assert.equal(none.m.status().python, "missing");
  assert.match((await none.m.setup()) ?? "", /Python 3\.12/);
  assert.match((await none.m.train()) ?? "", /Set up training first/);

  const s = manager({ python: false, exits: [{ lines: [], code: 0 }, { lines: ["Successfully installed numpy"], code: 0 }] });
  assert.equal(s.m.status().python, "no-environment");
  assert.equal(await s.m.setup(), null);
  assert.deepEqual(s.spawned.map((c) => [c.file, ...c.args]), [
    ["python3.12", "-m", "venv", ".venv"],
    [join("/repo", "ml", ".venv", "bin", "python"), "-m", "pip", "install", "-r", "requirements.txt"],
  ]);
});

test("rollback runs the models command and reloads; only one action at a time", async () => {
  const r = manager({ exits: [{ lines: ["{}"], code: 0 }] });
  const first = r.m.rollback();
  assert.match((await r.m.train()) ?? "", /busy/);
  assert.equal(await first, null);
  assert.deepEqual(r.spawned[0]?.args, ["-m", "lookback_ml.models", "rollback"]);
  assert.equal(r.reloads.length, 1);

  const none = manager({ exits: [{ lines: ["there is no earlier model to roll back to"], code: 2 }] });
  assert.match((await none.m.rollback()) ?? "", /no earlier model/);
  assert.equal(none.reloads.length, 0);
});

test("the reload command and its answer", () => {
  assert.deepEqual(JSON.parse(reloadModelMessage("r")), { type: "reload-model", id: "r" });
  assert.equal(describeModel({ model: false, games: [] }), "heuristic scores (no model)");
  assert.equal(describeModel({ model: true, games: ["cs2", "overwatch"], trainedAt: "2026-11-28T08:00:00+00:00" }), "the model trained 2026-11-28T08:00:00+00:00 (games: cs2, overwatch)");
  assert.equal(describeModel(null), null);
});
