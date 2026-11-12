import type { Account, Settings } from "../contract.gen";

export const MiB = 1024 ** 2;
export const GiB = 1024 ** 3;
export const TiB = 1024 ** 4;

export const MINUTE = 60_000;
export const HOUR = 3_600_000;
export const DAY = 86_400_000;

export const LATEST_VERSION = "++Fortnite+Release-40.20-CL-47120934-Windows";
export const OLD_VERSION = "++Fortnite+Release-40.10-CL-46698122-Windows";
export const OLDER_VERSION = "++Fortnite+Release-40.00-CL-46302871-Windows";

/** Manifest sizes for a fresh install. */
export const FULL_DOWNLOAD_BYTES = Math.round(27.9 * GiB);
export const FULL_INSTALL_BYTES = Math.round(96.3 * GiB);
/** What an existing install occupies on disk (slightly smaller than the manifest total). */
export const INSTALLED_ON_DISK_BYTES = Math.round(94.8 * GiB);
export const UPDATE_DOWNLOAD_BYTES = Math.round(1.2 * GiB);
export const UPDATE_WRITE_BYTES = Math.round(4.1 * GiB);
/** Free headroom an update needs (patching writes alongside the old files). */
export const UPDATE_HEADROOM_BYTES = Math.round(4.8 * GiB);
export const REPAIR_DOWNLOAD_BYTES = Math.round(0.18 * GiB);
export const REPAIR_WRITE_BYTES = Math.round(0.46 * GiB);
export const INSTALL_MARGIN_BYTES = 1 * GiB;

export const DATA_DIRECTORY = "C:\\Users\\alex\\AppData\\Local\\Glider";
export const LOG_DIRECTORY = `${DATA_DIRECTORY}\\logs`;
export const EGL_MANIFEST_PATH = "C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests\\3F2A91C4B7E04D11A6C0D8E5F27B9A13.item";
export const EGL_INSTALL_PATH = "D:\\Epic Games\\Fortnite";
export const EGL_STALE_PATH = "E:\\Old Games\\Fortnite";
export const DEFAULT_INSTALL_BASE = "D:\\Games";
export const EGL_BLOCKED_REASON = "Epic Games Launcher is updating Fortnite";

export const ACCOUNT: Account = {
  accountId: "a1b2c3d4e5f6478899aabbccddeeff00",
  displayName: "Marlowe",
  avatarUrl: null,
};

/** Mirrors `Settings.Default` in Models.cs. */
export const DEFAULT_SETTINGS: Settings = {
  downloadWorkers: null,
  sharedMemoryMb: null,
  preferredCdn: null,
  autoUpdate: true,
  autoUpdateOnlyWhenIdle: true,
  updateCheckIntervalMinutes: 30,
  startWithWindows: false,
  startMinimized: false,
  closeToTray: true,
  minimizeToTrayWhilePlaying: true,
  launchArgs: "",
  logLevel: "info",
  theme: "system",
  eglSync: true,
  notifications: true,
  defaultInstallBase: null,
};

export const INVALID_FOLDER_REASON =
  "This folder doesn't contain Fortnite. Choose the folder that contains FortniteGame and Engine.";
export const STALE_CANDIDATE_REASON =
  "Fortnite isn't at this location any more. Epic Games Launcher still lists it, but the folder is gone.";

/** "++Fortnite+Release-40.20-CL-…" → "40.20" */
export function shortVersion(version: string | null): string {
  if (!version) return "unknown";
  return /Release-(\d+(?:\.\d+)*)/.exec(version)?.[1] ?? version;
}

export const iso = (ms: number = Date.now()): string => new Date(ms).toISOString();
export const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
