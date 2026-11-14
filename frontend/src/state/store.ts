import { create } from "zustand";
import type { AppSnapshot, JobInfo, WindowInfo } from "../ipc/contract.gen";
import { share } from "./share";

/**
 * Mirror of the backend's latest `state` snapshot. The UI never derives backend state itself: it
 * replaces the snapshot wholesale (with structural sharing so unchanged branches keep identity)
 * and selects narrow slices. Optimistic patches are overwritten by the next real snapshot.
 */
export type MaxButtonState = "idle" | "hover" | "down";

interface AppStore {
  snapshot: AppSnapshot | null;
  window: WindowInfo;
  /** The native Snap Layouts overlay swallows the maximize button's mouse events; the host reports them instead. */
  maxButton: MaxButtonState;
  setMaxButton: (s: MaxButtonState) => void;
  setSnapshot: (next: AppSnapshot) => void;
  /** Optimistic local edit, replaced by the backend's next snapshot. */
  patch: (fn: (s: AppSnapshot) => AppSnapshot) => void;
  setWindow: (w: WindowInfo) => void;
}

/** Dev-only (mock mode): lets the stress toggle rewrite every incoming snapshot. */
let transform: ((s: AppSnapshot) => AppSnapshot) | null = null;
export const setSnapshotTransform = (fn: typeof transform): void => void (transform = fn);

export const useApp = create<AppStore>((set) => ({
  snapshot: null,
  window: { maximized: false, focused: true, visible: true },
  maxButton: "idle",
  setMaxButton: (maxButton) => set((s) => (s.maxButton === maxButton ? s : { maxButton })),
  setSnapshot: (raw) =>
    set((s) => {
      const next = transform ? transform(raw) : raw;
      return { snapshot: s.snapshot ? share(s.snapshot, next) : next };
    }),
  patch: (fn) => set((s) => (s.snapshot ? { snapshot: share(s.snapshot, fn(s.snapshot)) } : s)),
  setWindow: (window) =>
    set((s) => (s.window.maximized === window.maximized && s.window.focused === window.focused && s.window.visible === window.visible ? s : { window })),
}));

/** Select from a snapshot that is known to exist (the shell renders only after boot). */
export function useSnap<T>(select: (s: AppSnapshot) => T): T {
  return useApp((s) => select(s.snapshot as AppSnapshot));
}

export const getSnapshot = (): AppSnapshot | null => useApp.getState().snapshot;

// ----- Job helpers (pure) -----
// `queue.jobs` is already in run order: unfinished jobs first (running first), then finished ones, newest first.

export const ACTIVE_STATUSES = ["running", "paused", "waiting", "queued"] as const;
export const isActiveJob = (j: JobInfo): boolean => (ACTIVE_STATUSES as readonly string[]).includes(j.status);

/** Unfinished jobs in run order. */
export const activeJobs = (jobs: readonly JobInfo[]): JobInfo[] => jobs.filter(isActiveJob);

/** The job the user cares about right now: the running one, else the first parked/queued one. */
export function currentJob(jobs: readonly JobInfo[]): JobInfo | undefined {
  const active = activeJobs(jobs);
  return active.find((j) => j.status === "running") ?? active[0];
}

/** Unfinished jobs after the current one, in run order. */
export const queuedJobs = (jobs: readonly JobInfo[], current?: JobInfo): JobInfo[] => activeJobs(jobs).filter((j) => j.id !== current?.id);

/** The unfinished job of one game (the running one if there are several). */
export function jobForGame(jobs: readonly JobInfo[], appName: string): JobInfo | undefined {
  const mine = activeJobs(jobs).filter((j) => j.appName === appName);
  return mine.find((j) => j.status === "running") ?? mine[0];
}

/** Finished jobs, newest first. */
export const historyJobs = (jobs: readonly JobInfo[]): JobInfo[] => jobs.filter((j) => !isActiveJob(j));
