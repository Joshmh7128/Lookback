import { spawn } from "node:child_process";
import type { ProfileEdits, ProfileToolResult } from "../shared/api";
import type { Command } from "./locate";

// The Profiles tab edits weights and thresholds through
// `lookback-testbench profile-view` / `profile-save`, which change the
// checkout's profiles/ files in place after checking that the whole folder
// still loads the way the recorder loads it. The renderer's edits are checked
// here first (shape, names, finite numbers) so only a well-formed edit
// reaches the tool; the tool does the real validation.

const ID = /^[a-z0-9-]+(\.[a-z0-9-]+)*$/;
const NAME = /^[A-Za-z0-9_.-]{1,128}$/;
const CLIPPER_FIELDS = new Set(["high", "low", "preMs", "postMs", "refractoryMs", "peakScore"]);
const MAX_EDITS = 512;

export function isProfileId(id: unknown): id is string {
  return typeof id === "string" && id.length <= 128 && ID.test(id);
}

export function profileViewArgs(profilesDir: string, id: string | null): string[] {
  return ["profile-view", "--profiles", profilesDir, ...(id ? ["--id", id] : [])];
}

export function profileSaveArgs(profilesDir: string, id: string): string[] {
  return ["profile-save", "--profiles", profilesDir, "--id", id, "--edits", "-"];
}

function finite(v: unknown): v is number {
  return typeof v === "number" && Number.isFinite(v);
}

/**
 * The renderer's edits as a clean object, or null when anything in them is
 * malformed. Unknown fields are dropped; a null clipper value or component
 * weight means "inherit again".
 */
export function sanitizeEdits(raw: unknown): ProfileEdits | null {
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) return null;
  const r = raw as Record<string, unknown>;
  const out: ProfileEdits = { events: [], composites: [], clipper: {}, components: {} };
  let count = 0;
  const more = (): boolean => ++count <= MAX_EDITS;

  if (r["events"] !== undefined && !Array.isArray(r["events"])) return null;
  if (r["composites"] !== undefined && !Array.isArray(r["composites"])) return null;
  for (const e of (r["events"] as unknown[] | undefined) ?? []) {
    if (typeof e !== "object" || e === null) return null;
    const { name, weight, thresholds } = e as Record<string, unknown>;
    if (typeof name !== "string" || !NAME.test(name) || !more()) return null;
    const edit: ProfileEdits["events"][number] = { name };
    if (weight !== undefined) {
      if (!finite(weight)) return null;
      edit.weight = weight;
    }

    if (thresholds !== undefined) {
      if (typeof thresholds !== "object" || thresholds === null || Array.isArray(thresholds)) return null;
      edit.thresholds = {};
      for (const [k, v] of Object.entries(thresholds as Record<string, unknown>)) {
        if (!/^\d{1,3}$/.test(k) || !finite(v) || !more()) return null;
        edit.thresholds[k] = v;
      }
    }

    out.events.push(edit);
  }

  for (const c of (r["composites"] as unknown[] | undefined) ?? []) {
    if (typeof c !== "object" || c === null) return null;
    const { name, weight } = c as Record<string, unknown>;
    if (typeof name !== "string" || !NAME.test(name) || !finite(weight) || !more()) return null;
    out.composites.push({ name, weight });
  }

  for (const key of ["clipper", "components"] as const) {
    const o = r[key];
    if (o === undefined) continue;
    if (typeof o !== "object" || o === null || Array.isArray(o)) return null;
    for (const [k, v] of Object.entries(o as Record<string, unknown>)) {
      const nameOk = key === "clipper" ? CLIPPER_FIELDS.has(k) : NAME.test(k);
      if (!nameOk || !(v === null || finite(v)) || !more()) return null;
      out[key][k] = v;
    }
  }

  return out;
}

/** True when the edits change nothing. */
export function isEmptyEdits(e: ProfileEdits): boolean {
  return e.events.length === 0 && e.composites.length === 0 && Object.keys(e.clipper).length === 0 && Object.keys(e.components).length === 0;
}

/**
 * Runs a profile command and parses the one JSON object it prints. The tool
 * reports refusals as `{ ok: false, message }`; anything else that goes wrong
 * (not found, crash, timeout, garbage) becomes a refusal here too.
 */
export function runProfileTool(command: Command, stdin: string | null, timeoutMs = 60_000): Promise<ProfileToolResult> {
  return new Promise((resolve) => {
    let out = "";
    let err = "";
    let done = false;
    const finish = (result: ProfileToolResult): void => {
      if (!done) {
        done = true;
        clearTimeout(timer);
        resolve(result);
      }
    };

    const child = spawn(command.file, command.args, { cwd: command.cwd, stdio: ["pipe", "pipe", "pipe"], windowsHide: true, env: { ...process.env, DOTNET_NOLOGO: "1" } });
    child.stdout.setEncoding("utf8").on("data", (s: string) => {
      if (out.length < 8 * 1024 * 1024) out += s;
    });
    child.stderr.setEncoding("utf8").on("data", (s: string) => {
      err = (err + s).slice(-4000);
    });
    const timer = setTimeout(() => {
      child.kill();
      finish({ ok: false, message: `${command.display} took longer than ${timeoutMs / 1000} s` });
    }, timeoutMs);
    child.once("error", (e) => finish({ ok: false, message: `could not run ${command.display}: ${e.message}` }));
    child.once("exit", (code) => {
      const line = out.trim().split(/\r?\n/).pop() ?? "";
      try {
        const parsed = JSON.parse(line) as ProfileToolResult;
        if (typeof parsed === "object" && parsed !== null && typeof parsed.ok === "boolean") {
          finish(parsed);
          return;
        }
      } catch {
        // fall through
      }

      const tail = err.trim().split(/\r?\n/).slice(-6).join("\n");
      finish({ ok: false, message: tail || `${command.display} exited with code ${code} and printed no result` });
    });
    child.stdin.on("error", () => {
      // the tool may exit before reading stdin; its exit reports why
    });
    child.stdin.end(stdin ?? "");
  });
}
