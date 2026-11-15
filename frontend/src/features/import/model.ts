import type { CandidateSource, EglInstall, EglInstallStatus, GliderError, JobInfo } from "../../ipc/contract.gen";
import { isActiveJob } from "../../state/store";

/**
 * Pure model of "bring existing installs into Glider": which installs can be picked, what an import job's outcome
 * means for the person, and the totals. The hook (`useImportSession`) and the list render on top of this.
 */

export interface InstallGroups {
  /** Selectable: Epic Games Launcher (or Glider 0.1, or a chosen folder) has it and Glider can take it over. */
  ready: EglInstall[];
  /** Already managed by Glider. Listed so the scan is complete; nothing to do. */
  imported: EglInstall[];
  /** Not selectable: the folder is gone, Epic is busy with it, or it isn't in the signed-in account. Each carries a reason. */
  unavailable: EglInstall[];
}

export function groupInstalls(installs: readonly EglInstall[]): InstallGroups {
  const out: InstallGroups = { ready: [], imported: [], unavailable: [] };
  for (const i of installs) {
    if (i.status === "importable") out.ready.push(i);
    else if (i.status === "imported") out.imported.push(i);
    else out.unavailable.push(i);
  }
  const byTitle = (a: EglInstall, b: EglInstall) => a.title.localeCompare(b.title, undefined, { sensitivity: "base" });
  out.ready.sort(byTitle);
  out.imported.sort(byTitle);
  out.unavailable.sort(byTitle);
  return out;
}

export const SOURCE_LABEL: Record<CandidateSource, string> = {
  epicGamesLauncher: "Epic Games Launcher",
  gliderV1: "Glider 0.1",
  manual: "Chosen folder",
};

/**
 * `imported` = the game is in Glider and its files are fine ("Ready"). `checking` = Core queued a verify job for it and that hasn't
 * finished (a verify repairs what it finds on its own, so nobody knows yet whether anything is damaged). `needsRepair` = that verify
 * ended with damage left.
 */
export type RowState = "waiting" | "running" | "checking" | "imported" | "needsRepair" | "failed" | "cancelled";

export interface RowResult {
  state: RowState;
  /** Plain-language detail for the result (the outcome message, or the failure reason). */
  message: string | null;
  error: GliderError | null;
  /** The verify job's count: files it repaired (`imported`) or files still damaged (`needsRepair`). 0 = none, or unknown. */
  files: number;
}

const result = (state: RowState, message: string | null = null, error: GliderError | null = null, files = 0): RowResult => ({ state, message, error, files });

/** The verify job Core queued for an import: the game's newest one created at or after the import job. */
export function verifyJobFor(importJob: JobInfo | null | undefined, candidates: readonly JobInfo[]): JobInfo | null {
  if (!importJob) return null;
  const since = Date.parse(importJob.createdAt);
  let best: JobInfo | null = null;
  for (const j of candidates) {
    if (j.kind !== "verify" || j.appName !== importJob.appName || Date.parse(j.createdAt) < since) continue;
    if (!best || Date.parse(j.createdAt) >= Date.parse(best.createdAt)) best = j; // on a tie the later candidate (the snapshot's copy) wins
  }
  return best;
}

/** After an import that asked for a check: what the verify job says. No verify job yet, or one still going, is "checking". */
function afterVerify(verify: JobInfo | null | undefined): RowResult {
  if (!verify || isActiveJob(verify)) return result("checking");
  const r = verify.verify;
  const left = r ? Math.max(0, r.filesBad - r.filesFixed) : 0;
  if (verify.status === "completed" && (!r || r.filesBad === 0 || r.verified)) return result("imported", null, null, r?.filesFixed ?? 0);
  // Failed ("still bad"), canceled, or ended unverified: the files aren't known to be fine, and Repair finishes the job.
  return result("needsRepair", verify.error?.message ?? verify.outcome?.message ?? null, null, left);
}

/**
 * What one import means for the person: the import job, then (when Core queued a check) that game's verify job. A job we have not
 * heard about yet is simply waiting.
 */
export function rowResult(job: JobInfo | null | undefined, verify?: JobInfo | null): RowResult {
  if (!job) return result("waiting");
  switch (job.status) {
    case "queued":
    case "waiting":
    case "paused":
      return result("waiting", job.blockedReason);
    case "running":
      return result("running");
    case "completed":
      if (job.outcome?.kind === "failed") return result("failed", job.outcome.message, job.error);
      // Core marks an import "needs repair" while it verifies the files it adopted; the verify job tells how that went.
      if (job.outcome?.kind === "needsRepair") return afterVerify(verify);
      return result("imported", job.outcome?.message ?? null);
    case "failed":
      return result("failed", job.error?.message ?? job.outcome?.message ?? "Glider couldn't import this install.", job.error);
    case "cancelled":
      return result("cancelled");
  }
}

export interface BatchSummary {
  total: number;
  /** In Glider with files that are fine (or were repaired). */
  imported: number;
  /** In Glider, with the check still running. */
  checking: number;
  needsRepair: number;
  failed: number;
  cancelled: number;
  /** Still waiting or running. */
  pending: number;
  settled: boolean;
}

