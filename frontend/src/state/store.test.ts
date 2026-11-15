import { beforeEach, describe, expect, it } from "vitest";
import type { AppSnapshot, JobProgress } from "../ipc/contract.gen";
import { setRpcForTests } from "../ipc/rpc";
import { mockClient, snapshotFor } from "../test/fixtures";
import {
  HISTORY_CAPACITY,
  historySamples,
  ingestProgress,
  loadHistory,
  pruneProgress,
  readHistory,
  resetProgressForTests,
  seedProgress,
  subscribeHistory,
  useProgress,
} from "./progress";
import { share } from "./share";
import { activeJobs, currentJob, historyJobs, jobForGame, queuedJobs, useApp } from "./store";

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
    const first = await snapshotFor("returning");
    useApp.getState().setSnapshot(first);
    const queueRef = useApp.getState().snapshot!.queue;
    const eglRef = useApp.getState().snapshot!.egl;

    const next: AppSnapshot = structuredClone(first);
    next.settings.theme = "dark";
    useApp.getState().setSnapshot(next);

    const s = useApp.getState().snapshot!;
    expect(s.settings.theme).toBe("dark");
    expect(s.queue).toBe(queueRef);
    expect(s.egl).toBe(eglRef);
  });

  it("applies optimistic patches that the next snapshot overwrites", async () => {
    const first = await snapshotFor("returning");
    useApp.getState().setSnapshot(first);
    useApp.getState().patch((s) => ({ ...s, settings: { ...s.settings, closeToTray: !s.settings.closeToTray } }));
    expect(useApp.getState().snapshot!.settings.closeToTray).toBe(!first.settings.closeToTray);
    useApp.getState().setSnapshot(first);
    expect(useApp.getState().snapshot!.settings.closeToTray).toBe(first.settings.closeToTray);
  });
});

describe("job helpers", () => {
  it("picks the running job first, then queued ones in run order, then history newest-first", async () => {
    const snap = await snapshotFor("queue");
    const jobs = snap.queue.jobs;
    const running = currentJob(jobs);
    expect(running?.status).toBe("running");
    const rest = queuedJobs(jobs, running);
    expect(rest.map((j) => j.status)).toEqual(["paused", "queued", "queued", "queued"]);
    expect(rest.map((j) => j.id)).toEqual(activeJobs(jobs).slice(1).map((j) => j.id));
    const history = historyJobs(jobs);
    expect(history.length).toBeGreaterThan(5);
    expect(history.every((j) => ["completed", "failed", "cancelled"].includes(j.status))).toBe(true);
    const finishedAt = history.map((j) => Date.parse(j.finishedAt!));
    expect(finishedAt).toEqual([...finishedAt].sort((a, b) => b - a));
  });

  it("finds the unfinished job of one game, preferring the running one", async () => {
    const snap = await snapshotFor("queue");
    const running = snap.queue.jobs.find((j) => j.status === "running")!;
    expect(jobForGame(snap.queue.jobs, running.appName)?.id).toBe(running.id);
    expect(jobForGame(snap.queue.jobs, "nobody")).toBeUndefined();
    const extra = { ...running, id: "later", status: "queued" as const };
    expect(jobForGame([extra, running], running.appName)?.id).toBe(running.id);
  });

  it("with nothing running, the first parked/queued job is current", async () => {
    const snap = await snapshotFor("queue");
    const parked = snap.queue.jobs.filter((j) => j.status !== "running");
    expect(currentJob(parked)?.id).toBe(activeJobs(parked)[0]!.id);
    expect(currentJob([])).toBeUndefined();
  });
});

