import { join } from "node:path";
import type { Settings } from "../shared/api";

/** A process to spawn. `display` is what the overview shows. */
export interface Command {
  file: string;
  args: string[];
  cwd: string;
  display: string;
}

export interface LocateContext {
  /** Repository root in a development checkout, or null when packaged. */
  repoRoot: string | null;
  /** Electron's resources folder when packaged, or null. */
  resourcesDir: string | null;
  platform: NodeJS.Platform;
  env: NodeJS.ProcessEnv;
  exists: (path: string) => boolean;
}

interface Tool {
  /** Assembly name, which is also the apphost name. */
  assembly: string;
  /** Project folder under src/. */
  project: string;
  /** Folder under resources/ in a packaged app. */
  packaged: string;
  /** Environment variable that overrides the location. */
  envVar: string;
}

export const RECORDER: Tool = { assembly: "Lookback.Recorder", project: "Lookback.Recorder", packaged: "recorder", envVar: "LOOKBACK_RECORDER" };
export const TESTBENCH: Tool = { assembly: "lookback-testbench", project: "Lookback.Testbench", packaged: "testbench", envVar: "LOOKBACK_TESTBENCH" };

/**
 * Finds a .NET tool, in order: the path in settings, the environment
 * variable, the packaged copy, then a build in the repository (Release
 * before Debug, the apphost before `dotnet <dll>`). Returns null when none
 * exists; the caller tells the user to build it.
 */
export function locateTool(tool: Tool, configured: string, ctx: LocateContext, args: string[]): Command | null {
  const cwd = ctx.repoRoot ?? ctx.resourcesDir ?? process.cwd();
  const exe = ctx.platform === "win32" ? `${tool.assembly}.exe` : tool.assembly;

  const explicit = configured || ctx.env[tool.envVar] || "";
  if (explicit) {
    return ctx.exists(explicit) ? fromPath(explicit, args, cwd) : null;
  }

  const candidates: string[] = [];
  if (ctx.resourcesDir) {
    candidates.push(join(ctx.resourcesDir, tool.packaged, exe));
  }

  if (ctx.repoRoot) {
    for (const config of ["Release", "Debug"]) {
      const dir = join(ctx.repoRoot, "src", tool.project, "bin", config, "net10.0");
      candidates.push(join(dir, exe), join(dir, `${tool.assembly}.dll`));
    }
  }

  const found = candidates.find((c) => ctx.exists(c));
  return found ? fromPath(found, args, cwd) : null;
}

function fromPath(path: string, args: string[], cwd: string): Command {
  if (path.toLowerCase().endsWith(".dll")) {
    return { file: "dotnet", args: [path, ...args], cwd, display: `dotnet ${basename(path)}` };
  }

  return { file: path, args, cwd, display: basename(path) };
}

function basename(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export interface RecorderExtras {
  /** The checkout's profiles folder: turns on the calibration panel's Save. */
  profilesDir?: string | null;
  /** Where ratings go (`<dir>/<game>/ratings.jsonl`); unset, the recorder uses %LOCALAPPDATA%/Lookback/datasets. */
  datasetsDir?: string | null;
  /** The shell's pid: the recorder exits when the shell does, however it ends. */
  parentPid?: number | null;
}

/**
 * Recorder arguments for the chosen settings. The token is never an
 * argument. Options an older recorder does not know are ignored by its
 * configuration binder.
 */
export function recorderArgs(settings: Settings, extras: RecorderExtras = {}): string[] {
  const args = [`--Recorder:PipeName=${settings.pipeName}`, `--Recorder:WebSocketPort=${settings.webSocketPort}`];
  if (settings.profilerPage) {
    args.push("--Recorder:Profiler=true");
  }

  if (extras.profilesDir) {
    args.push(`--Recorder:ProfilesDirectory=${extras.profilesDir}`);
  }

  if (extras.datasetsDir) {
    args.push(`--Recorder:DatasetsDirectory=${extras.datasetsDir}`);
  }

  if (extras.parentPid && Number.isInteger(extras.parentPid) && extras.parentPid > 0) {
    args.push(`--Recorder:ParentProcessId=${extras.parentPid}`);
  }

  return args;
}

/** The recorder's own default ratings folder, for the Settings page when no datasets folder is passed. */
export function defaultRatingsFolder(env: NodeJS.ProcessEnv): string {
  return join(env["LOCALAPPDATA"] ?? join(env["USERPROFILE"] ?? "~", "AppData", "Local"), "Lookback", "datasets");
}

/** The profiles folder: the one in settings, else the repository's, else none. */
export function profilesFolder(settings: Settings, ctx: Pick<LocateContext, "repoRoot" | "exists">): string | null {
  const dir = settings.profilesDir || (ctx.repoRoot ? join(ctx.repoRoot, "profiles") : "");
  return dir && ctx.exists(dir) ? dir : null;
}

/** The datasets folder: the one in settings, else the repository's, else none. */
export function datasetsFolder(settings: Settings, ctx: Pick<LocateContext, "repoRoot" | "exists">): string | null {
  const dir = settings.datasetsDir || (ctx.repoRoot ? join(ctx.repoRoot, "datasets") : "");
  return dir && ctx.exists(dir) ? dir : null;
}
