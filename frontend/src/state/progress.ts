import { create } from "zustand";
import type { JobProgress } from "../ipc/contract.gen";

/**
 * 10 Hz progress lives apart from the snapshot so it never re-renders the app:
 *  - `latest` (zustand): the newest JobProgress per job; components select exactly one job's entry.
 *  - the speed history is a mutable ring buffer outside React, read imperatively by sparklines.
 */

/** 60 s of history at 10 Hz. */
export const HISTORY_CAPACITY = 600;

class Track {
  private readonly buf = new Float32Array(HISTORY_CAPACITY);
  private head = 0;
  private count = 0;
  private readonly listeners = new Set<() => void>();

  push(v: number): void {
    this.buf[this.head] = Number.isFinite(v) && v > 0 ? v : 0;
    this.head = (this.head + 1) % HISTORY_CAPACITY;
    if (this.count < HISTORY_CAPACITY) this.count++;
    this.listeners.forEach((l) => l());
  }

  /** Chronological copy, oldest first. */
  read(): Float32Array {
    const out = new Float32Array(this.count);
    const start = (this.head - this.count + HISTORY_CAPACITY) % HISTORY_CAPACITY;
    for (let i = 0; i < this.count; i++) out[i] = this.buf[(start + i) % HISTORY_CAPACITY]!;
    return out;
  }

  subscribe(l: () => void): () => void {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  }
}

const tracks = new Map<string, Track>();
const track = (jobId: string): Track => {
  let t = tracks.get(jobId);
  if (!t) tracks.set(jobId, (t = new Track()));
  return t;
};

interface ProgressStore {
  latest: Record<string, JobProgress>;
}

export const useProgress = create<ProgressStore>(() => ({ latest: {} }));

export function ingestProgress(p: JobProgress): void {
  // The chart shows what the user is waiting on: network while downloading, disk otherwise.
  track(p.jobId).push(p.phase === "downloading" ? p.rawDownloadBytesPerSecond : p.diskBytesPerSecond);
  useProgress.setState((s) => ({ latest: { ...s.latest, [p.jobId]: p } }));
}

export const readHistory = (jobId: string): Float32Array => track(jobId).read();
export const subscribeHistory = (jobId: string, notify: () => void): (() => void) => track(jobId).subscribe(notify);

/** Forget jobs that no longer exist in the snapshot. */
export function pruneProgress(liveJobIds: ReadonlySet<string>): void {
  for (const id of [...tracks.keys()]) if (!liveJobIds.has(id)) tracks.delete(id);
  const { latest } = useProgress.getState();
  const stale = Object.keys(latest).filter((id) => !liveJobIds.has(id));
  if (stale.length) {
    const next = { ...latest };
    for (const id of stale) delete next[id];
    useProgress.setState({ latest: next });
  }
}

/** Latest progress for a job, falling back to what the snapshot carried. */
export function useJobProgress(jobId: string | undefined, fallback: JobProgress | null | undefined): JobProgress | null {
  const live = useProgress((s) => (jobId ? s.latest[jobId] : undefined));
  return live ?? fallback ?? null;
}

export function resetProgressForTests(): void {
  tracks.clear();
  useProgress.setState({ latest: {} });
}
