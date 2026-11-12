import type { ErrorCode, Notice } from "../contract.gen";

export const SCENARIO_NAMES = [
  "fresh",
  "eglSignedOut",
  "signedIn",
  "eglDetected",
  "eglMultiple",
  "installed",
  "updateAvailable",
  "downloading",
  "eglBusy",
  "diskFull",
  "authExpired",
  "playing",
] as const;

export type ScenarioName = (typeof SCENARIO_NAMES)[number];

export interface ScenarioInfo {
  name: ScenarioName;
  label: string;
  description: string;
}

export const MOCK_ART = ["default", "none", "busy", "bright", "white"] as const;
export type MockArt = (typeof MOCK_ART)[number];

export interface MockOptions {
  /** Initial scenario (default "fresh"). */
  scenario?: ScenarioName;
  /** Start the running job at this progress, 0..1 (default 0.42 for "downloading"). */
  seek?: number;
  /** Bytes advance this many times faster than the displayed speeds (default 12). ETA counts down in real seconds. */
  timeScale?: number;
  /** The simulation clock does not advance (stable screenshots). Progress events still emit at 10 Hz. */
  freeze?: boolean;
  /** The next mutating call fails with this error. */
  fail?: ErrorCode;
  /** Key art: the default scene, none (the no-art fallback) or a worst-case stand-in (busy / bright / white). */
  art?: MockArt;
  backdrop?: "mica" | "solid";
  /** Multiplier on simulated latencies (default 1; tests use 0). */
  latency?: number;
  seed?: number;
  /** Whether Easy Anti-Cheat is already installed (default true). false adds a prerequisites step to launch. */
  eacInstalled?: boolean;
}

export interface MockController {
  readonly scenarios: readonly ScenarioInfo[];
  getScenario(): ScenarioName;
  /** Reset state to a scenario and emit a fresh snapshot. */
  loadScenario(name: ScenarioName, opts?: Partial<MockOptions>): void;
  /** The next mutating call fails with this error. */
  failNext(code: ErrorCode): void;
  /** The running/paused/waiting job fails with this error (and a `notice` is pushed). */
  failActiveJob(code: ErrorCode): void;
  emitNotice(n: Omit<Notice, "id">): void;
  /** Simulate the game exiting (runState → closing → idle). */
  endGame(): void;
  /** Toggle "Epic Games Launcher is busy with the game": waiting job ⇄ running. */
  setEglBusy(busy: boolean): void;
  /** Fired when the scenario resets or a state snapshot is pushed. */
  subscribe(listener: () => void): () => void;
  /** Stop every timer (tests, HMR). */
  dispose(): void;
}

export const ERROR_CODES = [
  "unknown",
  "invalidRequest",
  "notSignedIn",
  "authExpired",
  "authFailed",
  "authCancelled",
  "insufficientSpace",
  "network",
  "cdnUnavailable",
  "filesLocked",
  "permissionDenied",
  "eglBusy",
  "gameRunning",
  "notInstalled",
  "alreadyInstalled",
  "invalidFolder",
  "corrupted",
  "busy",
  "legendaryMissing",
  "legendaryFailed",
  "eacSetupFailed",
  "launchFailed",
  "cancelled",
  "timeout",
] as const satisfies readonly ErrorCode[];

const finite = (v: string | null): number | undefined => {
  if (v === null || v.trim() === "") return undefined;
  const n = Number(v);
  return Number.isFinite(n) ? n : undefined;
};
const truthy = (v: string | null): boolean | undefined =>
  v === null ? undefined : v === "" || v === "1" || v.toLowerCase() === "true";

/** `?scenario= &seek= &timescale= &freeze=1 &fail= &art=none|busy|bright|white &backdrop= &latency= &seed= &eac=0` */
export function parseMockOptions(search: string): MockOptions {
  const q = new URLSearchParams(search);
  const o: MockOptions = {};

  const scenario = q.get("scenario");
  if (scenario && (SCENARIO_NAMES as readonly string[]).includes(scenario)) o.scenario = scenario as ScenarioName;

  const seek = finite(q.get("seek"));
  if (seek !== undefined) o.seek = Math.min(1, Math.max(0, seek));

  const timeScale = finite(q.get("timescale") ?? q.get("timeScale"));
  if (timeScale !== undefined && timeScale > 0) o.timeScale = timeScale;

  const freeze = truthy(q.get("freeze"));
  if (freeze !== undefined) o.freeze = freeze;

  const failCode = q.get("fail");
  if (failCode && (ERROR_CODES as readonly string[]).includes(failCode)) o.fail = failCode as ErrorCode;

  const art = q.get("art");
  if (art && (MOCK_ART as readonly string[]).includes(art)) o.art = art as MockArt;

  const backdrop = q.get("backdrop");
  if (backdrop === "mica" || backdrop === "solid") o.backdrop = backdrop;

  const latency = finite(q.get("latency"));
  if (latency !== undefined && latency >= 0) o.latency = latency;

  const seed = finite(q.get("seed"));
  if (seed !== undefined) o.seed = Math.trunc(seed);

  const eac = truthy(q.get("eac"));
  if (eac !== undefined) o.eacInstalled = eac;

  return o;
}
