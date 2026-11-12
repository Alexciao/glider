import { create } from "zustand";
import type { AppSnapshot, JobInfo, WindowInfo } from "../ipc/contract.gen";
import { share } from "./share";

/**
 * Mirror of the backend's latest `state` snapshot. The UI never derives backend state itself: it
 * replaces the snapshot wholesale (with structural sharing so unchanged branches keep identity)
 * and selects narrow slices. Optimistic patches are overwritten by the next real snapshot.
 */
interface AppStore {
  snapshot: AppSnapshot | null;
  window: WindowInfo;
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
  window: { maximized: false, focused: true },
  setSnapshot: (raw) =>
    set((s) => {
      const next = transform ? transform(raw) : raw;
      return { snapshot: s.snapshot ? share(s.snapshot, next) : next };
    }),
  patch: (fn) => set((s) => (s.snapshot ? { snapshot: share(s.snapshot, fn(s.snapshot)) } : s)),
  setWindow: (window) => set((s) => (s.window.maximized === window.maximized && s.window.focused === window.focused ? s : { window })),
}));

/** Select from a snapshot that is known to exist (the shell renders only after boot). */
export function useSnap<T>(select: (s: AppSnapshot) => T): T {
  return useApp((s) => select(s.snapshot as AppSnapshot));
}

export const getSnapshot = (): AppSnapshot | null => useApp.getState().snapshot;

// ----- Job helpers (pure) -----

export const ACTIVE_STATUSES = ["running", "paused", "waiting", "queued"] as const;
export const isActiveJob = (j: JobInfo): boolean => (ACTIVE_STATUSES as readonly string[]).includes(j.status);

/** The job the user cares about right now: running first, then waiting, paused, queued. */
export function currentJob(jobs: readonly JobInfo[]): JobInfo | undefined {
  const rank = { running: 0, waiting: 1, paused: 2, queued: 3 } as Record<string, number>;
  return jobs
    .filter(isActiveJob)
    .sort((a, b) => (rank[a.status]! - rank[b.status]!) || a.createdAt.localeCompare(b.createdAt))[0];
}

export const queuedJobs = (jobs: readonly JobInfo[], current?: JobInfo): JobInfo[] =>
  jobs.filter((j) => isActiveJob(j) && j.id !== current?.id).sort((a, b) => a.createdAt.localeCompare(b.createdAt));

export const historyJobs = (jobs: readonly JobInfo[]): JobInfo[] =>
  jobs.filter((j) => !isActiveJob(j)).sort((a, b) => (b.finishedAt ?? b.createdAt).localeCompare(a.finishedAt ?? a.createdAt));