export function summarize(states: readonly RowState[]): BatchSummary {
  const s: BatchSummary = { total: states.length, imported: 0, checking: 0, needsRepair: 0, failed: 0, cancelled: 0, pending: 0, settled: false };
  for (const st of states) {
    if (st === "imported") s.imported++;
    else if (st === "checking") s.checking++;
    else if (st === "needsRepair") s.needsRepair++;
    else if (st === "failed") s.failed++;
    else if (st === "cancelled") s.cancelled++;
    else s.pending++;
  }
  // Settled = every import has run; a check that is still going doesn't hold the person up.
  s.settled = s.total > 0 && s.pending === 0;
  return s;
}

/** Games that ended up in Glider, with or without a follow-up. */
export const linkedCount = (s: BatchSummary): number => s.imported + s.checking + s.needsRepair;

export function sumBytes(installs: readonly EglInstall[]): number {
  let n = 0;
  for (const i of installs) n += i.sizeBytes ?? 0;
  return n;
}

/** Three or four words for why an install can't be picked. The backend's full reason stays available as a tooltip. */
export function unavailableLabel(status: EglInstallStatus): string {
  switch (status) {
    case "imported":
      return "Already in Glider";
    case "stale":
      return "Folder missing";
    case "busy":
      return "Launcher is busy";
    case "notOwned":
      return "Not in your library";
    case "importable":
      return "";
  }
}

/** Why an import failed, in a few lowercase words ("folder in use"), for "Failed · folder in use". */
export function shortFailure(error: GliderError | null, message: string | null): string {
  switch (error?.code) {
    case "filesLocked":
      return "folder in use";
    case "permissionDenied":
      return "no permission";
    case "network":
    case "offline":
    case "timeout":
      return "no connection";
    case "notOwned":
      return "not in your library";
    case "notFound":
    case "invalidFolder":
      return "folder missing";
    case "eglBusy":
      return "Launcher is busy";
    case "insufficientSpace":
      return "not enough space";
    case "gameRunning":
      return "game is running";
    case "corrupted":
      return "files damaged";
  }
  const t = (error?.title ?? message ?? "").replace(/\.$/, "");
  return t.length > 0 && t.length <= 28 ? t.charAt(0).toLowerCase() + t.slice(1) : "see logs";
}

/** The short phrase beside a game in the results: "Ready", "Checking files", "Repaired · 12 files", "Needs repair · 12 files", "Failed · folder in use". */
export function resultPhrase(result: RowResult): string {
  const files = `${result.files} ${result.files === 1 ? "file" : "files"}`;
  switch (result.state) {
    case "waiting":
      return "Waiting";
    case "running":
      return "";
    case "checking":
      return "Checking files";
    case "imported":
      return result.files > 0 ? `Repaired · ${files}` : "Ready";
    case "needsRepair":
      return result.files > 0 ? `Needs repair · ${files}` : "Needs repair";
    case "failed":
      return `Failed · ${shortFailure(result.error, result.message)}`;
    case "cancelled":
      return "Cancelled";
  }
}

/**
 * One headline for a batch: "Importing 3 of 8", then what is true now: "8 games added, 2 being checked" while Core verifies the
 * adopted files, "8 games ready" once it has, "7 games ready, 1 needs a repair" when a check left damage.
 */
export function batchHeadline(s: BatchSummary): string {
  if (!s.settled) return `Importing ${s.total - s.pending} of ${s.total}`;
  if (linkedCount(s) === 0) return "Nothing was imported";
  const games = (n: number) => `${n} ${n === 1 ? "game" : "games"}`;
  const parts: string[] = [];
  if (s.imported > 0) parts.push(`${games(s.imported)} ${s.checking > 0 ? "added" : "ready"}`);
  if (s.checking > 0) parts.push(s.imported > 0 ? `${s.checking} being checked` : `${games(s.checking)} being checked`);
  if (s.needsRepair > 0) parts.push(parts.length > 0 ? `${s.needsRepair} ${s.needsRepair === 1 ? "needs" : "need"} a repair` : `${games(s.needsRepair)} ${s.needsRepair === 1 ? "needs" : "need"} a repair`);
  if (s.failed > 0) parts.push(`${s.failed} failed`);
  return parts.join(", ");
}

/** The line under a settled batch's headline: that nothing was downloaded, and what a repair does only when one is needed. */
export function batchNote(s: BatchSummary): string | null {
  if (!s.settled || linkedCount(s) === 0) return null;
  return s.needsRepair > 0 ? "Nothing was downloaded. Repairs re-download only damaged files." : "Nothing was downloaded.";
}

/** What to do when the scan found nothing. Without the launcher on this PC, say so first. */
export function emptyBody(launcherInstalled: boolean): string {
  const next = "Scan your drives or point Glider to a folder.";
  return launcherInstalled ? next : `Epic Games Launcher isn't installed. ${next}`;
}
