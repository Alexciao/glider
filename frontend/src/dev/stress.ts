import type { AppSnapshot, EglInstall, JobInfo, JobKind, JobProgress } from "../ipc/contract.gen";
import { refreshSnapshot } from "../state/actions";
import { setProgressTransform } from "../state/bridge";
import { setSnapshotTransform } from "../state/store";

/**
 * Dev-only "break the UI" data. Worst-case values enter at the same boundary as the demo data (the backend snapshot),
 * and every value is realistic or a real limit: Epic display names are 3–16 characters, Windows paths run to 260,
 * Legendary versions come in shapes we don't parse, disks reach terabytes, a stalled CDN reports a few hundred bytes
 * per second. (The library itself is stressed by the mock's own scenarios: `perf600`, `emptyLibrary`.)
 */
export type StressMode = "demo" | "worst" | "empty" | "one" | "huge";
export const STRESS_MODES: readonly { value: StressMode; label: string }[] = [
  { value: "demo", label: "Demo data" },
  { value: "worst", label: "Worst case" },
  { value: "empty", label: "Empty" },
  { value: "one", label: "One" },
  { value: "huge", label: "Huge" },
];

const NAME = "Wiśniewska_K_Jr"; // 15 of Epic's 16 characters, diacritic, underscore-separated
const LONG_PATH = "D:\\Users\\Aleksandra Wiśniewska-Kowalczyk\\Documents\\My Games\\Epic Games Store Library\\The Extraordinarily Long Titled Game";
const UNC_PATH = "\\\\NAS-ARCHIVE-01.northwind-industries-holdings.example.com\\media-share\\Games and Software\\Epic Games Launcher Installs\\Game";
const ODD_VERSION = "++Fortnite+Release-Cert-Next-40.20-CL-47120934-Windows-Shipping-Win64";
const LONG_TITLE = "The Extraordinarily Long Titled Game: Definitive, Complete & Anniversary Edition";
const TB = 1024 ** 4;
const GB = 1024 ** 3;

const LONG_ERROR = {
  code: "filesLocked" as const,
  title: "Game files are in use",
  message:
    "Close the game and any program using its files, then try again. Glider couldn't replace Content\\Paks\\pakchunk10-WindowsClient.ucas because another process (BattlEye, an antivirus scan or a backup tool) still has it open.",
  fix: "retry" as const,
  detail:
    "IOException: The process cannot access the file 'D:\\Users\\Aleksandra Wiśniewska-Kowalczyk\\Documents\\My Games\\Epic Games Store Library\\Game\\Content\\Paks\\pakchunk10-WindowsClient.ucas' because it is being used by another process.\n   at Glider.Legendary.Installer.Replace(String path)\n   at Glider.Jobs.JobRunner.Run(JobContext ctx)",
  appName: null,
};

function job(i: number, kind: JobKind, status: JobInfo["status"], ageMinutes: number, appName: string): JobInfo {
  const finished = new Date(Date.now() - ageMinutes * 60_000).toISOString();
  return {
    id: `stress-${i}`,
    appName,
    title: LONG_TITLE,
    kind,
    status,
    phases: ["preparing", "downloading", "installing", "finalizing"],
    phase: status === "completed" ? "finalizing" : null,
    progress: null,
    error: status === "failed" ? { ...LONG_ERROR, appName } : null,
    createdAt: new Date(Date.now() - (ageMinutes + 4) * 60_000).toISOString(),
    startedAt: null,
    finishedAt: status === "completed" || status === "failed" || status === "cancelled" ? finished : null,
    targetPath: LONG_PATH,
    fromVersion: ODD_VERSION,
    toVersion: ODD_VERSION.replace("40.20", "40.30"),
    blockedReason: status === "waiting" ? "Epic Games Launcher is verifying, repairing and updating three other games" : null,
    verify: null,
    outcome: null,
  };
}

function installs(n: number): EglInstall[] {
  return Array.from({ length: n }, (_, i) => ({
    id: `stress-c${i}`,
    source: i % 3 === 2 ? ("manual" as const) : ("epicGamesLauncher" as const),
    appName: `stress-app-${i}`,
    title: LONG_TITLE,
    installPath: i % 2 === 0 ? UNC_PATH : LONG_PATH,
    drive: "D:\\",
    version: i === 1 ? null : ODD_VERSION,
    sizeBytes: i === 1 ? null : 1.4 * TB,
    status: i === 3 ? ("stale" as const) : i === 4 ? ("busy" as const) : ("importable" as const),
    drift: "none" as const,
    reason: i === 3 ? "This folder doesn't exist." : i === 4 ? "Epic Games Launcher is installing or updating this right now." : null,
    artUrl: null,
  }));
}

