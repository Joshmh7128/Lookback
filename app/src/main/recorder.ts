import { type ChildProcess, spawn } from "node:child_process";
import { EventEmitter } from "node:events";
import type { RecorderState } from "../shared/api";
import { LineSplitter, pageUrl, parseRecorderLine, type ReadyInfo } from "./handshake";
import type { Command } from "./locate";

export interface RecorderHostOptions {
  /** How long to wait for LOOKBACK_READY before giving up. */
  readyTimeoutMs?: number;
  /** How long a stop waits for a clean exit before killing the process. */
  stopGraceMs?: number;
  /** Log lines kept for the overview. */
  logLines?: number;
}

export interface RecorderHostEvents {
  state: [RecorderState];
  log: [string];
}

/**
 * Runs Lookback.Recorder as a child process and reads its handshake.
 *
 * The recorder's stdout carries the handshake, so the token arrives over the
 * child's pipe and nowhere else: not on a command line, not in an
 * environment block, not on disk. This class keeps it in memory and never
 * logs it; `state` never includes it.
 */
export class RecorderHost extends EventEmitter<RecorderHostEvents> {
  private child: ChildProcess | null = null;
  private ready: ReadyInfo | null = null;
  private profilerUrl: string | null = null;
  private readonly log: string[] = [];
  /** The child's last stderr lines, kept for the crash report. */
  private stderrTail: string[] = [];
  private readyTimer: NodeJS.Timeout | null = null;
  private stopping = false;
  private current: RecorderState = idle("stopped", null);
  private readonly readyTimeoutMs: number;
  private readonly stopGraceMs: number;
  private readonly logLines: number;

  constructor(options: RecorderHostOptions = {}) {
    super();
    this.readyTimeoutMs = options.readyTimeoutMs ?? 60_000;
    this.stopGraceMs = options.stopGraceMs ?? 3_000;
    this.logLines = options.logLines ?? 500;
  }

  get state(): RecorderState {
    return { ...this.current };
  }

  /** Handshake details, including the token, while the recorder is ready. */
  get connection(): ReadyInfo | null {
    return this.current.status === "ready" ? this.ready : null;
  }

  /** The profiler page link (token in its fragment), while ready. */
  get profiler(): string | null {
    return this.current.status === "ready" ? this.profilerUrl : null;
  }

  /** The calibration panel link (token in its fragment), while ready and offered. */
  get calibrate(): string | null {
    const ready = this.connection;
    return ready?.calibrate ? pageUrl(ready.calibrate, ready.token) : null;
  }

  get logTail(): string[] {
    return [...this.log];
  }

  /** The last stderr lines of the current or last run (for a crash report). */
  get stderrLines(): string[] {
    return [...this.stderrTail];
  }

  /** The reason from the recorder's last `lookback: fatal: …` line, if it wrote one. */
  get fatal(): string | null {
    for (let i = this.stderrTail.length - 1; i >= 0; i--) {
      const m = FATAL.exec(this.stderrTail[i] ?? "");
      if (m) return m[1] ?? null;
    }

    return null;
  }

  start(command: Command): void {
    if (this.child) {
      return;
    }

    this.ready = null;
    this.profilerUrl = null;
    this.stopping = false;
    this.stderrTail = [];
    this.set({ ...idle("starting", null), command: command.display, startedAt: Date.now() });
    this.append(`starting ${command.display}`);

    let child: ChildProcess;
    try {
      child = spawn(command.file, command.args, {
        cwd: command.cwd,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        // The environment is inherited as is; the token never goes into it.
        env: { ...process.env, DOTNET_NOLOGO: "1" },
      });
    } catch (err) {
      this.set({ ...idle("failed", `could not start: ${message(err)}`), command: command.display });
      return;
    }

    this.child = child;
    this.set({ ...this.current, pid: child.pid ?? null });

    const out = new LineSplitter((line) => this.onStdout(line));
    const err = new LineSplitter((raw) => {
      // Windows console output ends lines with CRLF.
      const line = raw.replace(/\r$/, "");
      this.append(line);
      this.stderrTail.push(line.length > 500 ? `${line.slice(0, 500)}…` : line);
      if (this.stderrTail.length > STDERR_TAIL) this.stderrTail.shift();
    });
    child.stdout?.setEncoding("utf8").on("data", (chunk: string) => out.push(chunk));
    child.stderr?.setEncoding("utf8").on("data", (chunk: string) => err.push(chunk));

    this.readyTimer = setTimeout(() => {
      if (this.current.status === "starting") {
        this.append(`no LOOKBACK_READY line after ${Math.round(this.readyTimeoutMs / 1000)} s; stopping`);
        this.fail("the recorder did not report ready in time", true);
      }
    }, this.readyTimeoutMs);

    child.once("error", (e) => {
      this.append(`recorder process error: ${message(e)}`);
      this.fail(`could not start: ${message(e)}`);
      if (child.pid === undefined) {
        // It never started, so no exit event follows.
        this.clearTimer();
        this.child = null;
      }
    });

    child.once("exit", (code, signal) => {
      out.flush();
      err.flush();
      this.clearTimer();
      this.child = null;
      this.ready = null;
      this.profilerUrl = null;
      const how = signal ? `signal ${signal}` : `code ${code}`;
      this.append(`recorder exited (${how})`);
      if (this.stopping) {
        this.set({ ...idle("stopped", null), command: this.current.command });
      } else if (this.current.status !== "failed") {
        const fatal = this.fatal;
        this.set({ ...idle("exited", `the recorder exited unexpectedly (${how})${fatal ? `: ${fatal}` : ""}`), command: this.current.command, restartable: true });
      } else {
        this.set({ ...this.current, pid: null });
      }
    });
  }