describe("progress store (keyed by job, with ≈1 Hz history)", () => {
  beforeEach(() => resetProgressForTests());
  const sample = (jobId: string, seconds: number, net = 100, disk = 200): JobProgress => ({
    jobId,
    appName: "Game",
    phase: "downloading",
    percent: seconds,
    phasePercent: seconds,
    elapsedSeconds: seconds,
    etaSeconds: 10,
    network: { downloadedBytes: 1, totalBytes: 10, bytesPerSecond: net, averageBytesPerSecond: net, peakBytesPerSecond: net, cdn: "cdn", connections: 8, ceilingBytesPerSecond: null },
    disk: { writtenBytes: 1, totalBytes: 10, bytesPerSecond: disk, averageBytesPerSecond: disk, peakBytesPerSecond: disk, installSizeBytes: 10 },
    verify: null,
    filesDone: null,
    filesTotal: null,
    mode: "balanced",
    limiter: "network",
    limiterReason: null,
  });

  it("keeps the latest value per job, separately for each job", () => {
    ingestProgress(sample("a", 3));
    ingestProgress(sample("b", 7));
    expect(useProgress.getState().latest["a"]?.percent).toBe(3);
    expect(useProgress.getState().latest["b"]?.percent).toBe(7);
    expect(Object.keys(useProgress.getState().latest).sort()).toEqual(["a", "b"]);
  });

  it("samples history at ≈1 Hz of job time, not at the event rate (10 Hz)", () => {
    for (let tick = 0; tick < 50; tick++) ingestProgress(sample("j1", tick / 10 + 1, 100 + tick, 200 + tick));
    // 50 events spanning t = 1.0 … 5.9 s → one sample per whole second (1, 2, 3, 4, 5).
    expect(Array.from(readHistory("j1", "network"))).toEqual([100, 110, 120, 130, 140]);
    expect(Array.from(readHistory("j1", "disk"))).toEqual([200, 210, 220, 230, 240]);
    expect(historySamples("j1").map((x) => x.t)).toEqual([1, 2, 3, 4, 5]);
  });

  it("charts disk read speed while verifying (no network figures), zero when neither applies", () => {
    ingestProgress({
      ...sample("v", 1),
      phase: "verifying",
      network: null,
      disk: null,
      verify: { filesChecked: 1, filesTotal: 2, bytesChecked: 1, bytesTotal: 2, readBytesPerSecond: 1_800_000_000, badFiles: 0, fixedFiles: 0 },
    });
    ingestProgress({ ...sample("v", 2), phase: "finalizing", network: null, disk: null, verify: null });
    expect(Array.from(readHistory("v", "network"))).toEqual([0, 0]);
    expect(Array.from(readHistory("v", "disk"))).toEqual([1_800_000_000, 0]);
  });

  it("is a ring: at most 600 samples, oldest dropped, order kept", () => {
    for (let i = 1; i <= HISTORY_CAPACITY + 25; i++) ingestProgress(sample("j3", i, i));
    const h = readHistory("j3");
    expect(h.length).toBe(HISTORY_CAPACITY);
    expect(h[h.length - 1]).toBe(HISTORY_CAPACITY + 25);
    expect(h[0]).toBe(26);
  });

  it("notifies history subscribers once per new sample and prunes finished jobs", () => {
    let calls = 0;
    const off = subscribeHistory("j4", () => calls++);
    ingestProgress(sample("j4", 1));
    ingestProgress(sample("j4", 1.5)); // same second: no new sample
    ingestProgress(sample("j4", 2));
    off();
    ingestProgress(sample("j4", 3));
    expect(calls).toBe(2);
    pruneProgress(new Set());
    expect(useProgress.getState().latest["j4"]).toBeUndefined();
    expect(readHistory("j4").length).toBe(0);
  });

  it("seeds from the backend's history, then continues with live samples (graphs survive a UI reload)", async () => {
    const { client } = mockClient("downloading");
    setRpcForTests(client);
    try {
      const snap = await client.call("app.getSnapshot");
      const job = snap.queue.jobs.find((j) => j.status === "running")!;
      await loadHistory(job.id);
      const seeded = [...historySamples(job.id)];
      expect(seeded.length).toBeGreaterThan(5);
      expect(seeded.every((s, i) => i === 0 || s.t === seeded[i - 1]!.t + 1)).toBe(true);
      const lastT = seeded.at(-1)!.t;
      ingestProgress({ ...sample(job.id, lastT + 1, 77) });
      ingestProgress({ ...sample(job.id, lastT, 5) }); // not newer: ignored
      const after = historySamples(job.id);
      expect(after.length).toBe(seeded.length + 1);
      expect(after.at(-1)).toMatchObject({ t: lastT + 1, networkBytesPerSecond: 77 });
      await loadHistory(job.id); // idempotent
      expect(historySamples(job.id).length).toBe(after.length);
    } finally {
      setRpcForTests(null);
    }
  });

  it("seedProgress fills in jobs we have not heard a live event for, never overwriting a live one", () => {
    ingestProgress(sample("live", 4));
    seedProgress([sample("live", 1), sample("cold", 9), null]);
    expect(useProgress.getState().latest["live"]?.percent).toBe(4);
    expect(useProgress.getState().latest["cold"]?.percent).toBe(9);
  });
});
