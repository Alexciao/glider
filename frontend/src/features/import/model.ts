import type { CandidateSource, EglInstall, EglInstallStatus, GliderError, JobInfo } from "../../ipc/contract.gen";

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

export type RowState = "waiting" | "running" | "imported" | "needsRepair" | "failed" | "cancelled";

export interface RowResult {
  state: RowState;
  /** Plain-language detail for the result (the outcome message, or the failure reason). */
  message: string | null;
  error: GliderError | null;
}

/** What one import job means for the person. A job we have not heard about yet is simply waiting. */
export function rowResult(job: JobInfo | null | undefined): RowResult {
  if (!job) return { state: "waiting", message: null, error: null };
  switch (job.status) {
    case "queued":
    case "waiting":
    case "paused":
      return { state: "waiting", message: job.blockedReason, error: null };
    case "running":
      return { state: "running", message: null, error: null };
    case "completed":
      if (job.outcome?.kind === "failed") return { state: "failed", message: job.outcome.message, error: job.error };
      if (job.outcome?.kind === "needsRepair") return { state: "needsRepair", message: job.outcome.message, error: null };
      return { state: "imported", message: job.outcome?.message ?? null, error: null };
    case "failed":
      return { state: "failed", message: job.error?.message ?? job.outcome?.message ?? "Glider couldn't import this install.", error: job.error };
    case "cancelled":
      return { state: "cancelled", message: null, error: null };
  }
}

export interface BatchSummary {
  total: number;
  imported: number;
  needsRepair: number;
  failed: number;
  cancelled: number;
  /** Still waiting or running. */
  pending: number;
  settled: boolean;
}

export function summarize(states: readonly RowState[]): BatchSummary {
  const s: BatchSummary = { total: states.length, imported: 0, needsRepair: 0, failed: 0, cancelled: 0, pending: 0, settled: false };
  for (const st of states) {
    if (st === "imported") s.imported++;
    else if (st === "needsRepair") s.needsRepair++;
    else if (st === "failed") s.failed++;
    else if (st === "cancelled") s.cancelled++;
    else s.pending++;
  }
  s.settled = s.total > 0 && s.pending === 0;
  return s;
}

/** Games that ended up in Glider, with or without a follow-up. */
export const linkedCount = (s: BatchSummary): number => s.imported + s.needsRepair;

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

/** Why an import failed, in a few lowercase words ("folder in use"), for "Failed: folder in use". */
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

/** The short phrase beside a game in the results: "Imported", "Needs repair: 12 files", "Failed: folder in use". */
export function resultPhrase(result: RowResult, job: JobInfo | null): string {
  switch (result.state) {
    case "waiting":
      return "Waiting";
    case "running":
      return "";
    case "imported":
      return "Imported";
    case "needsRepair": {
      const bad = job?.verify?.filesBad ?? 0;
      return bad > 0 ? `Needs repair: ${bad} ${bad === 1 ? "file" : "files"}` : "Needs repair";
    }
    case "failed":
      return `Failed: ${shortFailure(result.error, result.message)}`;
    case "cancelled":
      return "Cancelled";
  }
}

/** One headline for a batch: "Importing 3 of 8", "8 games imported", "7 of 8 imported". */
export function batchHeadline(s: BatchSummary): string {
  if (!s.settled) return `Importing ${s.total - s.pending} of ${s.total}`;
  const linked = linkedCount(s);
  if (linked === 0) return "Nothing was imported";
  return linked === s.total ? `${linked} ${linked === 1 ? "game" : "games"} imported` : `${linked} of ${s.total} imported`;
}
