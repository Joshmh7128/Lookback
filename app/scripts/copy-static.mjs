// Copies the static page files (HTML, CSS) next to the compiled scripts.
import { cpSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
for (const dir of ["renderer", "viewer"]) {
  const from = join(root, "src", dir);
  const to = join(root, "dist", dir);
  mkdirSync(to, { recursive: true });
  for (const name of readdirSync(from)) {
    if (name.endsWith(".html") || name.endsWith(".css")) cpSync(join(from, name), join(to, name));
  }
}
