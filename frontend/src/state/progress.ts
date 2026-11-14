import { create } from "zustand";
import type { JobProgress, JobSample } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";

/**
 * Progress lives apart from the snapshot so 10 Hz events never re-render the app, and is keyed by jobId:
 *  - `latest` (zustand): the newest JobProgress per job; components select exactly one job's entry.
 *  - history: ≈1 Hz `JobSample`s per job (the same shape the backend keeps for `jobs.getHistory`), in a mutable
 *    buffer outside React that sparklines read imperatively. It is seeded from `jobs.getHistory` when a job's card
 *    mounts (so graphs survive a UI reload) and then extended from the live events.
 */

/** Same cap as the backend's ring (≤ 600 points). */
export const HISTORY_CAPACITY = 600;

export type HistoryChannel = "network" | "disk";

class Track {
  samples: JobSample[] = [];
  /** `t` of the newest sample; live events add at most one sample per second of job time. */
  private lastT = -1;
  private seeded = false;
  private readonly listeners = new Set<() => void>();

  private trim(): void {
    if (this.samples.length > HISTORY_CAPACITY) this.samples.splice(0, this.samples.length - HISTORY_CAPACITY);
  }

  /** Live: one sample per whole second of elapsed job time. */
  ingest(p: JobProgress): void {
    const t = Math.floor(p.elapsedSeconds);
    if (t <= this.lastT) return;
    this.lastT = t;
    this.samples.push({
      t,
      networkBytesPerSecond: p.network?.bytesPerSecond ?? 0,
      diskBytesPerSecond: p.disk?.bytesPerSecond ?? p.verify?.readBytesPerSecond ?? 0,
    });
    this.trim();
    this.notify();
  }

  /** Merge the backend's history: its samples first, then any live ones newer than its last. */
  seed(server: readonly JobSample[]): void {
    const newest = server.at(-1)?.t ?? -1;
    const live = this.samples.filter((s) => s.t > newest);
    this.samples = [...server, ...live];
    this.lastT = Math.max(this.lastT, newest);
    this.seeded = true;
    this.trim();
    this.notify();
  }

  get isSeeded(): boolean {
    return this.seeded;
  }

  read(channel: HistoryChannel): Float32Array {
    const out = new Float32Array(this.samples.length);
    for (let i = 0; i < out.length; i++) {
      const s = this.samples[i]!;
      out[i] = channel === "network" ? s.networkBytesPerSecond : s.diskBytesPerSecond;
    }
    return out;
  }

  private notify(): void {
    this.listeners.forEach((l) => l());
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
  track(p.jobId).ingest(p);
  useProgress.setState((s) => ({ latest: { ...s.latest, [p.jobId]: p } }));
}

/** Seed `latest` from the progress the snapshot carried, for jobs we have not heard a live event for yet. */
export function seedProgress(progress: readonly (JobProgress | null | undefined)[]): void {
  const missing = progress.filter((p): p is JobProgress => !!p && useProgress.getState().latest[p.jobId] === undefined);
  if (missing.length === 0) return;
  useProgress.setState((s) => {
    const latest = { ...s.latest };
    for (const p of missing) latest[p.jobId] ??= p;
    return { latest };
  });
}

/** Chronological values of one channel (bytes/s), oldest first; 1 sample ≈ 1 second. */
export const readHistory = (jobId: string, channel: HistoryChannel = "network"): Float32Array => track(jobId).read(channel);
export const subscribeHistory = (jobId: string, notify: () => void): (() => void) => track(jobId).subscribe(notify);
export const historySamples = (jobId: string): readonly JobSample[] => track(jobId).samples;

/** Load the backend's history for a job once (idempotent). Failures are silent: the live feed still fills the chart. */
export async function loadHistory(jobId: string): Promise<void> {
  const t = track(jobId);
  if (t.isSeeded) return;
  try {
    const h = await rpc().call("jobs.getHistory", { jobId });
    t.seed(h.samples);
  } catch {
    /* the job may have been dismissed meanwhile */
  }
}

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
