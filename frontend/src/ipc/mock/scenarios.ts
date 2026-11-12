import type {
  AppSnapshot,
  GameState,
  InstallCandidate,
  JobInfo,
  JobKind,
  JobProgress,
  UninstallMode,
  VerifyMode,
} from "../contract.gen";
import { GAME_ART } from "./art";
import {
  ACCOUNT,
  DAY,
  DATA_DIRECTORY,
  DEFAULT_SETTINGS,
  EGL_BLOCKED_REASON,
  EGL_INSTALL_PATH,
  EGL_MANIFEST_PATH,
  EGL_STALE_PATH,
  FULL_DOWNLOAD_BYTES,
  FULL_INSTALL_BYTES,
  GiB,
  INSTALLED_ON_DISK_BYTES,
  LATEST_VERSION,
  LOG_DIRECTORY,
  MINUTE,
  OLDER_VERSION,
  OLD_VERSION,
  UPDATE_DOWNLOAD_BYTES,
  UPDATE_HEADROOM_BYTES,
  iso,
} from "./constants";
import { Errors } from "./errors";
import { stressArt } from "./artStress";
import type { MockArt, ScenarioInfo, ScenarioName } from "./options";

export const SCENARIOS: readonly ScenarioInfo[] = [
  { name: "fresh", label: "Fresh", description: "First run: signed out, nothing installed, no Epic Games Launcher." },
  { name: "eglSignedOut", label: "EGL found (signed out)", description: "First run on a PC with Epic Games Launcher and Fortnite installed." },
  { name: "signedIn", label: "Signed in", description: "Signed in during onboarding, no install, no Epic Games Launcher." },
  { name: "eglDetected", label: "EGL detected", description: "Signed in; one Epic Games Launcher install at the latest version." },
  { name: "eglMultiple", label: "EGL multiple", description: "Signed in; two candidates, one outdated and one stale." },
  { name: "installed", label: "Installed", description: "Up to date, ready to play, shared with Epic Games Launcher." },
  { name: "updateAvailable", label: "Update available", description: "Installed, a 1.2 GB update is waiting." },
  { name: "downloading", label: "Downloading", description: "A fresh install is downloading (seek sets the progress)." },
  { name: "eglBusy", label: "EGL busy", description: "Epic Games Launcher is updating Fortnite; Glider's update waits." },
  { name: "diskFull", label: "Disk full", description: "The update failed for lack of space; the drive is nearly full." },
  { name: "authExpired", label: "Session expired", description: "Installed, but the Epic session needs a fresh sign-in." },
  { name: "playing", label: "Playing", description: "Fortnite is running (1 h 12 min so far)." },
];

/** Side information the backend needs to run or finish a job. */
export interface JobMeta {
  candidateId?: string | null;
  adoptPath?: string;
  adoptVersion?: string | null;
  adoptFromEgl?: boolean;
  verifyMode?: VerifyMode;
  uninstallMode?: UninstallMode;
  followUpJobId?: string;
}

export interface BuildConfig {
  backdrop: "mica" | "solid";
  art: MockArt;
}

export interface ScenarioBuild {
  snapshot: AppSnapshot;
  metas: Map<string, JobMeta>;
  /** The running job that `seek` applies to. */
  seekJobId: string | null;
}

function candidate(id: string, installPath: string, version: string | null, extra: Partial<InstallCandidate> = {}): InstallCandidate {
  return {
    id,
    source: "epicGamesLauncher",
    installPath,
    version,
    folderExists: true,
    executableFound: true,
    isStale: false,
    sizeBytes: INSTALLED_ON_DISK_BYTES,
    ...extra,
  };
}

function finalProgress(jobId: string, downloadBytes: number, writeBytes: number, elapsed: number, phase: JobProgress["phase"] = "finalizing"): JobProgress {
  return {
    jobId,
    phase,
    percent: 100,
    downloadedBytes: downloadBytes,
    totalDownloadBytes: downloadBytes,
    writtenBytes: writeBytes,
    totalWriteBytes: writeBytes,
    downloadBytesPerSecond: 0,
    diskBytesPerSecond: 0,
    rawDownloadBytesPerSecond: 0,
    etaSeconds: 0,
    elapsedSeconds: elapsed,
  };
}

