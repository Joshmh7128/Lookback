import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { test } from "node:test";
import type { RecorderState } from "../shared/api";
import { RecorderSupervisor } from "../main/supervisor";

function state(status: RecorderState["status"], restartable = false, detail: string | null = null): RecorderState {
  return { status, command: "Lookback.Recorder.exe", pid: null, pipe: null, endpoint: null, profiler: false, ingest: false, calibrate: false, detail, startedAt: null, restartable };
}

/** A recorder stand-in and a hand-driven clock. */
function setup(startResult = true) {
  const recorder = Object.assign(new EventEmitter(), { state: state("stopped"), stderrLines: ["info: up", "lookback: fatal: encode stalled"], fatal: "encode stalled" as string | null });
  const timers: { fn: () => void; at: number; id: number }[] = [];
  let now = 1_000_000;
  let id = 0;
  let starts = 0;
  const sup = new RecorderSupervisor(
    recorder,
    () => {
      starts++;
      return startResult;
    },
    {
      now: () => now,
      setTimer: (fn, ms) => {
        timers.push({ fn, at: now + ms, id: ++id });
        return id;
      },
      clearTimer: (h) => {
        const i = timers.findIndex((t) => t.id === h);
        if (i >= 0) timers.splice(i, 1);
      },
    },
  );
  const emit = (s: RecorderState): void => {
    recorder.emit("state", s);
  };
  const advance = (ms: number): void => {
    now += ms;
    for (const t of [...timers].sort((a, b) => a.at - b.at)) {
      if (t.at <= now) {
        timers.splice(timers.indexOf(t), 1);
        t.fn();
      }
    }
  };
  /** One run: up, then an unexpected exit. */
  const crash = (): void => {
    emit(state("starting"));
    emit(state("ready"));
    emit(state("exited", true, "the recorder exited unexpectedly (code 134): encode stalled"));
  };
  return { sup, emit, advance, crash, starts: () => starts, timers, recorder };
}

test("an unexpected exit restarts after 1 s, then 2 s, then 5 s, and records the crash", () => {
  const t = setup();
  t.crash();
  const s = t.sup.state;
  assert.equal(s.nextRestartAt !== null, true);
  assert.equal(s.lastCrash?.fatal, "encode stalled");
  assert.deepEqual(s.lastCrash?.stderr, ["info: up", "lookback: fatal: encode stalled"]);
  t.advance(999);
  assert.equal(t.starts(), 0);
  t.advance(1);
  assert.equal(t.starts(), 1);
  t.crash();
  t.advance(1999);
  assert.equal(t.starts(), 1);
  t.advance(1);
  assert.equal(t.starts(), 2);
  t.crash();
  t.advance(4999);
  assert.equal(t.starts(), 2);
  t.advance(1);
  assert.equal(t.starts(), 3);
  assert.equal(t.sup.state.restarts, 3);
});

test("after five restarts in a minute it gives up until the user starts it", () => {
  const t = setup();
  for (let i = 0; i < 5; i++) {
    t.crash();
    t.advance(5000);
  }

  assert.equal(t.starts(), 5);
  t.crash();
  t.advance(60_000 - 25_001); // still inside the minute of the first restarts
  assert.equal(t.starts(), 5);
  assert.equal(t.sup.state.gaveUp, true);
  assert.equal(t.sup.state.nextRestartAt, null);

  t.sup.reset();
  assert.equal(t.sup.state.gaveUp, false);
  assert.equal(t.sup.state.restarts, 0);
});

test("restarts older than a minute do not count", () => {
  const t = setup();
  for (let i = 0; i < 4; i++) {
    t.crash();
    t.advance(5000);
  }

  t.advance(61_000);
  t.crash();
  t.advance(1000); // back to the first delay
  assert.equal(t.starts(), 5);
  assert.equal(t.sup.state.restarts, 1);
});

test("a stop the user asked for, or a failure a restart cannot fix, is left alone", () => {
  const t = setup();
  t.emit(state("starting"));
  t.emit(state("ready"));
  t.emit(state("stopped"));
  t.emit(state("starting"));
  t.emit(state("failed", false, "could not start: ENOENT"));
  t.emit(state("failed", true, "never started")); // not running before, so nothing to restart
  assert.equal(t.timers.length, 0);
  assert.equal(t.sup.state.lastCrash, null);
});

test("a user action cancels a pending restart; a recorder that cannot be found stops the retries", () => {
  const t = setup();
  t.crash();
  assert.equal(t.timers.length, 1);
  t.sup.reset();
  assert.equal(t.timers.length, 0);
  t.advance(10_000);
  assert.equal(t.starts(), 0);

  const missing = setup(false);
  missing.crash();
  missing.advance(1000);
  assert.equal(missing.sup.state.gaveUp, true);
});
