import assert from "node:assert/strict";
import { cpSync, existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { after, test } from "node:test";
import { locateTool, TESTBENCH, type Command } from "../main/locate";
import { isEmptyEdits, isProfileId, profileSaveArgs, profileViewArgs, runProfileTool, sanitizeEdits } from "../main/profile-editor";

const dir = mkdtempSync(join(tmpdir(), "lookback-profile-editor-"));
after(() => rmSync(dir, { recursive: true, force: true }));

function node(script: string): Command {
  return { file: process.execPath, args: ["-e", script], cwd: process.cwd(), display: "fake-testbench" };
}

test("profile ids and tool arguments", () => {
  assert.ok(isProfileId("roblox.99-nights-in-the-forest.day"));
  for (const bad of ["", "Valorant", "../x", "a..b", "a b", 7, null, "x".repeat(200)]) assert.equal(isProfileId(bad), false, String(bad));
  assert.deepEqual(profileViewArgs("D:\\p", null), ["profile-view", "--profiles", "D:\\p"]);
  assert.deepEqual(profileViewArgs("D:\\p", "cs2"), ["profile-view", "--profiles", "D:\\p", "--id", "cs2"]);
  assert.deepEqual(profileSaveArgs("D:\\p", "cs2"), ["profile-save", "--profiles", "D:\\p", "--id", "cs2", "--edits", "-"]);
});

test("edits from the renderer are cleaned: unknown fields dropped, nulls kept as resets", () => {
  const e = sanitizeEdits({
    events: [{ name: "elimination", weight: 1.5, thresholds: { "0": 0.06 }, when: [] }],
    composites: [{ name: "multikill", weight: 3 }],
    clipper: { high: 1.8, low: null },
    components: { "audio.rms_db": 0.5, "scene.cut": null },
    capturePolicy: { allowPresentHook: true },
  });
  assert.deepEqual(e, {
    events: [{ name: "elimination", weight: 1.5, thresholds: { "0": 0.06 } }],
    composites: [{ name: "multikill", weight: 3 }],
    clipper: { high: 1.8, low: null },
    components: { "audio.rms_db": 0.5, "scene.cut": null },
  });
  assert.ok(e && !isEmptyEdits(e));
  assert.ok(isEmptyEdits(sanitizeEdits({})!));
});

test("malformed edits are refused before they reach the tool", () => {
  for (const bad of [
    null,
    [],
    "x",
    { events: {} },
    { events: [{ weight: 1 }] },
    { events: [{ name: "a b", weight: 1 }] },
    { events: [{ name: "e", weight: "1" }] },
    { events: [{ name: "e", weight: Number.NaN }] },
    { events: [{ name: "e", thresholds: { first: 1 } }] },
    { composites: [{ name: "m" }] },
    { clipper: { allowPresentHook: 1 } },
    { clipper: { high: Number.POSITIVE_INFINITY } },
    { components: { "bad signal": 1 } },
    { components: { "a.b": "1" } },
    { components: Object.fromEntries(Array.from({ length: 600 }, (_, i) => [`s.${i}`, 1])) },
  ]) {
    assert.equal(sanitizeEdits(bad), null, JSON.stringify(bad)?.slice(0, 80));
  }
});

test("the tool's JSON result is returned; stdin carries the edits", async () => {
  const echo = node(`let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{console.log("noise");console.log(JSON.stringify({ok:true,files:[JSON.parse(s).x]}))})`);
  assert.deepEqual(await runProfileTool(echo, JSON.stringify({ x: "cs2/cs2.json" })), { ok: true, files: ["cs2/cs2.json"] });

  const refused = node(`console.log(JSON.stringify({ok:false,message:"low must be below high"}));process.exit(1)`);
  assert.deepEqual(await runProfileTool(refused, null), { ok: false, message: "low must be below high" });
});

test("a crash, garbage output or a hang becomes a refusal with a reason", async () => {
  const crash = await runProfileTool(node(`console.error("Unhandled exception. boom");process.exit(3)`), null);
  assert.equal(crash.ok, false);
  assert.match(crash.message ?? "", /boom/);

  const garbage = await runProfileTool(node(`console.log("not json")`), null);
  assert.equal(garbage.ok, false);
  assert.match(garbage.message ?? "", /printed no result/);

  const hang = await runProfileTool(node(`setTimeout(()=>{},10000)`), null, 300);
  assert.deepEqual(hang, { ok: false, message: "fake-testbench took longer than 0.3 s" });

  const missing = await runProfileTool({ file: join(dir, "no-such-tool.exe"), args: [], cwd: dir, display: "no-such-tool" }, null);
  assert.equal(missing.ok, false);
  assert.match(missing.message ?? "", /could not run no-such-tool/);
});

// Against the real lookback-testbench from this checkout, on a copy of one game's profiles.
// Skipped when it has not been built (`dotnet build Lookback.slnx -c Release`).
const repoRoot = join(__dirname, "..", "..", "..");
const ctx = { repoRoot, resourcesDir: null, platform: process.platform, env: process.env, exists: existsSync };
const built = locateTool(TESTBENCH, "", ctx, []) !== null;

test("the real testbench shows a profile with sources and saves a validated edit", { skip: built ? false : "lookback-testbench is not built", timeout: 120_000 }, async () => {
  const profiles = join(dir, "profiles");
  cpSync(join(repoRoot, "profiles", "cs2"), join(profiles, "cs2"), { recursive: true });
  const run = (args: string[], stdin: string | null) => runProfileTool(locateTool(TESTBENCH, "", ctx, args)!, stdin);

  const view = await run(profileViewArgs(profiles, "cs2.competitive"), null);
  assert.equal(view.ok, true, view.message);
  assert.equal(view.profiles?.[0]?.id, "cs2");
  assert.equal(view.view?.file, "cs2/cs2.competitive.json");
  assert.ok(view.view?.clipper.numbers.every((n) => n.source === "defaults"));

  const file = join(profiles, "cs2", "cs2.competitive.json");
  const before = readFileSync(file, "utf8");
  const refused = await run(profileSaveArgs(profiles, "cs2.competitive"), JSON.stringify(sanitizeEdits({ clipper: { low: 3 } })));
  assert.equal(refused.ok, false);
  assert.match(refused.message ?? "", /must be below high/);
  assert.equal(readFileSync(file, "utf8"), before);

  const saved = await run(profileSaveArgs(profiles, "cs2.competitive"), JSON.stringify(sanitizeEdits({ clipper: { high: 1.9 }, components: { "scene.cut": 0 } })));
  assert.equal(saved.ok, true, saved.message);
  assert.deepEqual(saved.files, ["cs2/cs2.competitive.json"]);
  assert.equal(saved.view?.clipper.numbers.find((n) => n.field === "high")?.source, "cs2.competitive");
  assert.match(readFileSync(file, "utf8"), /"clipper": \{\n {4}"high": 1\.9,/);

  const reset = await run(profileSaveArgs(profiles, "cs2.competitive"), JSON.stringify(sanitizeEdits({ clipper: { high: null }, components: { "scene.cut": null } })));
  assert.equal(reset.ok, true, reset.message);
  assert.equal(readFileSync(file, "utf8"), before);
});
