import type { AppSnapshot, JobKind, JobPhase } from "../ipc/contract.gen";
import { jobForGame } from "./store";

export const JOB_VERB: Record<JobKind, string> = {
  install: "Installing",
  update: "Updating",
  repair: "Repairing",
  verify: "Verifying",
  move: "Moving",
  uninstall: "Uninstalling",
  import: "Importing",
  modify: "Changing",
  overlay: "Installing overlay",
};

/** Imperative, for things that haven't finished ("Update Fortnite"). */
export const JOB_ACTION: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verify",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
  modify: "Change components of",
  overlay: "Install",
};

/** Past tense, for finished history entries ("Updated Fortnite"). */
export const JOB_PAST: Record<JobKind, string> = {
  install: "Installed",
  update: "Updated",
  repair: "Repaired",
  verify: "Verified",
  move: "Moved",
  uninstall: "Uninstalled",
  import: "Imported",
  modify: "Changed components of",
  overlay: "Installed",
};

export const JOB_NOUN: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verification",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
  modify: "Change",
  overlay: "Overlay install",
};

/** The verb for what a job is doing right now: a verify that moved on to repairing says so. */
export function jobVerb(kind: JobKind, phase?: JobPhase | null): string {
  if ((kind === "verify" || kind === "repair") && phase === "repairing") return "Repairing";
  if (kind === "repair" && phase === "verifying") return "Verifying";
  return JOB_VERB[kind];
}

export const PHASE_LABEL: Record<JobPhase, string> = {
  preparing: "Preparing",
  verifying: "Verifying files",
  repairing: "Repairing files",
  downloading: "Downloading",
  installing: "Installing",
  finalizing: "Finalizing",
  moving: "Moving files",
  removing: "Removing files",
  importing: "Importing",
};

/** Anything that blocks a new write operation on this game. */
export function isBusy(s: AppSnapshot, appName: string): boolean {
  return jobForGame(s.queue.jobs, appName) !== undefined || s.sessions.some((x) => x.appName === appName);
}
