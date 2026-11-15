import { describe, expect, it } from "vitest";
import type { JobInfo, JobProgress } from "../ipc/contract.gen";
import { cdnName, explainSpeed, figuresOf, headlineOf, metaOf, speedStatusOf, stateOf, timeOf, type SpeedContext } from "./transfer";

const GB = 1024 ** 3;
const MB = 1024 ** 2;

function job(over: Partial<JobInfo> = {}): JobInfo {
  return {
    id: "j1",
    appName: "Game",
    title: "Game",
    kind: "install",
    status: "running",
    phases: ["preparing", "downloading", "installing", "finalizing"],
    phase: "downloading",
    progress: null,
    error: null,
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: "2026-01-01T00:00:01Z",
    finishedAt: null,
    targetPath: "D:\\Epic Games",
    fromVersion: null,
    toVersion: "1.2.4",
    blockedReason: null,
    verify: null,
    outcome: null,
    cancellable: true,
    ...over,
  };
}

function progress(over: Partial<JobProgress> = {}): JobProgress {
  return {
    jobId: "j1",
    appName: "Game",
    phase: "downloading",
    percent: 47,
    phasePercent: 60,
    elapsedSeconds: 252,
    etaSeconds: 360,
    network: { downloadedBytes: 2.41 * GB, totalBytes: 5.12 * GB, bytesPerSecond: 48 * MB, averageBytesPerSecond: 44 * MB, peakBytesPerSecond: 91 * MB, cdn: "epicgames-download1.akamaized.net", connections: 12 },
    disk: { writtenBytes: 3 * GB, totalBytes: 12 * GB, bytesPerSecond: 30 * MB, averageBytesPerSecond: 28 * MB, peakBytesPerSecond: 50 * MB, installSizeBytes: 12 * GB },
    verify: null,
    filesDone: 1204,
    filesTotal: 3310,
    mode: "balanced",
    limiter: "network",
    limiterReason: null,
    ...over,
  };
}

const labels = (f: ReturnType<typeof figuresOf>) => f.readouts.map((r) => r.label);

describe("figuresOf", () => {
  it("downloading: bytes named as downloaded, two readouts that are the chart's legend, a chart", () => {
    const f = figuresOf(job(), progress());
    expect(f.headline).toBe("2.41 GB of 5.12 GB");
    expect(headlineOf(f)).toBe("2.41 GB of 5.12 GB downloaded");
    expect(f.chart).toBe("network+disk");
    expect(f.readouts).toEqual([
      { key: "network", label: "Download", bytesPerSecond: 48 * MB, average: false, series: "network" },
      { key: "disk", label: "Disk write", bytesPerSecond: 30 * MB, average: false, series: "disk" },
    ]);
    expect(f.damaged).toBeNull();
  });

  it("has no caption facts: no file counts, on-disk size, server or elapsed in the figures", () => {
    const f = figuresOf(job(), progress());
    expect(Object.keys(f).sort()).toEqual(["chart", "damaged", "headline", "readouts", "throughput", "unit"]);
    expect(JSON.stringify(f)).not.toMatch(/Akamai|elapsed|files/i);
  });

  it("verifying: one 'Reading' readout, no chart, no network or write figure, damaged only once something is", () => {
    const base = {
      phase: "verifying" as const,
      network: null,
      disk: null,
      filesDone: 605,
      filesTotal: 1317,
    };
    const clean = figuresOf(job({ kind: "verify", phase: "verifying" }), progress({ ...base, verify: { filesChecked: 605, filesTotal: 1317, bytesChecked: 3.55 * GB, bytesTotal: 7.72 * GB, readBytesPerSecond: 1.8 * GB, badFiles: 0, fixedFiles: 0 } }));
    expect(headlineOf(clean)).toBe("3.55 GB of 7.72 GB checked");
    expect(clean.chart).toBeNull();
    expect(labels(clean)).toEqual(["Reading"]);
    expect(clean.readouts[0]!.series).toBeNull();
    expect(clean.damaged).toBeNull();
    const found = figuresOf(job({ kind: "verify", phase: "verifying" }), progress({ ...base, verify: { filesChecked: 605, filesTotal: 1317, bytesChecked: 3.55 * GB, bytesTotal: 7.72 * GB, readBytesPerSecond: 1.8 * GB, badFiles: 3, fixedFiles: 0 } }));
    expect(found.damaged).toBe("3 damaged so far");
  });

  it("repairing: the network readouts for just those files; the damaged count lives in the header, not here", () => {
    const p = progress({
      phase: "repairing",
      verify: { filesChecked: 1317, filesTotal: 1317, bytesChecked: 7.72 * GB, bytesTotal: 7.72 * GB, readBytesPerSecond: 0, badFiles: 12, fixedFiles: 5 },
    });
    const f = figuresOf(job({ kind: "verify", phase: "repairing" }), p);
    expect(labels(f)).toEqual(["Download", "Disk write"]);
    expect(f.damaged).toBeNull();
    expect(metaOf(job({ kind: "verify", phase: "repairing" }), p)).toBe("12 files to fix · 5.1 GB");
  });

  it("installing: bytes named as written, disk write only, chart on; moving: copied, 'Copying', no chart", () => {
    const tail = progress({ phase: "installing", network: null, limiter: "disk" });
    const f = figuresOf(job({ phase: "installing" }), tail);
    expect(f.chart).toBe("disk");
    expect(labels(f)).toEqual(["Disk write"]);
    expect(headlineOf(f)).toBe("3.00 GB of 12.00 GB written");
    const mv = figuresOf(job({ kind: "move", phase: "moving" }), progress({ phase: "moving", network: null }));
    expect(mv.chart).toBeNull();
    expect(labels(mv)).toEqual(["Copying"]);
    expect(headlineOf(mv)).toBe("3.00 GB of 12.00 GB copied");
  });

  it("paused: the readouts stay, as the job's averages and named so, so the card does not jump", () => {
    const f = figuresOf(job({ status: "paused" }), progress());
    expect(f.readouts).toEqual([
      { key: "network", label: "Download, average", bytesPerSecond: 44 * MB, average: true, series: "network" },
      { key: "disk", label: "Disk write, average", bytesPerSecond: 28 * MB, average: true, series: "disk" },
    ]);
  });

  it("a speed with no value yet is left out, never '0 B/s, average'", () => {
    const empty = progress({ network: { ...progress().network!, averageBytesPerSecond: 0 }, disk: { ...progress().disk!, averageBytesPerSecond: 0 } });
    expect(figuresOf(job({ status: "paused" }), empty).readouts).toEqual([]);
  });

  it("timed steps have no readouts and no chart", () => {
    const f = figuresOf(job({ phase: "preparing" }), progress({ phase: "preparing", network: null, disk: null, filesDone: null, filesTotal: null }));
    expect(f.readouts).toEqual([]);
    expect(f.headline).toBeNull();
    expect(headlineOf(f)).toBeNull();
    expect(f.chart).toBeNull();
  });
});

