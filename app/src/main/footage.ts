import { spawn as nodeSpawn, type ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import type { FootageJob, FootageRequest, SessionView } from "../shared/api";
import { analyzeMessage, newRequestId, type Ack } from "./control";
import { LineSplitter } from "./handshake";
import type { Command } from "./locate";

// "Open footage…": analyse a file the user owns (or has permission for) so
// its clip candidates can be previewed and rated.
//
// First choice is the recorder's `analyze` command (agreed, recorder side
// pending). When the recorder does not ack it, the shell uses the path that
// already exists: `lookback-testbench live <file> --pipe <recorder pipe>`,
// which analyses the file, applies RetentionRules, writes nothing, and
// publishes an ordinary FILE session into the recorder's bus, so it reaches
// /events like any other. Either way the session's title is the file's path,
// which is how the shell links the session back to the file for previews.
// The path stays in the main process's memory.

/** Playback speed for the testbench fallback: fast, but within what the stream comfortably carries. */
export const FALLBACK_SPEED = 8;

export interface FootageDeps {
  /** Sends a client command on /events; null when no ack came. */
  request: (id: string, text: string) => Promise<Ack | null>;
  /** The recorder's pipe name while it is ready. */
  pipe: () => string | null;
  /** The testbench command for these arguments, or null when it is not built. */
  testbench: (args: string[]) => Command | null;
  /** Sessions with this title (oldest first). */
  sessionsByTitle: (title: string) => SessionView[];
  /** Marks a session previewable once it is linked to its file. */
  linked: (sessionId: string) => void;
  log: (line: string) => void;
  spawn?: (command: Command) => ChildProcess;
}

interface Job extends FootageJob {
  path: string;
  child: ChildProcess | null;
  tail: string[];
}

interface FootageEvents {
  changed: [FootageJob[]];
}

export class FootageManager extends EventEmitter<FootageEvents> {
  private readonly jobs: Job[] = [];
  private readonly claimed = new Set<string>();
  private readonly spawn: (command: Command) => ChildProcess;

  constructor(private readonly deps: FootageDeps) {
    super();
    this.spawn =
      deps.spawn ??
      ((c) => nodeSpawn(c.file, c.args, { cwd: c.cwd, stdio: ["ignore", "pipe", "pipe"], windowsHide: true, env: { ...process.env, DOTNET_NOLOGO: "1" } }));
  }

  get list(): FootageJob[] {
    return this.jobs.map(publicJob);
  }

  /** The analysed file behind a session, if the shell opened it. */
  pathFor(sessionId: string): string | null {
    return this.jobs.find((j) => j.sessionId === sessionId)?.path ?? null;
  }

  async open(path: string, request: FootageRequest): Promise<FootageJob> {
    const job: Job = { id: newRequestId(), name: fileName(path), profile: request.profile || "generic", via: null, status: "starting", sessionId: null, detail: null, path, child: null, tail: [] };
    this.jobs.unshift(job);
    this.changed();

    const ack = await this.deps.request(job.id, analyzeMessage(job.id, path, request));
    if (ack?.ok) {
      job.via = "recorder";
      job.status = "analysing";
      this.deps.log(`footage: the recorder is analysing ${job.name}`);
    } else if (ack && ack.error !== "bad-request") {
      // The recorder understood and refused (for example retention): do not work around it.
      job.status = "failed";
      job.detail = `the recorder refused it (${ack.error ?? "no reason"})`;
    } else {
      this.startTestbench(job, request);
    }

    this.link();
    this.changed();
    return publicJob(job);
  }

  /** Links newly started sessions to their files and finishes jobs whose session ended. Call when the library changes. */
  link(): void {
    let changed = false;
    for (const job of this.jobs) {
      if (!job.sessionId && (job.status === "analysing" || job.status === "starting")) {
        const session = this.deps.sessionsByTitle(job.path).find((s) => s.sourceKind === "file" && !this.claimed.has(s.id));
        if (session) {
          job.sessionId = session.id;
          this.claimed.add(session.id);
          this.deps.linked(session.id);
          changed = true;
        }
      }

      if (job.sessionId && job.status === "analysing" && job.via === "recorder") {
        const s = this.deps.sessionsByTitle(job.path).find((x) => x.id === job.sessionId);
        if (s?.ended) {
          job.status = "done";
          changed = true;
        }
      }
    }

    if (changed) {
      this.changed();
    }
  }

  /** Stops any testbench runs (on quit). */
  stopAll(): void {
    for (const job of this.jobs) {
      job.child?.kill();
    }
  }

  private startTestbench(job: Job, request: FootageRequest): void {
    const pipe = this.deps.pipe();
    if (!pipe) {
      job.status = "failed";
      job.detail = "the recorder is not running";
      return;
    }

    const args = ["live", job.path, "--profile", job.profile, "--pipe", pipe, "--no-wait", "--speed", String(FALLBACK_SPEED), "--title", job.path, "--retention", request.retention];
    if (request.retention !== "owned") {
      args.push("--permission-ref", request.permissionRef);
    }

    const command = this.deps.testbench(args);
    if (!command) {
      job.status = "failed";
      job.detail = "lookback-testbench was not found; build the solution or set its path in Settings";
      return;
    }

    job.via = "testbench";
    job.status = "analysing";
    this.deps.log(`footage: analysing ${job.name} with ${command.display} at ${FALLBACK_SPEED}x (the recorder has no analyze command yet)`);
    let child: ChildProcess;
    try {
      child = this.spawn(command);
    } catch (err) {
      job.status = "failed";
      job.detail = err instanceof Error ? err.message : String(err);
      return;
    }

    job.child = child;
    const keep = new LineSplitter((line) => {
      if (line.trim()) {
        job.tail.push(line.trim());
        if (job.tail.length > 12) job.tail.shift();
      }
    });
    child.stdout?.setEncoding("utf8").on("data", (c: string) => keep.push(c));
    child.stderr?.setEncoding("utf8").on("data", (c: string) => keep.push(c));
    child.once("error", (err) => {
      job.child = null;
      job.status = "failed";
      job.detail = `could not run the testbench: ${err.message}`;
      this.changed();
    });
    child.once("exit", (code) => {
      keep.flush();
      job.child = null;
      if (job.status === "analysing") {
        job.status = code === 0 ? "done" : "failed";
        job.detail = code === 0 ? null : lastError(job.tail) ?? `the testbench exited with code ${code}`;
        this.deps.log(`footage: ${job.name} ${job.status}${job.detail ? `: ${job.detail}` : ""}`);
      }

      this.link();
      this.changed();
    });
  }

  private changed(): void {
    this.emit("changed", this.list);
  }
}

/** The file name of a Windows or POSIX path (the shell may show a path from either). */
export function fileName(path: string): string {
  return path.split(/[\\/]/).pop() || path;
}

function publicJob(j: Job): FootageJob {
  return { id: j.id, name: j.name, profile: j.profile, via: j.via, status: j.status, sessionId: j.sessionId, detail: j.detail };
}

/** The most useful line of a failed run: an exception message, else the last line. */
function lastError(lines: string[]): string | null {
  const err = lines.find((l) => /exception|error|refused|not found|no video/i.test(l));
  return (err ?? lines.at(-1) ?? null)?.slice(0, 300) ?? null;
}
