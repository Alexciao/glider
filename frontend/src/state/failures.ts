import type { FixAction, JobInfo, StorageState } from "../ipc/contract.gen";
import { driveOf, formatBytes, formatRelativeTime } from "./format";
import { KIND_NOUN } from "./transfer";

/**
 * What the "Couldn't finish" section says about one failed job, as pure data: the one-line message, which buttons it
 * offers and in what order. A failure for lack of space compares what the job needed with the drive's free space as it is
 * now, so a user who has already freed space is not told to do it again, and is never locked out of "Try again".
 */

/**
 * A job that didn't do what was asked: it failed, or it ran to the end and its outcome says it failed (an import Legendary didn't
 * register ends `completed` with outcome `failed`; the status stays so history keeps the job, but the person still has to look).
 */
export const isFailed = (j: JobInfo): boolean => j.status === "failed" || j.outcome?.kind === "failed";

/** Failed jobs, newest first (the order `queue.jobs` already has). */
export const failedJobsOf = (jobs: readonly JobInfo[]): JobInfo[] => jobs.filter(isFailed);

/** Finished work that is not a failure: completed and canceled jobs, for History. */
export const pastJobsOf = (jobs: readonly JobInfo[]): JobInfo[] => jobs.filter((j) => (j.status === "completed" || j.status === "cancelled") && !isFailed(j));

/** Free bytes on a drive ("D:") as the snapshot knows them; null when the snapshot doesn't list that drive. */
export function freeOnDrive(storage: Pick<StorageState, "drives" | "folders">, drive: string): number | null {
  if (!drive) return null;
  const letter = drive.toUpperCase();
  const d = storage.drives.find((x) => driveOf(x.root) === letter);
  if (d) return d.freeBytes;
  const f = storage.folders.find((x) => driveOf(x.path) === letter);
  return f ? f.freeBytes : null;
}

export interface SpaceNeed {
  /** What the job needed, in bytes. */
  needed: number;
  /** What the target drive has now; null when the snapshot can't say. */
  free: number | null;
  /** "D:", or "" when the failure didn't name one. */
  drive: string;
  /** There is room now: free space has caught up with the need. */
  enough: boolean;
}

/** The space picture of a failed job, or null when it didn't fail for lack of space (or didn't say how much it needed). */
export function spaceNeedOf(job: JobInfo, storage: Pick<StorageState, "drives" | "folders">): SpaceNeed | null {
  const e = job.error;
  if (!e || e.code !== "insufficientSpace" || e.requiredBytes == null || e.requiredBytes <= 0) return null;
  const drive = driveOf(e.drive ?? job.targetPath);
  const free = freeOnDrive(storage, drive);
  return { needed: e.requiredBytes, free, drive, enough: free !== null && free >= e.requiredBytes };
}

/** "Update failed · 25 min ago". */
export const failedMeta = (job: JobInfo, now: number): string => `${KIND_NOUN[job.kind]} failed · ${formatRelativeTime(job.finishedAt ?? job.createdAt, now)}`;

/** The one-line message of a failure. For space it is the live comparison, not the sentence Core wrote at failure time. */
export function failureMessage(job: JobInfo, space: SpaceNeed | null): string {
  if (space) {
    if (space.enough) return "There's enough space now.";
    const on = space.drive ? ` on ${space.drive}` : "";
    return space.free === null ? `Needs ${formatBytes(space.needed)} free${on}` : `Needs ${formatBytes(space.needed)}, ${formatBytes(space.free)} free${on}`;
  }
  return (job.error?.message ?? job.outcome?.message ?? "Something went wrong.").trim();
}

export type FailureAction = { id: "retry"; label: string } | { id: "fix"; fix: FixAction; label: string };

export interface FailureActions {
  primary: FailureAction;
  /** Only while a space failure is still short: "Try again" stays, quieter, because free space can come from somewhere Glider can't see. */
  secondary?: FailureAction;
}

const RETRY: FailureAction = { id: "retry", label: "Try again" };

/** The buttons of a failure row. `labels` maps a fix to its button text (`FIX_LABELS`). */
export function failureActions(job: JobInfo, space: SpaceNeed | null, labels: Record<FixAction, string>): FailureActions {
  if (space) {
    if (space.enough) return { primary: RETRY };
    return { primary: { id: "fix", fix: "freeSpace", label: labels.freeSpace }, secondary: RETRY };
  }
  const fix = job.error?.fix;
  if (!fix || fix === "retry") return { primary: RETRY };
  return { primary: { id: "fix", fix, label: labels[fix] } };
}

/**
 * Updates the bulk "Update N games" button may count: pending updates minus the ones whose update is in "Couldn't finish"
 * (they have their own "Try again", and `library.updateAll` leaves them alone).
 */
export function updatesToOffer(updatesAvailable: number, failed: readonly JobInfo[]): number {
  const failedUpdates = new Set(failed.filter((j) => j.kind === "update").map((j) => j.appName)).size;
  return Math.max(0, updatesAvailable - failedUpdates);
}

/** Whether any failure is about space, so the page keeps the drive's free space fresh while it shows one. */
export const hasSpaceFailure = (failed: readonly JobInfo[]): boolean => failed.some((j) => j.error?.code === "insufficientSpace" && j.error.requiredBytes != null);