describe("timeOf", () => {
  it("time left beside the percentage, elapsed in its tooltip", () => {
    expect(timeOf(job(), progress())).toEqual({ text: "6 min left", tip: "4:12 elapsed" });
  });
  it("with no estimate, or not running, the elapsed time takes the slot", () => {
    expect(timeOf(job(), progress({ etaSeconds: null }))).toEqual({ text: "4:12 elapsed", tip: null });
    expect(timeOf(job({ status: "paused" }), progress())).toEqual({ text: "4:12 elapsed", tip: null });
    expect(timeOf(job({ status: "queued" }), null)).toBeNull();
  });
});

describe("speedStatusOf", () => {
  const ctx: SpeedContext = { speedLimitBytesPerSecond: 20 * MB, path: "D:\\Epic Games", chosenMode: "balanced", effectiveMode: "balanced", modeReason: null };
  const status = (p: JobProgress, c: Partial<SpeedContext> = {}, through: "network" | "write" | "read" | null = "network", j: JobInfo = job()) => speedStatusOf(j, p, through, { ...ctx, ...c });

  it("is silent when the network is the limit (the normal case), the job is paused, or the phase has no network", () => {
    expect(status(progress({ limiter: "network" }))).toBeNull();
    expect(status(progress({ limiter: "paused" }))).toBeNull();
    expect(status(progress({ limiter: "unknown" }))).toBeNull();
    expect(status(progress({ limiter: "disk" }), {}, "network", job({ status: "paused" }))).toBeNull();
    expect(status(progress({ limiter: "disk" }), {}, null)).toBeNull();
  });

  it("a slow drive, processor or server is 'Slow download' (amber kind), with the reason in one line", () => {
    const disk = status(progress({ limiter: "disk", disk: { ...progress().disk!, averageBytesPerSecond: 8 * MB } }))!;
    expect(disk).toMatchObject({ cause: "disk", kind: "slow", label: "Slow download", setting: null });
    expect(disk.reason).toBe("Drive D: writes about 8.0 MB/s, slower than your connection.");
    expect(status(progress({ limiter: "cpu" }))).toMatchObject({ cause: "cpu", kind: "slow", label: "Slow download" });
    expect(status(progress({ limiter: "cdn" }))).toMatchObject({ cause: "cdn", kind: "slow", reason: "Akamai is responding slowly right now.", setting: "cdn" });
  });

  it("a limit the user chose is a muted label naming the cause, linked to its setting", () => {
    expect(status(progress({ limiter: "speedLimit" }))).toMatchObject({ cause: "speedLimit", kind: "limit", label: "Speed limit", reason: "Held to your 20.0 MB/s limit.", setting: "speedLimit" });
    expect(status(progress({ limiter: "mode", mode: "background" }))).toMatchObject({ cause: "mode", kind: "limit", label: "Background mode", setting: "downloadMode" });
  });

  it("says nothing about disk for verify reads or the install tail", () => {
    expect(status(progress({ limiter: "disk", network: null }), {}, "write")).toBeNull();
    expect(status(progress({ limiter: "disk", network: null }), {}, "read")).toBeNull();
  });

  it("priority: a throttle Glider applied on its own beats the disk, the disk beats the processor, the speed limit, the mode and the server", () => {
    const throttle = { effectiveMode: "background" as const, modeReason: "Background mode while Fortnite is running" };
    expect(status(progress({ limiter: "disk" }), throttle)).toMatchObject({ cause: "playing", kind: "limit", label: "Throttled while playing", reason: "Background mode while Fortnite is running.", setting: "whilePlaying" });
    // Idle boost only adds speed: it is not a limit and shows nothing by itself.
    expect(status(progress({ limiter: "network" }), { effectiveMode: "full", modeReason: "Full speed while the PC is idle" })).toBeNull();
    expect(status(progress({ limiter: "disk" }))!.cause).toBe("disk");
    expect(status(progress({ limiter: "cpu" }))!.cause).toBe("cpu");
  });

  it("the throttle for a game is about the network phases only", () => {
    const throttle = { effectiveMode: "background" as const, modeReason: "Background mode while Fortnite is running" };
    expect(status(progress({ limiter: "disk", network: null }), throttle, "write")).toBeNull();
  });
});

