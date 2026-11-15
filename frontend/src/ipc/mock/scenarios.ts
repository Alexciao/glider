import type {
  AuthState,
  Collection,
  Connectivity,
  EglInstall,
  ErrorCode,
  GliderError,
  JobKind,
  JobOutcome,
  JobPhase,
  JobStatus,
  OnboardingState,
  OverlayState,
  RunningGame,
  Settings,
} from "../contract.gen";
import type { WorstArt } from "./art";
import { generateCatalog, type MockGame } from "./catalog";
import { ACCOUNT, DEFAULT_SETTINGS, FORTNITE, GiB, HOUR, MINUTE, OVERLAY_LATEST, OVERLAY_VERSION, iso } from "./constants";
import { defaultDrives, driveLetter, type MockDrive } from "./disk";
import { Errors } from "./errors";
import { mulberry32, shuffled } from "./rng";
import type { ScenarioInfo, ScenarioName } from "./options";

/** A job to hydrate when a scenario loads: running/paused/waiting/queued ones go to the queue, finished ones to history. */
export interface SeedJob {
  kind: JobKind;
  appName: string;
  status: JobStatus;
  /** Fraction of the phase (or of the main byte phase when `phase` is omitted) a running/paused/waiting job is at. */
  seek?: number;
  phase?: JobPhase;
  error?: ErrorCode;
  /** Finished jobs: how long ago, and how long it took. */
  finishedAgoMs?: number;
  durationS?: number;
  blockedReason?: string;
  targetPath?: string;
  outcome?: JobOutcome;
  uninstallSeconds?: number;
  importVerify?: boolean;
  /** Pre-flight: the game this job targets will be flagged as "EGL busy with it". */
  eglBusy?: boolean;
}

export interface EglWorld {
  launcherInstalled: boolean;
  launcherRunning: boolean;
  /** The scan has run and its results are visible (otherwise they appear after sign-in). */
  revealed: boolean;
  installs: EglInstall[];
  busyApps: string[];
  lastScan: string | null;
}

export interface World {
  scenario: ScenarioName;
  now: number;
  auth: AuthState;
  connectivity: Connectivity;
  games: MockGame[];
  /** The library as Epic would sync it after sign-in (first run). */
  deferred: MockGame[];
  syncError: GliderError | null;
  lastSyncedAt: string | null;
  jobs: SeedJob[];
  sessions: RunningGame[];
  settings: Settings;
  drives: MockDrive[];
  egl: EglWorld;
  overlay: OverlayState;
  collections: Collection[];
  onboarding: OnboardingState;
  freeze: boolean;
}

export interface WorldConfig {
  seed: number;
  now: number;
  games?: number;
  art: boolean;
  artVariant?: WorstArt;
  eacInstalled: boolean;
}

export const SCENARIOS: readonly ScenarioInfo[] = [
  { name: "returning", label: "Returning user", description: "Signed in, ~300 games, installs on four drives, history, nothing running" },
  { name: "firstRun", label: "First run", description: "Signed out, onboarding at Welcome; signing in syncs the library and scans for EGL installs" },
  { name: "firstRunSignedIn", label: "First run: scan step", description: "Signed in mid-onboarding with a full EGL scan result" },
  { name: "emptyLibrary", label: "Empty library", description: "Signed in, onboarding done, no games at all" },
  { name: "offline", label: "Offline", description: "No connection: cached library, installs refused, offline-capable games still play" },
  { name: "syncError", label: "Sync error", description: "The last library sync failed; the cached library stays usable" },
  { name: "perf600", label: "600 games", description: "Performance library: 600 base games plus DLC" },
  { name: "queue", label: "Busy queue", description: "One running download, a paused job, queued jobs, a waiting job and history" },
  { name: "downloading", label: "Downloading", description: "A single install at 42%" },
  { name: "playing", label: "Playing", description: "Fortnite has been running for 38 minutes" },
  { name: "updates", label: "Many updates", description: "Two dozen installed games have updates pending" },
  { name: "authExpired", label: "Session expired", description: "Epic session expired; sign in again" },
  { name: "diskFull", label: "Disk almost full", description: "C: and D: are nearly full; installs and updates are refused" },
  { name: "eglBusy", label: "EGL busy", description: "Epic Games Launcher is updating a game; its job waits" },
  { name: "errors", label: "Error states", description: "Failed jobs with every kind of error, a sync error and broken installs" },
  { name: "frozenPreparing", label: "Frozen: preparing", description: "An install held in its preparing step" },
  { name: "frozenVerifying", label: "Frozen: verifying", description: "A verify reading files, bad files showing up" },
  { name: "frozenRepairing", label: "Frozen: repairing", description: "Verify found bad files; re-downloading just those" },
  { name: "frozenDownloading", label: "Frozen: downloading", description: "An install downloading (network + disk figures)" },
  { name: "frozenInstalling", label: "Frozen: installing", description: "Network done; disk-only install tail" },
  { name: "frozenFinalizing", label: "Frozen: finalizing", description: "An update in its finalizing step" },
  { name: "frozenMoving", label: "Frozen: moving", description: "An install being moved to another drive" },
  { name: "frozenRemoving", label: "Frozen: removing", description: "Files being deleted by an uninstall" },
  { name: "frozenImporting", label: "Frozen: importing", description: "An Epic Games Launcher install being linked" },
];

