import { create } from "zustand";
import type { EglInstall, JobInfo } from "../../ipc/contract.gen";
import { suppressNoticesWhen } from "../../state/notices";
import { isActiveJob, useApp } from "../../state/store";

/**
 * The import batch the person started (onboarding or Settings). It lives in a store, not in component state, for two
 * reasons: closing the Settings dialog must not lose the progress, and the snapshot only keeps the 20 newest finished
 * jobs, so a 40-game import would otherwise forget its first results.
 */
export interface BatchItem {
  install: EglInstall;
  /** null when the backend refused this one (it never became a job). */
  jobId: string | null;
}

interface BatchStore {
  items: BatchItem[];
  /** Last known state of every batch job that finished, by job id. */
  finished: Record<string, JobInfo>;
  /** How many import lists are on screen. While one is, its games' notices are not toasted (the list shows them). */
  viewers: number;
  attach: () => () => void;
  start: (items: BatchItem[]) => void;
  /** Replace one item's job (retry). */
  restart: (installId: string, jobId: string) => void;
  clear: () => void;
}

export const useImportBatch = create<BatchStore>((set) => ({
  items: [],
  finished: {},
  viewers: 0,
  attach: () => {
    useImportBatch.setState((s) => ({ viewers: s.viewers + 1 }));
    return () => useImportBatch.setState((s) => ({ viewers: Math.max(0, s.viewers - 1) }));
  },
  start: (items) => set((s) => ({ items: [...s.items.filter((x) => !items.some((n) => n.install.id === x.install.id)), ...items] })),
  restart: (installId, jobId) =>
    set((s) => {
      const old = s.items.find((x) => x.install.id === installId)?.jobId;
      const finished = { ...s.finished };
      if (old) delete finished[old];
      return { items: s.items.map((x) => (x.install.id === installId ? { ...x, jobId } : x)), finished };
    }),
  clear: () => set({ items: [], finished: {} }),
}));

let wired = false;

/** Record finished batch jobs as snapshots arrive. Idempotent; called once from the session hook. */
export function wireImportBatch(): void {
  if (wired) return;
  wired = true;
  suppressNoticesWhen((n) => {
    const { viewers, items } = useImportBatch.getState();
    return viewers > 0 && n.jobId !== null && items.some((i) => i.jobId === n.jobId);
  });
  useApp.subscribe((state, prev) => {
    const jobs = state.snapshot?.queue.jobs;
    if (!jobs || jobs === prev.snapshot?.queue.jobs) return;
    const { items, finished } = useImportBatch.getState();
    if (items.length === 0) return;
    const ids = new Set(items.map((i) => i.jobId).filter((x): x is string => !!x));
    let next: Record<string, JobInfo> | null = null;
    for (const j of jobs) {
      if (ids.has(j.id) && !isActiveJob(j) && finished[j.id] !== j) (next ??= { ...finished })[j.id] = j;
    }
    if (next) useImportBatch.setState({ finished: next });
  });
}
