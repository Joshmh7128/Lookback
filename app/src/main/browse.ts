import { spawn } from "node:child_process";
import type { BrowseResult } from "../shared/api";
import type { Command } from "./locate";

/**
 * Runs `lookback-testbench browse` to (re)generate the dataset browser page.
 * The page holds profile and manifest data only, never media or session
 * data, so writing it is outside what RetentionRules governs.
 */
export function runBrowse(command: Command, timeoutMs = 120_000): Promise<BrowseResult> {
  return new Promise((resolve) => {
    const lines: string[] = [];
    const keep = (chunk: string): void => {
      for (const line of chunk.split(/\r?\n/)) {
        if (line.trim()) lines.push(line);
      }

      if (lines.length > 40) lines.splice(0, lines.length - 40);
    };

    let done = false;
    const finish = (result: BrowseResult): void => {
      if (!done) {
        done = true;
        clearTimeout(timer);
        resolve(result);
      }
    };

    const child = spawn(command.file, command.args, { cwd: command.cwd, stdio: ["ignore", "pipe", "pipe"], windowsHide: true, env: { ...process.env, DOTNET_NOLOGO: "1" } });
    child.stdout.setEncoding("utf8").on("data", keep);
    child.stderr.setEncoding("utf8").on("data", keep);
    const timer = setTimeout(() => {
      child.kill();
      finish({ ok: false, message: `${command.display} took longer than ${timeoutMs / 1000} s` });
    }, timeoutMs);
    child.once("error", (err) => finish({ ok: false, message: `could not run ${command.display}: ${err.message}` }));
    child.once("exit", (code) => finish({ ok: code === 0, message: lines.slice(-8).join("\n") || `exited with code ${code}` }));
  });
}

export function browseArgs(outFile: string, datasetsDir: string): string[] {
  const args = ["browse", "--out", outFile];
  if (datasetsDir) {
    args.push("--datasets", datasetsDir);
  }

  return args;
}