const FROZEN: Partial<Record<ScenarioName, { kind: JobKind; phase: JobPhase; seek: number; pick: "notInstalled" | "installedUpdate" | "broken" | "installed" }>> = {
  frozenPreparing: { kind: "install", phase: "preparing", seek: 0.55, pick: "notInstalled" },
  frozenVerifying: { kind: "verify", phase: "verifying", seek: 0.46, pick: "broken" },
  frozenRepairing: { kind: "verify", phase: "repairing", seek: 0.5, pick: "broken" },
  frozenDownloading: { kind: "install", phase: "downloading", seek: 0.42, pick: "notInstalled" },
  frozenInstalling: { kind: "install", phase: "installing", seek: 0.4, pick: "notInstalled" },
  frozenFinalizing: { kind: "update", phase: "finalizing", seek: 0.5, pick: "installedUpdate" },
  frozenMoving: { kind: "move", phase: "moving", seek: 0.45, pick: "installed" },
  frozenRemoving: { kind: "uninstall", phase: "removing", seek: 0.5, pick: "installed" },
  frozenImporting: { kind: "import", phase: "importing", seek: 0.5, pick: "notInstalled" },
};

const isBase = (g: MockGame): boolean => g.summary.kind === "game";
const installable = (g: MockGame): boolean => isBase(g) && g.summary.installStatus === "notInstalled" && g.summary.thirdParty === "none";
const installedOk = (g: MockGame): boolean => isBase(g) && g.summary.installStatus === "installed" && g.summary.appName !== FORTNITE;