describe("explainSpeed", () => {
  const ctx: SpeedContext = { speedLimitBytesPerSecond: 25 * MB, path: "D:\\Epic Games", chosenMode: "balanced", effectiveMode: "balanced", modeReason: null };
  const slowDisk = progress({
    limiter: "disk",
    network: { ...progress().network!, bytesPerSecond: 7 * MB, peakBytesPerSecond: 41 * MB },
    disk: { ...progress().disk!, bytesPerSecond: 7.4 * MB, averageBytesPerSecond: 7.6 * MB },
  });
  const explain = (p: JobProgress, c = ctx) => explainSpeed(speedStatusOf(job(), p, "network", c)!, p, c);

  it("a slow drive: the measured numbers (what the drive writes, what the connection has reached) and no settings link", () => {
    const e = explain(slowDisk);
    expect(e.facts).toEqual(["Drive D: writes about 7.6 MB/s.", "Glider is downloading at 7.0 MB/s, the pace the drive allows.", "Your connection has reached about 41.0 MB/s."]);
    expect(e.advice.length).toBeGreaterThan(1);
    expect(e.link).toBeNull();
  });

  it("only mentions the connection's ceiling when it is clearly above what the job gets", () => {
    const flat = progress({ limiter: "disk", network: { ...progress().network!, bytesPerSecond: 40 * MB, peakBytesPerSecond: 41 * MB } });
    expect(explain(flat).facts.join(" ")).not.toContain("connection has reached");
  });

  it("a speed limit and a mode link to their Settings rows; the server links to the server picker", () => {
    expect(explain(progress({ limiter: "speedLimit" })).link).toEqual({ focus: "speedLimit", label: "Change speed limit" });
    expect(explain(progress({ limiter: "mode", mode: "background" })).link).toEqual({ focus: "downloadMode", label: "Change download mode" });
    expect(explain(progress({ limiter: "cdn" })).link?.focus).toBe("cdn");
  });
});

describe("state and meta", () => {
  it("one state word; the kind appears only when the state does not already say it", () => {
    expect(stateOf(job(), progress()).label).toBe("Downloading");
    expect(stateOf(job({ status: "paused" }), progress()).label).toBe("Paused");
    expect(metaOf(job(), progress())).toBe("Install · 1.2.4");
    expect(metaOf(job({ kind: "update", fromVersion: "1.2.3", toVersion: "1.2.4" }), progress())).toBe("Update · 1.2.3 → 1.2.4");
    expect(metaOf(job({ kind: "verify", phase: "verifying" }), progress({ phase: "verifying" }))).toBeNull();
    expect(metaOf(job({ kind: "verify", phase: "downloading" }), progress())).toBe("Verify");
  });
  it("maps CDN hosts to providers", () => {
    expect(cdnName("fastly-download.epicgames.com")).toBe("Fastly");
    expect(cdnName("download.epicgames.com")).toBe("Epic");
    expect(cdnName("example.net")).toBe("example.net");
  });
});
