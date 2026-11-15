import type { ErrorCode, Notice } from "../contract.gen";

/**
 * Mock scenarios (docs: frontend/README.md). `frozen*` scenarios hold one job phase still for screenshots; they
 * imply `freeze`. The first group are the "whole app" states, the rest are focused ones.
 */
export const SCENARIO_NAMES = [
  "returning",
  "firstRun",
  "firstRunSignedIn",
  "emptyLibrary",
  "offline",
  "syncError",
  "perf600",
  "queue",
  "downloading",
  "playing",
  "updates",
  "authExpired",
  "diskFull",
  "eglBusy",
  "errors",
  "frozenPreparing",
  "frozenVerifying",
  "frozenRepairing",
  "frozenDownloading",
  "frozenInstalling",
  "frozenFinalizing",
  "frozenMoving",
  "frozenRemoving",
  "frozenImporting",
] as const;

export type ScenarioName = (typeof SCENARIO_NAMES)[number];

export interface ScenarioInfo {
  name: ScenarioName;
  label: string;
  description: string;
}

/** `none` strips all art; `noWide` keeps the tall cover but no wide art (the download card blurs the cover); `busy` / `bright` / `white` replace it with the worst key art for text legibility. */
export const MOCK_ART = ["default", "none", "noWide", "busy", "bright", "white"] as const;
export type MockArt = (typeof MOCK_ART)[number];

export interface MockOptions {
  /** Initial scenario (default "returning"). */
  scenario?: ScenarioName;
  /** Start the running job at this progress of its main phase, 0..1 (scenarios with a live job have their own default). */
  seek?: number;
  /** Bytes advance this many times faster than the displayed speeds (default 12). ETA counts down in real seconds. */
  timeScale?: number;
  /** The simulation clock does not advance (stable screenshots). Progress events still emit at 10 Hz. */
  freeze?: boolean;
  /** The next mutating call fails with this error. */
  fail?: ErrorCode;
  /** `none` strips all art (the no-art fallback); `busy` / `bright` / `white` are worst-case key art. */
  art?: MockArt;
  backdrop?: "mica" | "solid";
  /** Multiplier on simulated latencies (default 1; tests use 0). */
  latency?: number;
  seed?: number;
  /** Library size override (base games; DLC come on top). Defaults: 300, perf600 = 600. */
  games?: number;
  /** Whether Easy Anti-Cheat is already installed for Fortnite (default true). false adds a prerequisites step to launch. */
  eacInstalled?: boolean;
  /** Force what holds a download back, as if the drive, the processor or the download server were slow (screenshots, dev). Default: none. */
  bottleneck?: "disk" | "cpu" | "cdn";
}

export interface MockController {
  readonly scenarios: readonly ScenarioInfo[];
  getScenario(): ScenarioName;
  /** Reset state to a scenario and emit a fresh snapshot + library reset. */
  loadScenario(name: ScenarioName, opts?: Partial<MockOptions>): void;
  /** The next mutating call fails with this error. */
  failNext(code: ErrorCode): void;
  /** The running/paused/waiting job fails with this error (and a `notice` is pushed). */
  failActiveJob(code: ErrorCode): void;
  emitNotice(n: Omit<Notice, "id">): void;
  /** Simulate a game exiting (runState → closing → idle). Without an argument, the first running one. */
  endGame(appName?: string): void;
  /** Toggle "Epic Games Launcher is busy with a game": waiting job ⇄ running. */
  setEglBusy(busy: boolean, appName?: string): void;
  /** Flip connectivity (the offline scenario starts offline). */
  setConnectivity(c: "online" | "offline"): void;
  /** Set a drive's free space ("D", bytes): freeing disk space outside Glider. The next snapshot carries it. */
  setFreeSpace(letter: string, freeBytes: number): void;
  /** Swallow the next `library` delta on the wire (its revision is still consumed), so the next one shows a gap. */
  dropNextLibraryDelta(): void;
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
  "offline",
  "cdnUnavailable",
  "filesLocked",
  "permissionDenied",
  "eglBusy",
  "gameRunning",
  "gameBusy",
  "notFound",
  "notOwned",
  "notInstalled",
  "alreadyInstalled",
  "invalidFolder",
  "corrupted",
  "busy",
  "legendaryMissing",
  "legendaryFailed",
  "legendaryLocked",
  "eacSetupFailed",
  "prerequisiteFailed",
  "launchFailed",
  "thirdPartyRequired",
  "activationRequired",
  "saveConflict",
  "saveSyncFailed",
  "overlayFailed",
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

/** `?scenario= &seek= &timescale= &freeze=1 &fail= &art=none &backdrop= &latency= &seed= &games= &eac=0` */
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

  const games = finite(q.get("games"));
  if (games !== undefined && games >= 0) o.games = Math.min(5000, Math.trunc(games));

  const eac = truthy(q.get("eac"));
  if (eac !== undefined) o.eacInstalled = eac;

  const bottleneck = q.get("bottleneck");
  if (bottleneck === "disk" || bottleneck === "cpu" || bottleneck === "cdn") o.bottleneck = bottleneck;

  return o;
}
