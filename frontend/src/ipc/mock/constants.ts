import type { Account, Settings } from "../contract.gen";

export const KiB = 1024;
export const MiB = 1024 ** 2;
export const GiB = 1024 ** 3;
export const TiB = 1024 ** 4;

export const SECOND = 1000;
export const MINUTE = 60_000;
export const HOUR = 3_600_000;
export const DAY = 86_400_000;

/** The user's v0.1 game. The only real title in the mock library (named, never with Epic's art). */
export const FORTNITE = "Fortnite";
export const LATEST_VERSION = "++Fortnite+Release-40.20-CL-47120934-Windows";
export const OLD_VERSION = "++Fortnite+Release-40.10-CL-46698122-Windows";

export const OVERLAY_APP = "eos-overlay";
export const OVERLAY_VERSION = "1.4.2";
export const OVERLAY_LATEST = "1.5.0";

export const DATA_DIRECTORY = "C:\\Users\\alex\\AppData\\Local\\Glider";
export const LOG_DIRECTORY = `${DATA_DIRECTORY}\\logs`;

export const ACCOUNT: Account = {
  accountId: "a1b2c3d4e5f6478899aabbccddeeff00",
  displayName: "Marlowe",
  avatarUrl: null,
};

/** Mirrors `Settings.Default` in Models.cs. */
export const DEFAULT_SETTINGS: Settings = {
  downloadMode: "balanced",
  speedLimitBytesPerSecond: null,
  idleFullSpeed: true,
  whilePlaying: "pause",
  downloadWorkers: null,
  sharedMemoryMb: null,
  preferredCdn: null,
  autoUpdate: "always",
  autoUpdateOnlyWhenIdle: true,
  updateCheckIntervalMinutes: 30,
  startWithWindows: false,
  startMinimized: false,
  closeToTray: true,
  minimizeToTrayWhilePlaying: true,
  logLevel: "info",
  theme: "system",
  eglSync: true,
  notifications: true,
  defaultInstallBase: null,
  libraryFolders: [],
  cloudSaves: true,
  handleEpicLinks: false,
};

export const CDNS = [
  "download.epicgames.com",
  "epicgames-download1.akamaized.net",
  "fastly-download.epicgames.com",
  "cloudflare.epicgamescdn.com",
] as const;

/** "++Fortnite+Release-40.20-CL-…" → "40.20"; other versions pass through. */
export function shortVersion(version: string | null): string {
  if (!version) return "unknown";
  return /Release-(\d+(?:\.\d+)*)/.exec(version)?.[1] ?? version;
}

export const iso = (ms: number = Date.now()): string => new Date(ms).toISOString();
export const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
