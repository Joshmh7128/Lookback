import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { request } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { datasetsFolder, locateTool, profilesFolder, RECORDER, recorderArgs } from "../main/locate";
import { listProfiles } from "../main/profiles";
import { DEFAULT_SETTINGS, loadSettings, sanitizeSettings, saveSettings } from "../main/settings";
import { resolveStatic } from "../main/static-files";
import { VIEWER_CSP, ViewerServer } from "../main/viewer-server";

const dir = mkdtempSync(join(tmpdir(), "lookback-app-test-"));
after(() => rmSync(dir, { recursive: true, force: true }));

test("settings: bad values fall back to defaults and unknown keys are dropped", () => {
  const s = sanitizeSettings({ pipeName: "bad pipe", webSocketPort: 80, autoStart: "yes", profilesDir: "  D:\\p  ", extra: 1 });
  assert.equal(s.pipeName, DEFAULT_SETTINGS.pipeName);
  assert.equal(s.webSocketPort, 0);
  assert.equal(s.autoStart, DEFAULT_SETTINGS.autoStart);
  assert.equal(s.profilesDir, "D:\\p");
  assert.ok(!("extra" in s));
  assert.deepEqual(sanitizeSettings(null), { ...DEFAULT_SETTINGS });
});

test("settings: save and load round-trip; a broken file loads defaults", () => {
  const file = join(dir, "settings", "settings.json");
  const saved = saveSettings(file, { ...DEFAULT_SETTINGS, webSocketPort: 5050, profilerPage: false });
  assert.deepEqual(loadSettings(file), saved);
  writeFileSync(file, "{ nope");
  assert.deepEqual(loadSettings(file), { ...DEFAULT_SETTINGS });
});

test("static paths cannot escape the root", () => {
  const root = join(dir, "static");
  assert.equal(resolveStatic(root, "/index.html"), join(root, "index.html"));
  assert.equal(resolveStatic(root, "/"), join(root, "index.html"));
  for (const bad of ["/../secret.html", "/%2e%2e/secret.html", "/a\\..\\b.html", "/.hidden.html", "/x.exe", "/%00.html", "/%E0%A4%A.html"]) {
    assert.equal(resolveStatic(root, bad), null, bad);
  }
});

test("the recorder is found Release first, apphost before dll, and explicit paths win", () => {
  const repo = join(dir, "repo");
  const rel = join(repo, "src", "Lookback.Recorder", "bin", "Release", "net10.0");
  const dbg = join(repo, "src", "Lookback.Recorder", "bin", "Debug", "net10.0");
  const have = new Set([join(rel, "Lookback.Recorder.dll"), join(dbg, "Lookback.Recorder.exe")]);
  const ctx = { repoRoot: repo, resourcesDir: null, platform: "win32" as const, env: {}, exists: (p: string) => have.has(p) };
  const found = locateTool(RECORDER, "", ctx, ["--x"]);
  assert.deepEqual(found, { file: "dotnet", args: [join(rel, "Lookback.Recorder.dll"), "--x"], cwd: repo, display: "dotnet Lookback.Recorder.dll" });
  have.add(join(rel, "Lookback.Recorder.exe"));
  assert.equal(locateTool(RECORDER, "", ctx, [])?.file, join(rel, "Lookback.Recorder.exe"));
  assert.equal(locateTool(RECORDER, "C:\\missing.exe", ctx, []), null);
  assert.equal(locateTool(RECORDER, "", { ...ctx, env: { LOOKBACK_RECORDER: join(dbg, "Lookback.Recorder.exe") } }, [])?.file, join(dbg, "Lookback.Recorder.exe"));
});

