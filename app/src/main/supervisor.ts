import { EventEmitter } from "node:events";
import type { RecorderState, SupervisionState } from "../shared/api";

// Keeps the recorder running. When it exits unexpectedly (a crash, or a
// watchdog ending it on purpose with `lookback: fatal: …` on stderr), the
// shell keeps its last stderr lines for the user and starts it again after
// 1 s, 2 s, then 5 s. Each start re-reads LOOKBACK_READY: the port and token
// change. After `maxRestarts` restarts within a minute it stops trying and
// says so; the user can start it again by hand.

export const RESTART_DELAYS_MS = [1000, 2000, 5000];

export interface SupervisorOptions {
  maxRestarts?: number;
  windowMs?: number;
  delays?: number[];
  now?: () => number;
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

export interface SupervisedRecorder {
  readonly state: RecorderState;
  readonly stderrLines: string[];
  readonly fatal: string | null;
  on(event: "state", listener: (state: RecorderState) => void): unknown;
}

interface SupervisorEvents {
  state: [SupervisionState];
}

export class RecorderSupervisor extends EventEmitter<SupervisorEvents> {
  private restartTimes: number[] = [];
  private timer: unknown = null;
  private nextAt: number | null = null;
  private gaveUp = false;
  private lastCrash: SupervisionState["lastCrash"] = null;
  private wasRunning = false;
  private readonly maxRestarts: number;
  private readonly windowMs: number;
  private readonly delays: number[];
  private readonly now: () => number;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;

  /** `start` launches the recorder again with a fresh command (and returns false when it cannot be found). */
  constructor(
    private readonly recorder: SupervisedRecorder,
    private readonly start: () => boolean,
    options: SupervisorOptions = {},
  ) {
    super();
    this.maxRestarts = options.maxRestarts ?? 5;
    this.windowMs = options.windowMs ?? 60_000;
    this.delays = options.delays ?? RESTART_DELAYS_MS;
    this.now = options.now ?? Date.now;
    this.setTimer = options.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = options.clearTimer ?? ((h) => clearTimeout(h as NodeJS.Timeout));
    recorder.on("state", (s) => this.onState(s));
  }

  get state(): SupervisionState {
    return {
      restarts: this.recent().length,
      maxRestarts: this.maxRestarts,
      nextRestartAt: this.nextAt,
      gaveUp: this.gaveUp,
      lastCrash: this.lastCrash ? { ...this.lastCrash, stderr: [...this.lastCrash.stderr] } : null,
    };
  }

  /** The user started, stopped or restarted the recorder: cancel any pending restart and start counting afresh. */
  reset(): void {
    this.cancel();
    this.restartTimes = [];
    this.gaveUp = false;
    this.emitState();
  }

  /** Stops supervising (the app is quitting). */
  dispose(): void {
    this.cancel();
  }

  private onState(s: RecorderState): void {
    if (s.status === "ready" || s.status === "starting") {
      this.wasRunning = true;
      return;
    }

    if (!this.wasRunning || !s.restartable) {
      // A stop the user asked for, or a failure a restart cannot fix (not found, cannot spawn).
      this.wasRunning = false;
      return;
    }

    this.wasRunning = false;
    this.lastCrash = { at: this.now(), detail: s.detail ?? "the recorder stopped", fatal: this.recorder.fatal, stderr: this.recorder.stderrLines.slice(-10) };
    const recent = this.recent();
    if (recent.length >= this.maxRestarts) {
      this.gaveUp = true;
      this.nextAt = null;
      this.emitState();
      return;
    }

    const delay = this.delays[Math.min(recent.length, this.delays.length - 1)] ?? 5000;
    this.cancel();
    this.nextAt = this.now() + delay;
    this.timer = this.setTimer(() => {
      this.timer = null;
      this.nextAt = null;
      this.restartTimes.push(this.now());
      if (!this.start()) {
        this.gaveUp = true;
      }

      this.emitState();
    }, delay);
    this.emitState();
  }

  private recent(): number[] {
    const since = this.now() - this.windowMs;
    this.restartTimes = this.restartTimes.filter((t) => t > since);
    return this.restartTimes;
  }

  private cancel(): void {
    if (this.timer !== null) {
      this.clearTimer(this.timer);
      this.timer = null;
    }

    this.nextAt = null;
  }

  private emitState(): void {
    this.emit("state", this.state);
  }
}