function buildEgl(games: MockGame[], seed: number, now: number, revealed: boolean): EglWorld {
  const rand = mulberry32(seed ^ 0xe91);
  const installs: EglInstall[] = [];
  const toInstall = games.filter(installable);
  const pool = shuffled(rand, toInstall);
  const drives = ["D:\\Epic Games", "E:\\Epic Games", "F:\\Epic Games", "C:\\Program Files\\Epic Games"];
  const art = (g: MockGame): string | null => g.summary.art.tallThumb ?? g.summary.art.tall;

  const fortnite = games.find((g) => g.summary.appName === FORTNITE);
  if (fortnite && fortnite.summary.installPath) {
    installs.push({
      id: "glider:Fortnite",
      source: "gliderV1",
      appName: FORTNITE,
      title: "Fortnite",
      installPath: fortnite.summary.installPath,
      drive: "D:\\",
      version: fortnite.summary.installedVersion,
      sizeBytes: Math.round(94.8 * GiB),
      status: "imported",
      drift: "eglNewer",
      reason: null,
      artUrl: art(fortnite),
    });
  }
  for (const g of games.filter((x) => installedOk(x) && x.summary.sharedWithEgl).slice(0, 2)) {
    installs.push({
      id: `egl:${g.summary.appName}`,
      source: "epicGamesLauncher",
      appName: g.summary.appName,
      title: g.summary.title,
      installPath: g.summary.installPath ?? "",
      drive: `${(g.summary.installPath ?? "C:")[0]}:\\`,
      version: g.summary.installedVersion,
      sizeBytes: g.summary.installedSizeBytes,
      status: "imported",
      drift: "none",
      reason: null,
      artUrl: art(g),
    });
  }
  const importable = pool.slice(0, 8);
  importable.forEach((g, i) => {
    const base = drives[i % drives.length]!;
    installs.push({
      id: `egl:${g.summary.appName}`,
      source: "epicGamesLauncher",
      appName: g.summary.appName,
      title: g.summary.title,
      installPath: `${base}\\${g.folderName}`,
      drive: `${base[0]}:\\`,
      version: i % 4 === 3 ? "0.9.0" : g.summary.latestVersion,
      sizeBytes: Math.round((g.summary.installSizeBytes ?? 4 * GiB) * 0.98),
      status: "importable",
      drift: i % 4 === 3 ? "eglNewer" : "none",
      reason: null,
      artUrl: art(g),
    });
  });
  pool.slice(8, 10).forEach((g) => {
    installs.push({
      id: `egl:${g.summary.appName}`,
      source: "epicGamesLauncher",
      appName: g.summary.appName,
      title: g.summary.title,
      installPath: `E:\\Old Games\\${g.folderName}`,
      drive: "E:\\",
      version: g.summary.latestVersion,
      sizeBytes: null,
      status: "stale",
      drift: "unknown",
      reason: `${g.summary.title} isn't at this location any more. Epic Games Launcher still lists it, but the folder is gone.`,
      artUrl: art(g),
    });
  });
  const busy = pool[10];
  if (busy) {
    installs.push({
      id: `egl:${busy.summary.appName}`,
      source: "epicGamesLauncher",
      appName: busy.summary.appName,
      title: busy.summary.title,
      installPath: `D:\\Epic Games\\${busy.folderName}`,
      drive: "D:\\",
      version: busy.summary.latestVersion,
      sizeBytes: Math.round((busy.summary.installSizeBytes ?? 4 * GiB) * 0.6),
      status: "busy",
      drift: "none",
      reason: "Epic Games Launcher is installing or updating this right now. Glider won't touch it.",
      artUrl: art(busy),
    });
  }
  installs.push({
    id: "egl:Mystery",
    source: "epicGamesLauncher",
    appName: "MysteryTitle",
    title: "Starfall Courier",
    installPath: "F:\\Epic Games\\StarfallCourier",
    drive: "F:\\",
    version: "1.0.0",
    sizeBytes: Math.round(7.2 * GiB),
    status: "notOwned",
    drift: "unknown",
    reason: "This game isn't in the signed-in Epic account's library.",
    artUrl: null,
  });
  return { launcherInstalled: true, launcherRunning: true, revealed, installs, busyApps: [], lastScan: revealed ? iso(now - 14 * MINUTE) : null };
}

function buildCollections(games: MockGame[], seed: number): Collection[] {
  const rand = mulberry32(seed ^ 0xc011);
  const base = games.filter(isBase);
  const take = (n: number): string[] => shuffled(rand, base).slice(0, n).map((g) => g.summary.appName);
  if (base.length < 12) return [];
  return [
    { id: "col-coop", name: "Co-op nights", appNames: take(6) },
    { id: "col-backlog", name: "Backlog", appNames: take(14) },
    { id: "col-cozy", name: "Cozy", appNames: take(9) },
  ];
}

function history(games: MockGame[], seed: number): SeedJob[] {
  const rand = mulberry32(seed ^ 0x4157);
  const inst = games.filter(installedOk);
  const out: SeedJob[] = [];
  const g = (i: number): string => (inst[i % Math.max(1, inst.length)]?.summary.appName ?? FORTNITE);
  const kinds: [JobKind, JobStatus][] = [
    ["update", "completed"],
    ["install", "completed"],
    ["verify", "completed"],
    ["update", "completed"],
    ["import", "completed"],
    ["update", "failed"],
    ["install", "cancelled"],
    ["move", "completed"],
    ["verify", "completed"],
    ["update", "completed"],
  ];
  kinds.forEach(([kind, status], i) => {
    out.push({
      kind,
      appName: i === 0 ? FORTNITE : g(i * 3),
      status,
      finishedAgoMs: (i + 1) * (2 + rand() * 20) * HOUR,
      durationS: 60 + Math.round(rand() * 2400),
      error: status === "failed" ? "filesLocked" : undefined,
    });
  });
  return out;
}

