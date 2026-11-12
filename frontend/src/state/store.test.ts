import { beforeEach, describe, expect, it } from "vitest";
import type { AppSnapshot } from "../ipc/contract.gen";
import { snapshotFor } from "../test/fixtures";
import { HISTORY_CAPACITY, ingestProgress, pruneProgress, readHistory, resetProgressForTests, subscribeHistory, useProgress } from "./progress";
import { share } from "./share";
import { currentJob, historyJobs, queuedJobs, useApp } from "./store";

describe("share (structural sharing)", () => {
  it("returns the previous reference where nothing changed", () => {
    const a = { x: { y: 1 }, list: [{ id: 1 }, { id: 2 }], z: "s" };
    const b = { x: { y: 1 }, list: [{ id: 1 }, { id: 2 }], z: "s" };
    expect(share(a, b)).toBe(a);
  });
  it("rebuilds only the branches that changed", () => {
    const a = { game: { v: 1 }, jobs: [{ id: "a", p: 1 }, { id: "b", p: 5 }], settings: { t: "dark" } };
    const b = { game: { v: 1 }, jobs: [{ id: "a", p: 1 }, { id: "b", p: 6 }], settings: { t: "dark" } };
    const out = share(a, b);
    expect(out).not.toBe(a);
    expect(out.game).toBe(a.game);
    expect(out.settings).toBe(a.settings);
    expect(out.jobs[0]).toBe(a.jobs[0]);
    expect(out.jobs[1]).toEqual({ id: "b", p: 6 });
  });
  it("handles type changes and nulls", () => {
    expect(share(null, { a: 1 })).toEqual({ a: 1 });
    expect(share({ a: 1 }, null)).toBeNull();
    expect(share([1], { a: 1 })).toEqual({ a: 1 });
    expect(share({ a: 1 }, [1])).toEqual([1]);
  });
});

describe("app store", () => {
  beforeEach(() => useApp.setState({ snapshot: null }));

  it("replaces the snapshot wholesale but keeps identity for unchanged slices", async () => {
    const first = await snapshotFor("installed");
    useApp.getState().setSnapshot(first);
    const gameRef = useApp.getState().snapshot!.game;
    const eglRef = useApp.getState().snapshot!.egl;

    const next: AppSnapshot = structuredClone(first);
    next.settings.theme = "dark";
    useApp.getState().setSnapshot(next);

    const s = useApp.getState().snapshot!;
    expect(s.settings.theme).toBe("dark");
    expect(s.game).toBe(gameRef);
    expect(s.egl).toBe(eglRef);
  });

  it("applies optimistic patches that the next snapshot overwrites", async () => {
    const first = await snapshotFor("installed");
    useApp.getState().setSnapshot(first);
    useApp.getState().patch((s) => ({ ...s, settings: { ...s.settings, closeToTray: !s.settings.closeToTray } }));
    expect(useApp.getState().snapshot!.settings.closeToTray).toBe(!first.settings.closeToTray);
    useApp.getState().setSnapshot(first);
    expect(useApp.getState().snapshot!.settings.closeToTray).toBe(first.settings.closeToTray);
  });
});

describe("job helpers", () => {
  it("picks the running job, then queued ones in creation order, then history newest-first", async () => {
    const snap = await snapshotFor("downloading");
    const running = currentJob(snap.jobs);
    expect(running?.status).toBe("running");
    expect(queuedJobs(snap.jobs, running)).toHaveLength(0);
    const history = historyJobs(snap.jobs);
    expect(history.every((j) => ["completed", "failed", "cancelled"].includes(j.status))).toBe(true);
  });
});

describe("progress store", () => {
  beforeEach(() => resetProgressForTests());
  const sample = (jobId: string, n: number) => ({
    jobId,
    phase: "downloading" as const,
    percent: n,
    downloadedBytes: n,
    totalDownloadBytes: 1000,
    writtenBytes: n,
    totalWriteBytes: 1000,
    downloadBytesPerSecond: n,
    diskBytesPerSecond: n * 2,
    rawDownloadBytesPerSecond: n * 10,
    etaSeconds: 10,
    elapsedSeconds: n,
  });

  it("keeps the latest value per job and a chronological raw-speed history", () => {
    for (let i = 1; i <= 5; i++) ingestProgress(sample("j1", i));
    expect(useProgress.getState().latest["j1"]?.percent).toBe(5);
    expect(Array.from(readHistory("j1"))).toEqual([10, 20, 30, 40, 50]);
  });

  it("charts disk speed outside the downloading phase", () => {
    ingestProgress({ ...sample("j2", 4), phase: "verifying" });
    expect(Array.from(readHistory("j2"))).toEqual([8]);
  });

  it("wraps as a ring buffer without losing order", () => {
    for (let i = 1; i <= HISTORY_CAPACITY + 25; i++) ingestProgress(sample("j3", i));
    const h = readHistory("j3");
    expect(h.length).toBe(HISTORY_CAPACITY);
    expect(h[h.length - 1]).toBe((HISTORY_CAPACITY + 25) * 10);
    expect(h[0]).toBe(26 * 10);
  });

  it("notifies history subscribers and prunes finished jobs", () => {
    let calls = 0;
    const off = subscribeHistory("j4", () => calls++);
    ingestProgress(sample("j4", 1));
    ingestProgress(sample("j4", 2));
    off();
    ingestProgress(sample("j4", 3));
    expect(calls).toBe(2);
    pruneProgress(new Set());
    expect(useProgress.getState().latest["j4"]).toBeUndefined();
    expect(readHistory("j4").length).toBe(0);
  });
});