  /** Stops the recorder: a polite signal first, then a kill after the grace period. */
  async stop(): Promise<void> {
    const child = this.child;
    if (!child) {
      if (this.current.status !== "stopped") {
        this.set({ ...idle("stopped", null), command: this.current.command });
      }

      return;
    }

    this.stopping = true;
    this.clearTimer();
    const exited = new Promise<void>((resolve) => child.once("exit", () => resolve()));
    // On Windows there is no SIGTERM: kill() terminates the process, and the
    // OS closes its pipe and socket, which ends every client's connection.
    child.kill("SIGTERM");
    const timer = setTimeout(() => child.kill("SIGKILL"), this.stopGraceMs);
    await exited;
    clearTimeout(timer);
  }

  /** Adds a shell-side line to the log the overview shows. */
  note(line: string): void {
    this.append(line);
  }

  /** Marks the recorder as failed without a process, e.g. when it could not be found. */
  reportFailure(detail: string): void {
    if (!this.child) {
      this.append(detail);
      this.set({ ...idle("failed", detail), command: null });
    }
  }

  private onStdout(raw: string): void {
    const line = parseRecorderLine(raw);
    switch (line.kind) {
      case "ready":
        this.ready = line.info;
        this.clearTimer();
        this.append(`ready: pipe ${line.info.pipe}, events ${line.info.ws}${line.info.ingest ? `, ingest ${line.info.ingest}` : ""}`);
        this.set({
          ...this.current,
          status: "ready",
          pipe: line.info.pipe,
          endpoint: line.info.ws,
          ingest: line.info.ingest !== null,
          calibrate: line.info.calibrate !== null,
          detail: null,
        });
        break;
      case "profiler":
        this.profilerUrl = line.url;
        this.append("profiler page available");
        this.set({ ...this.current, profiler: true });
        break;
      case "invalid":
        // The reason never contains the line itself, which may hold the token.
        this.append(`ignored a malformed handshake line: ${line.reason}`);
        break;
      case "log":
        this.append(line.text);
        break;
    }
  }

  private fail(detail: string, restartable = false): void {
    this.set({ ...this.current, status: "failed", detail, endpoint: null, profiler: false, ingest: false, calibrate: false, restartable });
    this.ready = null;
    this.profilerUrl = null;
    this.child?.kill("SIGKILL");
  }

  private clearTimer(): void {
    if (this.readyTimer) {
      clearTimeout(this.readyTimer);
      this.readyTimer = null;
    }
  }

  private append(line: string): void {
    const text = line.length > 2000 ? `${line.slice(0, 2000)}…` : line;
    this.log.push(text);
    if (this.log.length > this.logLines) {
      this.log.splice(0, this.log.length - this.logLines);
    }

    this.emit("log", text);
  }

  private set(state: RecorderState): void {
    this.current = state;
    this.emit("state", this.state);
  }
}

function idle(status: RecorderState["status"], detail: string | null): RecorderState {
  return { status, command: null, pid: null, pipe: null, endpoint: null, profiler: false, ingest: false, calibrate: false, detail, startedAt: null, restartable: false };
}

const STDERR_TAIL = 20;
/** What the recorder's watchdogs write to stderr just before they end the process on purpose. */
const FATAL = /^\s*lookback: fatal: (.+)$/;

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