function job(id: string, kind: JobKind, status: JobInfo["status"], createdAt: number, extra: Partial<JobInfo> = {}): JobInfo {
  return {
    id,
    kind,
    status,
    progress: null,
    error: null,
    createdAt: iso(createdAt),
    finishedAt: null,
    targetPath: null,
    fromVersion: null,
    toVersion: null,
    blockedReason: null,
    ...extra,
  };
}

export function buildScenario(name: ScenarioName, cfg: BuildConfig, now: number): ScenarioBuild {
  const metas = new Map<string, JobMeta>();
  let seekJobId: string | null = null;

  const game: GameState = {
    appName: "Fortnite",
    title: "Fortnite",
    installStatus: "notInstalled",
    installed: null,
    latestVersion: LATEST_VERSION,
    updateAvailable: false,
    checkingForUpdates: false,
    lastUpdateCheck: null,
    runState: "idle",
    runningSince: null,
    art: cfg.art === "none" ? null : cfg.art === "default" ? { ...GAME_ART } : stressArt(cfg.art),
    downloadSizeBytes: FULL_DOWNLOAD_BYTES,
    installSizeBytes: FULL_INSTALL_BYTES,
  };

  const s: AppSnapshot = {
    app: {
      version: "0.1.0",
      legendaryVersion: "0.21.1",
      isWindows11: true,
      backdrop: cfg.backdrop,
      dataDirectory: DATA_DIRECTORY,
      logDirectory: LOG_DIRECTORY,
      cpuCount: 16,
      totalMemoryBytes: 32 * GiB,
      autoWorkers: 8,
      autoSharedMemoryMb: 1024,
      warm: true,
    },
    auth: { status: "signedOut", account: null },
    game,
    jobs: [],
    egl: {
      launcherInstalled: false,
      launcherRunning: false,
      busyWithGame: false,
      candidates: [],
      syncEnabled: DEFAULT_SETTINGS.eglSync,
      syncAvailable: false,
      manifestPath: null,
      lastScan: null,
    },
    settings: { ...DEFAULT_SETTINGS },
    onboarding: { completed: false, step: "welcome" },
  };

  const signedIn = (): void => {
    s.auth = { status: "signedIn", account: { ...ACCOUNT } };
  };
  const onboardingChoose = (): void => {
    s.onboarding = { completed: false, step: "chooseInstall" };
  };
  const eglInstalled = (candidates: InstallCandidate[], lastScan = now - 2 * MINUTE): void => {
    s.egl.launcherInstalled = true;
    s.egl.candidates = candidates;
    s.egl.lastScan = iso(lastScan);
  };
  /** Installed through Epic Games Launcher and adopted by Glider. */
  const installed = (version: string, opts: { installedAgo?: number } = {}): void => {
    signedIn();
    s.onboarding = { completed: true, step: "done" };
    game.installStatus = "installed";
    game.installed = {
      installPath: EGL_INSTALL_PATH,
      version,
      installSizeBytes: INSTALLED_ON_DISK_BYTES,
      sharedWithEgl: true,
      needsVerification: false,
      installedAt: iso(now - (opts.installedAgo ?? 9 * DAY)),
    };
    game.updateAvailable = version !== LATEST_VERSION;
    game.downloadSizeBytes = game.updateAvailable ? UPDATE_DOWNLOAD_BYTES : null;
    game.lastUpdateCheck = iso(now - 4 * MINUTE);
    eglInstalled([candidate("egl-1", EGL_INSTALL_PATH, version)], now - 4 * MINUTE);
    s.egl.syncAvailable = true;
    s.egl.manifestPath = EGL_MANIFEST_PATH;
  };

  switch (name) {
    case "fresh":
      game.latestVersion = null;
      game.downloadSizeBytes = game.installSizeBytes = null;
      break;

    case "eglSignedOut":
      game.latestVersion = null;
      game.downloadSizeBytes = game.installSizeBytes = null;
      eglInstalled([candidate("egl-1", EGL_INSTALL_PATH, LATEST_VERSION)]);
      break;

    case "signedIn":
      signedIn();
      onboardingChoose();
      break;

    case "eglDetected":
      signedIn();
      onboardingChoose();
      eglInstalled([candidate("egl-1", EGL_INSTALL_PATH, LATEST_VERSION)]);
      break;

    case "eglMultiple":
      signedIn();
      onboardingChoose();
      eglInstalled([
        candidate("egl-1", EGL_INSTALL_PATH, OLD_VERSION),
        candidate("egl-2", EGL_STALE_PATH, OLD_VERSION, { folderExists: false, executableFound: false, isStale: true, sizeBytes: null }),
      ]);
      break;

    case "installed": {
      installed(LATEST_VERSION);
      const created = now - DAY - 40 * MINUTE;
      s.jobs.push(
        job("job-1", "update", "completed", created, {
          finishedAt: iso(created + 74_000),
          targetPath: EGL_INSTALL_PATH,
          fromVersion: OLDER_VERSION,
          toVersion: OLD_VERSION,
          progress: finalProgress("job-1", Math.round(0.9 * GiB), Math.round(3.1 * GiB), 74),
        }),
      );
      break;
    }

    case "updateAvailable":
      installed(OLD_VERSION);
      break;

    case "downloading": {
      signedIn();
      s.onboarding = { completed: true, step: "done" };
      game.installStatus = "installing";
      const verifiedAt = now - 2 * DAY;
      s.jobs.push(
        job("job-1", "verify", "completed", verifiedAt, {
          finishedAt: iso(verifiedAt + 41_000),
          progress: finalProgress("job-1", 0, INSTALLED_ON_DISK_BYTES, 41, "verifying"),
        }),
        job("job-2", "install", "running", now - 6 * MINUTE, {
          targetPath: "D:\\Games\\Fortnite",
          toVersion: LATEST_VERSION,
        }),
      );
      metas.set("job-2", {});
      seekJobId = "job-2";
      break;
    }

    case "eglBusy":
      installed(OLD_VERSION);
      s.egl.launcherRunning = true;
      s.egl.busyWithGame = true;
      s.jobs.push(
        job("job-1", "update", "waiting", now - 90_000, {
          targetPath: EGL_INSTALL_PATH,
          fromVersion: OLD_VERSION,
          toVersion: LATEST_VERSION,
          blockedReason: EGL_BLOCKED_REASON,
        }),
      );
      metas.set("job-1", {});
      break;

    case "diskFull": {
      installed(OLD_VERSION);
      const created = now - 6 * MINUTE;
      s.jobs.push(
        job("job-1", "update", "failed", created, {
          finishedAt: iso(created + 4_000),
          targetPath: EGL_INSTALL_PATH,
          fromVersion: OLD_VERSION,
          toVersion: LATEST_VERSION,
          error: Errors.insufficientSpace(UPDATE_HEADROOM_BYTES, Math.round(0.9 * GiB), "Needed 5154959155 bytes on D:, 966367641 free"),
          progress: {
            ...finalProgress("job-1", UPDATE_DOWNLOAD_BYTES, 0, 4, "preparing"),
            percent: 0,
            downloadedBytes: 0,
            etaSeconds: null,
          },
        }),
      );
      break;
    }

    case "authExpired":
      installed(LATEST_VERSION);
      s.auth = { status: "expired", account: { ...ACCOUNT } };
      game.latestVersion = null;
      game.updateAvailable = false;
      game.downloadSizeBytes = null;
      game.lastUpdateCheck = iso(now - 3 * DAY);
      break;

    case "playing":
      installed(LATEST_VERSION);
      game.runState = "running";
      game.runningSince = iso(now - 72 * MINUTE);
      s.egl.launcherRunning = false;
      break;
  }

  s.egl.syncEnabled = s.settings.eglSync;
  return { snapshot: s, metas, seekJobId };
}