export function applyStress(snap: AppSnapshot, mode: StressMode): AppSnapshot {
  if (mode === "demo") return snap;
  const s: AppSnapshot = structuredClone(snap);

  if (mode === "empty") {
    s.queue.jobs = [];
    s.egl.installs = [];
    s.egl.lastScan = null;
    s.collections = [];
    return s;
  }
  if (mode === "one") {
    s.queue.jobs = s.queue.jobs.slice(0, 1);
    s.egl.installs = s.egl.installs.slice(0, 1);
    s.collections = s.collections.slice(0, 1);
    return s;
  }

  const history = Array.from({ length: mode === "huge" ? 160 : 3 }, (_, i) =>
    job(i, (["update", "verify", "install", "move", "repair", "import"] as const)[i % 6]!, i % 7 === 3 ? "failed" : i % 5 === 4 ? "cancelled" : "completed", 30 + i * 90, `stress-app-${i}`),
  );
  if (mode === "huge") {
    s.queue.jobs = [...s.queue.jobs.filter((j) => j.status === "running" || j.status === "waiting" || j.status === "paused" || j.status === "queued"), ...history];
    s.egl.installs = installs(40);
    s.collections = Array.from({ length: 30 }, (_, i) => ({ id: `stress-col-${i}`, name: `${LONG_TITLE} ${i}`, appNames: [] }));
    return s;
  }

  // worst: long, odd, extreme and missing values, mixed across the whole snapshot
  if (s.auth.account) s.auth.account.displayName = NAME;
  s.queue.jobs = [
    ...s.queue.jobs.map((j) => ({
      ...j,
      title: LONG_TITLE,
      targetPath: LONG_PATH,
      fromVersion: ODD_VERSION,
      toVersion: ODD_VERSION.replace("40.20", "40.30"),
      blockedReason: j.blockedReason ? "Epic Games Launcher is verifying, repairing and updating three other games" : null,
    })),
    ...history,
  ];
  s.egl.installs = installs(8);
  s.settings.defaultInstallBase = UNC_PATH;
  s.settings.libraryFolders = [UNC_PATH, LONG_PATH, ...s.settings.libraryFolders];
  s.storage.folders = s.storage.folders.map((f, i) => (i === 0 ? { ...f, path: UNC_PATH, freeBytes: 612 * GB } : f));
  return s;
}

/** A stalled CDN: a few hundred bytes per second, an ETA measured in days, a terabyte-class total. */
export function stressProgress(p: JobProgress, mode: StressMode): JobProgress {
  if (mode !== "worst" && mode !== "huge") return p;
  return {
    ...p,
    etaSeconds: 9.5 * 86400,
    network: p.network && {
      ...p.network,
      totalBytes: 612 * GB,
      bytesPerSecond: 312,
      averageBytesPerSecond: 280,
      peakBytesPerSecond: 61_000_000,
      cdn: "a-very-long-content-delivery-hostname.regional-edge.example-cdn.epicgames.com",
    },
    disk: p.disk && { ...p.disk, totalBytes: 1.4 * TB, bytesPerSecond: 0, installSizeBytes: 1.4 * TB },
  };
}

/* ------------------------------------------------------------------ mode */

let mode: StressMode = "demo";
const listeners = new Set<() => void>();

export const getStressMode = (): StressMode => mode;
export const subscribeStress = (l: () => void): (() => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

export function setStressMode(next: StressMode): void {
  mode = next;
  const url = new URL(location.href);
  if (next === "demo") url.searchParams.delete("data");
  else url.searchParams.set("data", next);
  history.replaceState(null, "", url);
  listeners.forEach((l) => l());
  void refreshSnapshot();
}

/** Called once when the mock backend is active; reads ?data= so a reload keeps the selection. */
export function installStress(search: string): void {
  const q = new URLSearchParams(search).get("data");
  if (q && STRESS_MODES.some((m) => m.value === q)) mode = q as StressMode;
  setSnapshotTransform((s) => applyStress(s, mode));
  setProgressTransform((p) => stressProgress(p, mode));
}
