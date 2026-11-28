import { spawn as nodeSpawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import type { ModelSummary, TrainingRun, TrainingStatus } from "../shared/api";
import { LineSplitter } from "./handshake";
import type { Command } from "./locate";

// The training environment: the checkout's ml/ package run from the app.
//
//   Set up   creates ml/.venv with the Python launcher and installs the pinned
//            packages from ml/requirements.txt (a download from PyPI; the
//            renderer asks first).
//   Train    `python -m lookback_ml.train --promote`: trains on datasets/, and
//            only installs the model in ml/out/model when it beats the
//            heuristic on held-out sessions; the model it replaces goes to
//            ml/out/history. The verdict comes back on a LOOKBACK_TRAIN line.
//   Roll back `python -m lookback_ml.models rollback`: the previous model again.
//
// After a model changes, main asks the recorder to reload it (reload-model),
// so new candidates are scored with it without a restart. Everything stays in
// the checkout; nothing is uploaded.

export interface TrainingPaths {
  ml: string;
  python: string;
  out: string;
}

export function trainingPaths(repoRoot: string, platform: NodeJS.Platform = process.platform): TrainingPaths {
  const ml = join(repoRoot, "ml");
  return {
    ml,
    python: platform === "win32" ? join(ml, ".venv", "Scripts", "python.exe") : join(ml, ".venv", "bin", "python"),
    out: join(ml, "out", "model"),
  };
}

/** The `LOOKBACK_TRAIN {...}` line `--promote` prints, or null. */
export function parseTrainLine(line: string): TrainingRun | null {
  const prefix = "LOOKBACK_TRAIN ";
  if (!line.startsWith(prefix)) return null;
  try {
    const v = JSON.parse(line.slice(prefix.length)) as Record<string, unknown>;
    if (typeof v["promoted"] !== "boolean" || typeof v["reason"] !== "string") return null;
    return runOf(v);
  } catch {
    return null;
  }
}

function num(v: unknown): number | null {
  return typeof v === "number" && Number.isFinite(v) ? v : null;
}

function metric(overall: unknown, scorer: string, key: string): number | null {
  const o = overall as Record<string, Record<string, unknown>> | null | undefined;
  return num(o?.[scorer]?.[key]);
}

function summaryOf(v: Record<string, unknown>, name: string | null = null): ModelSummary {
  const overall = v["overall"];
  return {
    name,
    trainedAt: typeof v["trainedAt"] === "string" ? v["trainedAt"] : null,
    rows: num(v["rows"]),
    sessions: num(v["sessions"]),
    games: Array.isArray(v["games"]) ? v["games"].filter((g): g is string => typeof g === "string") : [],
    auc: { heuristic: metric(overall, "heuristic", "auc"), model: metric(overall, "calibrated", "auc") },
    logLoss: { heuristic: metric(overall, "heuristic", "log_loss"), model: metric(overall, "calibrated", "log_loss") },
  };
}

function runOf(v: Record<string, unknown>): TrainingRun {
  return {
    at: typeof v["at"] === "string" ? v["at"] : null,
    promoted: v["promoted"] === true,
    refused: v["refused"] === true,
    reason: typeof v["reason"] === "string" ? v["reason"] : "",
    model: v["refused"] === true ? null : summaryOf(v),
  };
}

function readJson(path: string): Record<string, unknown> | null {
  try {
    const v = JSON.parse(readFileSync(path, "utf8")) as unknown;
    return typeof v === "object" && v !== null ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

/** A model folder's model.json as a summary, or null when there is none. */
export function readModel(folder: string, name: string | null = null): ModelSummary | null {
  const manifest = readJson(join(folder, "model.json"));
  if (!manifest) return null;
  const training = (manifest["training"] ?? {}) as Record<string, unknown>;
  return summaryOf({ ...training, games: manifest["games"] }, name);
}

/** What is installed: the model in use, earlier ones (newest first) and the last run's verdict. */
export function readModels(paths: TrainingPaths): Pick<TrainingStatus, "current" | "history" | "lastRun"> {
  const history = join(paths.out, "..", "history");
  let names: string[] = [];
  try {
    names = readdirSync(history, { withFileTypes: true }).filter((d) => d.isDirectory()).map((d) => d.name).sort().reverse();
  } catch {
    names = [];
  }

  const last = readJson(join(paths.out, "..", "runs", "latest", "verdict.json"));
  return {
    current: readModel(paths.out),
    history: names.map((n) => readModel(join(history, n), n)).filter((m): m is ModelSummary => m !== null),
    lastRun: last ? runOf(last) : null,
  };
}

export interface TrainingDeps {
  /** The checkout's paths, or null when the app runs without one. */
  paths: TrainingPaths | null;
  /** The Python to create the environment with, e.g. the `py -3.12` launcher; null when none was found. */
  basePython: () => Command | null;
  /** Asks the recorder to load the model now in the folder; resolves to what it uses, or null. */
  reload: () => Promise<string | null>;
  log: (line: string) => void;
  spawn?: (command: Command) => ChildProcess;
  exists?: (path: string) => boolean;
}

interface TrainingEvents {
  changed: [TrainingStatus];
}

const MAX_LOG = 300;

export class TrainingManager extends EventEmitter<TrainingEvents> {
  private busy: TrainingStatus["busy"] = null;
  private readonly lines: string[] = [];
  private recorderModel: string | null = null;
  private note: string | null = null;
  private readonly spawn: (command: Command) => ChildProcess;
  private readonly exists: (path: string) => boolean;

  constructor(private readonly deps: TrainingDeps) {
    super();
    this.exists = deps.exists ?? existsSync;
    this.spawn =
      deps.spawn ??
      ((c) =>
        nodeSpawn(c.file, c.args, {
          cwd: c.cwd,
          stdio: ["ignore", "pipe", "pipe"],
          windowsHide: true,
          env: { ...process.env, PYTHONUNBUFFERED: "1", PYTHONIOENCODING: "utf-8", PIP_DISABLE_PIP_VERSION_CHECK: "1" },
        }));
  }

  status(): TrainingStatus {
    const paths = this.deps.paths;
    const ready = paths !== null && this.exists(paths.python);
    return {
      available: paths !== null,
      python: !paths ? "missing" : ready ? "ready" : this.deps.basePython() ? "no-environment" : "missing",
      busy: this.busy,
      log: [...this.lines],
      note: this.note,
      recorderModel: this.recorderModel,
      ...(paths ? readModels(paths) : { current: null, history: [], lastRun: null }),
    };
  }

  /** Creates ml/.venv and installs ml/requirements.txt. Null when it ran; otherwise why not. */
  async setup(): Promise<string | null> {
    const paths = this.deps.paths;
    const base = this.deps.basePython();
    if (!paths) return "Training needs the source checkout (ml/ is not there).";
    if (!base) return "No Python 3.12 was found. Install it from python.org (with the py launcher), then try again.";
    return this.exclusive("setup", async () => {
      this.say(`Creating ml/.venv with ${base.display}…`);
      if (!(await this.run({ ...base, args: [...base.args, "-m", "venv", ".venv"], cwd: paths.ml, display: `${base.display} -m venv .venv` }))) {
        return "Could not create the Python environment (see the log).";
      }

      this.say("Installing the pinned packages from ml/requirements.txt…");
      const pip: Command = { file: paths.python, args: ["-m", "pip", "install", "-r", "requirements.txt"], cwd: paths.ml, display: "pip install -r requirements.txt" };
      return (await this.run(pip)) ? null : "Installing the packages failed (see the log).";
    });
  }

  /** Trains, and installs the model only if it beats the heuristic. Null when training ran (promoted or not); otherwise why not. */
  async train(): Promise<string | null> {
    const paths = this.ready();
    if (typeof paths === "string") return paths;
    return this.exclusive("train", async () => {
      let outcome: TrainingRun | null = null;
      this.say("Training on datasets/ (sessions held out for evaluation)…");
      const command: Command = { file: paths.python, args: ["-m", "lookback_ml.train", "--promote"], cwd: paths.ml, display: "python -m lookback_ml.train --promote" };
      await this.run(command, (line) => {
        outcome = parseTrainLine(line) ?? outcome;
      });
      const run = outcome as TrainingRun | null;
      if (!run) return "Training stopped without a verdict (see the log).";
      if (run.promoted) {
        this.say(`Installed: ${run.reason}.`);
        await this.reload();
      } else {
        this.say(`${run.refused ? "Not trained" : "Kept the model in use"}: ${run.reason}`);
      }

      return null;
    });
  }

  /** Puts the previous model back and has the recorder load it. */
  async rollback(): Promise<string | null> {
    const paths = this.ready();
    if (typeof paths === "string") return paths;
    return this.exclusive("rollback", async () => {
      const command: Command = { file: paths.python, args: ["-m", "lookback_ml.models", "rollback"], cwd: paths.ml, display: "python -m lookback_ml.models rollback" };
      if (!(await this.run(command))) return "There is no earlier model to roll back to.";
      this.say("Rolled back to the previous model.");
      await this.reload();
      return null;
    });
  }

  /** Asks the recorder to load the model in the folder now. */
  async reload(): Promise<void> {
    this.recorderModel = await this.deps.reload();
    this.changed();
  }

  private ready(): TrainingPaths | string {
    const paths = this.deps.paths;
    if (!paths) return "Training needs the source checkout (ml/ is not there).";
    if (!this.exists(paths.python)) return "Set up training first.";
    return paths;
  }

  private async exclusive(kind: NonNullable<TrainingStatus["busy"]>, body: () => Promise<string | null>): Promise<string | null> {
    if (this.busy) return "Training is busy; wait for it to finish.";
    this.busy = kind;
    this.note = null;
    this.changed();
    try {
      const result = await body();
      this.note = result;
      return result;
    } finally {
      this.busy = null;
      this.changed();
    }
  }

  private say(line: string): void {
    this.lines.push(line);
    if (this.lines.length > MAX_LOG) this.lines.splice(0, this.lines.length - MAX_LOG);
    this.deps.log(`training: ${line}`);
    this.changed();
  }

  private run(command: Command, onLine?: (line: string) => void): Promise<boolean> {
    return new Promise((resolve) => {
      let child: ChildProcess;
      try {
        child = this.spawn(command);
      } catch (e) {
        this.say(`${command.display}: ${(e as Error).message}`);
        resolve(false);
        return;
      }

      const take = (raw: string): void => {
        const line = raw.replace(/\r$/, "");
        if (!line.trim()) return;
        onLine?.(line);
        if (!line.startsWith("LOOKBACK_TRAIN ")) this.say(line);
      };
      const out = new LineSplitter(take);
      const err = new LineSplitter(take);
      child.stdout?.on("data", (chunk: Buffer) => out.push(chunk.toString()));
      child.stderr?.on("data", (chunk: Buffer) => err.push(chunk.toString()));
      let settled = false;
      child.once("error", (e) => {
        if (settled) return;
        settled = true;
        this.say(`${command.display}: ${e.message}`);
        resolve(false);
      });
      child.once("close", (code) => {
        if (settled) return;
        settled = true;
        out.flush();
        err.flush();
        // 3: --promote trained, but the model did not beat the heuristic (a verdict, not a failure).
        resolve(code === 0 || (code === 3 && command.args.includes("--promote")));
      });
    });
  }

  private changed(): void {
    this.emit("changed", this.status());
  }
}
