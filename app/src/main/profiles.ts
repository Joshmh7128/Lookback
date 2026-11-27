import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";

const ID = /"id"\s*:\s*"([A-Za-z0-9._-]{1,128})"/;

/**
 * Lists profile ids under a profiles folder (`<game>/<id>.json`, JSON with
 * comments). Only the `id` is read; the file is not otherwise interpreted.
 * Returns an empty list when the folder cannot be read.
 */
export function listProfiles(dir: string | null): string[] {
  if (!dir) {
    return [];
  }

  const ids = new Set<string>();
  const walk = (folder: string, depth: number): void => {
    let entries: import("node:fs").Dirent[];
    try {
      entries = readdirSync(folder, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      const full = join(folder, entry.name);
      if (entry.isDirectory() && depth < 3 && !entry.name.startsWith(".")) {
        walk(full, depth + 1);
      } else if (entry.isFile() && entry.name.endsWith(".json")) {
        const id = readId(full);
        if (id) {
          ids.add(id);
        }
      }
    }
  };
  walk(dir, 0);
  return [...ids].sort((a, b) => a.localeCompare(b));
}

function readId(file: string): string | null {
  try {
    const text = readFileSync(file, "utf8").slice(0, 64 * 1024);
    // Drop // comments first so a commented-out id is not picked up.
    const clean = text.replace(/^\s*\/\/.*$/gm, "");
    return ID.exec(clean)?.[1] ?? null;
  } catch {
    return null;
  }
}