/** The game as Glider sees it before anything was imported: not installed (Epic's install, if any, is still in EGL). */
function notInstalledYet(g: MockGame): MockGame {
  const s = g.summary;
  if (s.installStatus !== "installed" && s.installStatus !== "broken") return g;
  return {
    ...g,
    badFiles: 0,
    badBytes: 0,
    installedAt: null,
    executable: null,
    eglManifestPath: null,
    summary: {
      ...s,
      installStatus: "notInstalled",
      installedVersion: null,
      installPath: null,
      installedSizeBytes: null,
      downloadSizeBytes: g.fullDownloadBytes,
      updateAvailable: false,
      needsVerification: false,
      sharedWithEgl: false,
      saveStatus: g.saves ? "unknown" : s.saveStatus,
    },
    saves: g.saves && { ...g.saves, status: "unknown", savePath: null },
    conflict: null,
  };
}

export function buildWorld(name: ScenarioName, cfg: WorldConfig): World {
  const { now } = cfg;
  const count = cfg.games ?? (name === "perf600" ? 600 : name === "emptyLibrary" ? 0 : 300);
  const all = count === 0 ? [] : generateCatalog({ seed: cfg.seed, count, now, art: cfg.art, artVariant: cfg.artVariant });
  const firstRun = name === "firstRun";
  const freshStart = firstRun || name === "firstRunSignedIn";
  const games = freshStart ? all.map((g) => (g.summary.appName === FORTNITE ? g : notInstalledYet(g))) : [...all];

  const libraryFolders = ["D:\\Games", "E:\\Epic Games", "F:\\Library", "C:\\Program Files\\Epic Games"];
  const settings: Settings = { ...DEFAULT_SETTINGS, libraryFolders, defaultInstallBase: "D:\\Games" };
  const eglRevealed = !firstRun;
  const egl = buildEgl(games, cfg.seed, now, eglRevealed);
  if (count === 0) egl.installs = [];

  const world: World = {
    scenario: name,
    now,
    auth: { status: "signedIn", account: ACCOUNT },
    connectivity: "online",
    games: firstRun ? [] : games,
    deferred: firstRun ? games : [],
    syncError: null,
    lastSyncedAt: firstRun || count === 0 ? null : iso(now - 9 * MINUTE),
    jobs: [],
    sessions: [],
    settings,
    drives: defaultDrives(name === "diskFull"),
    egl,
    overlay: { installed: true, enabled: true, version: OVERLAY_VERSION, latestVersion: OVERLAY_LATEST, updateAvailable: true, installPath: "C:\\ProgramData\\Glider\\EOSOverlay", busy: false, managed: true, externalPath: null, note: null },
    collections: firstRun ? [] : buildCollections(games, cfg.seed),
    onboarding: { completed: true, step: "done", skipped: [] },
    freeze: false,
  };
  if (!cfg.eacInstalled) {
    const f = world.games.find((g) => g.summary.appName === FORTNITE) ?? world.deferred.find((g) => g.summary.appName === FORTNITE);
    if (f) f.prerequisites = [{ id: "eac", name: "Easy Anti-Cheat", installed: false }];
  }

  const fortniteApp = FORTNITE;
  const firstUpdate = games.find((g) => installedOk(g) && g.summary.updateAvailable)?.summary.appName ?? fortniteApp;
  const toInstall = games.filter(installable);
  const broken = games.find((g) => g.summary.installStatus === "broken");
  const histJobs = (): SeedJob[] => history(games, cfg.seed);

  switch (name) {
    case "returning":
    case "perf600":
      world.jobs = histJobs();
      break;
    case "firstRun":
      world.auth = { status: "signedOut", account: null };
      world.onboarding = { completed: false, step: "welcome", skipped: [] };
      world.settings = { ...DEFAULT_SETTINGS, libraryFolders: [], defaultInstallBase: null };
      world.overlay = { installed: false, enabled: false, version: null, latestVersion: OVERLAY_LATEST, updateAvailable: false, installPath: null, busy: false, managed: false, externalPath: null, note: null };
      break;
    case "firstRunSignedIn":
      world.onboarding = { completed: false, step: "scan", skipped: [] };
      world.settings = { ...DEFAULT_SETTINGS, libraryFolders: [], defaultInstallBase: null };
      world.collections = [];
      break;
    case "emptyLibrary":
      world.egl.installs = [];
      break;
    case "offline":
      world.connectivity = "offline";
      world.syncError = Errors.offline("Syncing your library");
      world.jobs = histJobs();
      break;
    case "syncError":
      world.syncError = Errors.network("GET https://library-service.live.use1a.on.epicgames.com/library/api/public/items: connection reset");
      world.jobs = histJobs();
      break;
    case "queue": {
      const q = shuffled(mulberry32(cfg.seed ^ 0x9), toInstall);
      world.jobs = [
        { kind: "install", appName: q[0]!.summary.appName, status: "running", seek: 0.42 },
        { kind: "update", appName: firstUpdate, status: "paused", seek: 0.31 },
        { kind: "install", appName: q[1]!.summary.appName, status: "queued" },
        { kind: "install", appName: q[2]!.summary.appName, status: "queued" },
        { kind: "update", appName: games.filter((g) => installedOk(g) && g.summary.updateAvailable)[1]?.summary.appName ?? fortniteApp, status: "queued" },
        ...histJobs(),
      ];
      break;
    }
    case "downloading":
      world.jobs = [{ kind: "install", appName: toInstall[0]!.summary.appName, status: "running", seek: 0.42 }, ...histJobs().slice(0, 4)];
      break;
    case "playing":
      world.sessions = [{ appName: fortniteApp, title: "Fortnite", state: "running", since: iso(now - 38 * MINUTE), offline: false, untracked: false }];
      world.jobs = histJobs();
      break;
    case "updates": {
      let n = 0;
      for (const g of world.games) {
        if (isBase(g) && g.summary.installStatus === "installed" && !g.summary.updateAvailable && n < 16) {
          g.summary = { ...g.summary, updateAvailable: true, latestVersion: `${g.summary.installedVersion ?? "1.0.0"}.1`, downloadSizeBytes: g.updateDownloadBytes };
          n++;
        }
      }
      world.jobs = histJobs().slice(0, 5);
      break;
    }
    case "authExpired":
      world.auth = { status: "expired", account: ACCOUNT };
      world.jobs = histJobs();
      break;
    case "diskFull":
      world.jobs = histJobs().slice(0, 4);
      break;
    case "eglBusy": {
      world.egl.busyApps = [firstUpdate];
      world.jobs = [{ kind: "update", appName: firstUpdate, status: "waiting", seek: 0.2, eglBusy: true }, ...histJobs().slice(0, 4)];
      break;
    }
    case "errors": {
      const inst = games.filter(installedOk);
      const codes: [ErrorCode, JobKind][] = [
        ["insufficientSpace", "update"],
        ["network", "install"],
        ["filesLocked", "update"],
        ["cdnUnavailable", "install"],
        ["corrupted", "verify"],
        ["legendaryFailed", "move"],
      ];
      world.syncError = Errors.network("library sync timed out");
      // The update that failed for space: its drive really is nearly full, so the row can say how much is missing.
      const spaceDrive = driveLetter(inst[0]?.summary.installPath ?? "");
      for (const d of world.drives) if (d.letter === spaceDrive) d.freeBytes = Math.round(0.9 * GiB);
      world.jobs = [
        ...codes.map(([error, kind], i): SeedJob => ({
          kind,
          appName: kind === "install" ? (toInstall[i]?.summary.appName ?? FORTNITE) : (inst[i]?.summary.appName ?? FORTNITE),
          status: "failed",
          error,
          finishedAgoMs: (i + 1) * 25 * MINUTE,
          durationS: 120 + i * 40,
        })),
        ...histJobs().slice(0, 3),
      ];
      break;
    }
    default: {
      const f = FROZEN[name];
      if (f) {
        world.freeze = true;
        const target =
          f.pick === "notInstalled" ? toInstall[0]
          : f.pick === "broken" ? broken
          : f.pick === "installedUpdate" ? games.find((g) => g.summary.appName === firstUpdate)
          : games.find(installedOk);
        const appName = target?.summary.appName ?? fortniteApp;
        world.jobs = [
          { kind: f.kind, appName, status: "running", phase: f.phase, seek: f.seek, uninstallSeconds: f.kind === "uninstall" ? 6 : undefined, targetPath: f.kind === "move" ? "E:\\Epic Games" : undefined },
          ...histJobs().slice(0, 5),
        ];
      }
    }
  }
  return world;
}

