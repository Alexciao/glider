import type { AppSnapshot, InstallCandidate, JobInfo, JobKind, JobProgress } from "../ipc/contract.gen";
import { refreshSnapshot } from "../state/actions";
import { setProgressTransform } from "../state/bridge";
import { setSnapshotTransform } from "../state/store";

/**
 * Dev-only "break the UI" data. Worst-case values enter at the same boundary as the demo data (the
 * backend snapshot), and every value is realistic or a real limit: Epic display names are 3–16
 * characters, Windows paths run to 260, Legendary versions come in shapes we don't parse, disks
 * reach terabytes, a stalled CDN reports a few hundred bytes per second.
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
const LONG_PATH = "D:\\Users\\Aleksandra Wiśniewska-Kowalczyk\\Documents\\My Games\\Epic Games Store Library\\Fortnite";
const UNC_PATH = "\\\\NAS-ARCHIVE-01.northwind-industries-holdings.example.com\\media-share\\Games and Software\\Epic Games Launcher Installs\\Fortnite";
const ODD_VERSION = "++Fortnite+Release-Cert-Next-40.20-CL-47120934-Windows-Shipping-Win64";
const TB = 1024 ** 4;
const GB = 1024 ** 3;

const LONG_ERROR = {
  code: "filesLocked" as const,
  title: "Game files are in use",
  message:
    "Close Fortnite and any program using its files, then retry. Glider couldn't replace FortniteGame\\Content\\Paks\\pakchunk10-WindowsClient.ucas because another process (BattlEye, an antivirus scan or a backup tool) still has it open.",
  fix: "retry" as const,
  detail:
    "IOException: The process cannot access the file 'D:\\Users\\Aleksandra Wiśniewska-Kowalczyk\\Documents\\My Games\\Epic Games Store Library\\Fortnite\\FortniteGame\\Content\\Paks\\pakchunk10-WindowsClient.ucas' because it is being used by another process.\n   at Glider.Legendary.Installer.Replace(String path)\n   at Glider.Jobs.JobRunner.Run(JobContext ctx)",
};

function job(i: number, kind: JobKind, status: JobInfo["status"], ageMinutes: number): JobInfo {
  const finished = new Date(Date.now() - ageMinutes * 60_000).toISOString();
  return {
    id: `stress-${i}`,
    kind,
    status,
    progress: null,
    error: status === "failed" ? LONG_ERROR : null,
    createdAt: new Date(Date.now() - (ageMinutes + 4) * 60_000).toISOString(),
    finishedAt: status === "completed" || status === "failed" || status === "cancelled" ? finished : null,
    targetPath: LONG_PATH,
    fromVersion: ODD_VERSION,
    toVersion: ODD_VERSION.replace("40.20", "40.30"),
    blockedReason: status === "waiting" ? "Epic Games Launcher is verifying, repairing and updating Fortnite and three other games" : null,
  };
}

function candidates(n: number): InstallCandidate[] {
  return Array.from({ length: n }, (_, i) => ({
    id: `stress-c${i}`,
    source: i % 3 === 2 ? ("manual" as const) : ("epicGamesLauncher" as const),
    installPath: i % 2 === 0 ? UNC_PATH : LONG_PATH,
    version: i === 1 ? null : ODD_VERSION,
    folderExists: i !== 3,
    executableFound: i !== 3 && i !== 4,
    isStale: i === 3,
    sizeBytes: i === 1 ? null : 1.4 * TB,
  }));
}

export function applyStress(snap: AppSnapshot, mode: StressMode): AppSnapshot {
  if (mode === "demo") return snap;
  const s: AppSnapshot = structuredClone(snap);

  if (mode === "empty") {
    s.jobs = [];
    s.egl.candidates = [];
    s.egl.lastScan = null;
    return s;
  }
  if (mode === "one") {
    s.jobs = s.jobs.slice(0, 1);
    s.egl.candidates = s.egl.candidates.slice(0, 1);
    return s;
  }

  const history = Array.from({ length: mode === "huge" ? 160 : 3 }, (_, i) =>
    job(i, (["update", "verify", "install", "move", "repair", "import"] as const)[i % 6]!, i % 7 === 3 ? "failed" : i % 5 === 4 ? "cancelled" : "completed", 30 + i * 90),
  );
  if (mode === "huge") {
    s.jobs = [...s.jobs.filter((j) => j.status === "running" || j.status === "waiting" || j.status === "paused"), ...history];
    s.egl.candidates = candidates(8);
    return s;
  }

  // worst: long, odd, extreme and missing values, mixed across the whole snapshot
  if (s.auth.account) {
    s.auth.account.displayName = NAME;
  }
  s.game.title = "Fortnite: Battle Royale, Zero Build & Festival Editions";
  s.game.latestVersion = ODD_VERSION.replace("40.20", "40.30");
  if (s.game.installed) {
    s.game.installed.installPath = LONG_PATH;
    s.game.installed.version = ODD_VERSION;
    s.game.installed.installSizeBytes = 1.4 * TB;
  }
  if (s.game.downloadSizeBytes !== null) s.game.downloadSizeBytes = 612 * GB;
  s.jobs = [
    ...s.jobs.map((j) => ({ ...j, targetPath: LONG_PATH, fromVersion: ODD_VERSION, toVersion: ODD_VERSION.replace("40.20", "40.30"), blockedReason: j.blockedReason ? "Epic Games Launcher is verifying, repairing and updating Fortnite and three other games" : null })),
    ...history,
  ];
  s.egl.candidates = candidates(5);
  s.settings.launchArgs =
    '-windowed -nosplash -NoEAC -dx12 -USEALLAVAILABLECORES -notexturestreaming -AUTH_LOGIN=unused -AUTH_PASSWORD=exchangecodehere -AUTH_TYPE=exchangecode -epicapp=Fortnite -epicenv=Prod -EpicPortal -epicusername="Wiśniewska_K_Jr" -epicuserid=0123456789abcdef0123456789abcdef';
  s.settings.defaultInstallBase = UNC_PATH.replace("\\Fortnite", "");
  return s;
}

/** A stalled CDN: a few hundred bytes per second, an ETA measured in days, a terabyte-class total. */
export function stressProgress(p: JobProgress, mode: StressMode): JobProgress {
  if (mode !== "worst" && mode !== "huge") return p;
  return {
    ...p,
    totalDownloadBytes: 612 * GB,
    totalWriteBytes: 1.4 * TB,
    downloadBytesPerSecond: 312,
    rawDownloadBytesPerSecond: p.rawDownloadBytesPerSecond > 0 ? 312 + (p.elapsedSeconds % 7) * 40 : 0,
    diskBytesPerSecond: 0,
    etaSeconds: 9.5 * 86400,
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