test("recorder arguments carry settings and the profiles folder, never a token", () => {
  assert.deepEqual(recorderArgs({ ...DEFAULT_SETTINGS }, "D:\\repo\\profiles"), [
    "--Recorder:PipeName=lookback.events",
    "--Recorder:WebSocketPort=0",
    "--Recorder:Profiler=true",
    "--Recorder:ProfilesDirectory=D:\\repo\\profiles",
  ]);
  assert.deepEqual(recorderArgs({ ...DEFAULT_SETTINGS, profilerPage: false }), ["--Recorder:PipeName=lookback.events", "--Recorder:WebSocketPort=0"]);
  const ctx = { repoRoot: "R", exists: (p: string) => p === join("R", "profiles") };
  assert.equal(profilesFolder({ ...DEFAULT_SETTINGS }, ctx), join("R", "profiles"));
  assert.equal(profilesFolder({ ...DEFAULT_SETTINGS, profilesDir: "elsewhere" }, ctx), null);
  assert.equal(datasetsFolder({ ...DEFAULT_SETTINGS }, ctx), null);
});

test("profile ids come from each file's id, skipping commented-out ones", () => {
  const p = join(dir, "profiles");
  mkdirSync(join(p, "game"), { recursive: true });
  writeFileSync(join(p, "game", "game.json"), '{\n  // "id": "commented",\n  "id": "game",\n  "level": "game"\n}');
  writeFileSync(join(p, "game", "game.mode.json"), '{ "id": "game.mode" }');
  writeFileSync(join(p, "game", "broken.json"), "{");
  writeFileSync(join(p, "readme.txt"), '"id": "nope"');
  assert.deepEqual(listProfiles(p), ["game", "game.mode"]);
  assert.deepEqual(listProfiles(join(dir, "missing")), []);
  assert.deepEqual(listProfiles(null), []);
});

test("the repository's real profiles are listed", () => {
  const ids = listProfiles(join(__dirname, "..", "..", "..", "profiles"));
  assert.ok(ids.includes("cs2") && ids.includes("overwatch") && ids.includes("generic"), ids.join(","));
});

function get(port: number, path: string, host: string): Promise<{ status: number; headers: Record<string, unknown>; body: string }> {
  return new Promise((resolve, reject) => {
    const req = request({ host: "127.0.0.1", port, path, headers: { Host: host } }, (res) => {
      let body = "";
      res.setEncoding("utf8").on("data", (c: string) => (body += c));
      res.on("end", () => resolve({ status: res.statusCode ?? 0, headers: res.headers, body }));
    });
    req.on("error", reject);
    req.end();
  });
}

test("the viewer server serves only its files, only to Host localhost:PORT, with the viewer CSP", async () => {
  const root = join(dir, "viewer");
  mkdirSync(root, { recursive: true });
  writeFileSync(join(root, "viewer.html"), "<p>viewer</p>");
  writeFileSync(join(dir, "outside.html"), "secret");
  const server = new ViewerServer(root);
  await server.start();
  try {
    const port = Number(new URL(server.origin).port);
    assert.equal(server.origin, `http://localhost:${port}`);
    const ok = await get(port, "/viewer.html", `localhost:${port}`);
    assert.equal(ok.status, 200);
    assert.equal(ok.body, "<p>viewer</p>");
    assert.equal(ok.headers["content-security-policy"], VIEWER_CSP);
    assert.equal(ok.headers["cache-control"], "no-store");
    assert.equal((await get(port, "/viewer.html", `127.0.0.1:${port}`)).status, 421);
    assert.equal((await get(port, "/viewer.html", `evil.example:${port}`)).status, 421);
    assert.equal((await get(port, "/../outside.html", `localhost:${port}`)).status, 404);
    assert.equal((await get(port, "/%2e%2e/outside.html", `localhost:${port}`)).status, 404);
  } finally {
    await server.stop();
  }

  assert.equal(readFileSync(join(dir, "outside.html"), "utf8"), "secret");
});

test("the viewer CSP allows only the official players' hosts", () => {
  const script = /script-src ([^;]+)/.exec(VIEWER_CSP)?.[1] ?? "";
  assert.deepEqual(script.split(" ").sort(), ["'self'", "https://player.twitch.tv", "https://s.ytimg.com", "https://www.youtube.com"]);
  assert.match(VIEWER_CSP, /connect-src 'none'/);
});
