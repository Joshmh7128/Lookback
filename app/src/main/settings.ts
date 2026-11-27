import { mkdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import type { Settings } from "../shared/api";

export const DEFAULT_SETTINGS: Readonly<Settings> = Object.freeze({
  recorderPath: "",
  testbenchPath: "",
  datasetsDir: "",
  profilesDir: "",
  pipeName: "lookback.events",
  webSocketPort: 0,
  profilerPage: true,
  autoStart: true,
});

const PIPE = /^[A-Za-z0-9._-]{1,64}$/;

/**
 * Returns valid settings from anything (a file's JSON, or what a renderer
 * sent). Unknown keys are dropped and each bad value falls back to its
 * default, so a hand-edited file can never stop the app from starting.
 */
export function sanitizeSettings(raw: unknown): Settings {
  const o = typeof raw === "object" && raw !== null ? (raw as Record<string, unknown>) : {};
  const text = (key: keyof Settings): string => {
    const v = o[key];
    return typeof v === "string" && v.length <= 1024 && !v.includes("\0") ? v.trim() : (DEFAULT_SETTINGS[key] as string);
  };
  const flag = (key: keyof Settings): boolean => (typeof o[key] === "boolean" ? (o[key] as boolean) : (DEFAULT_SETTINGS[key] as boolean));

  const pipe = text("pipeName");
  const port = o["webSocketPort"];
  return {
    recorderPath: text("recorderPath"),
    testbenchPath: text("testbenchPath"),
    datasetsDir: text("datasetsDir"),
    profilesDir: text("profilesDir"),
    pipeName: PIPE.test(pipe) ? pipe : DEFAULT_SETTINGS.pipeName,
    webSocketPort: typeof port === "number" && Number.isInteger(port) && (port === 0 || (port >= 1024 && port <= 65535)) ? port : DEFAULT_SETTINGS.webSocketPort,
    profilerPage: flag("profilerPage"),
    autoStart: flag("autoStart"),
  };
}

export function loadSettings(file: string): Settings {
  try {
    return sanitizeSettings(JSON.parse(readFileSync(file, "utf8")));
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

/** Writes through a temporary file and a rename, so a crash never leaves half a file. */
export function saveSettings(file: string, settings: Settings): Settings {
  const clean = sanitizeSettings(settings);
  mkdirSync(dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  writeFileSync(tmp, JSON.stringify(clean, null, 2), "utf8");
  renameSync(tmp, file);
  return clean;
}
