import { describe, expect, it } from "vitest";
import type { JobInfo, JobProgress } from "../ipc/contract.gen";
import { cdnName, describeLimiter, figuresOf, metaOf, stateOf } from "./transfer";

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
    ...over,
  };
}

const rows = (g: ReturnType<typeof figuresOf>) => Object.fromEntries(g.groups.map((x) => [x.id, x.rows.map((r) => r.label)]));

describe("figuresOf", () => {
  it("downloading: network figures, grouped, with a chart and a done-of-total headline", () => {
    const f = figuresOf(job(), progress());
    expect(f.headline).toBe("2.41 GB of 5.12 GB");
    expect(f.chart).toBe("network+disk");
    expect(rows(f)).toEqual({
      progress: ["Files"],
      speed: ["Now", "Average", "Peak", "Server"],
      time: ["Elapsed"],
      disk: ["Write speed", "Written", "Final size"],
    });
    expect(f.groups.find((g) => g.id === "speed")!.rows.find((r) => r.key === "cdn")!.value).toBe("Akamai");
  });

  it("never repeats a fact: no mode row, no time-left row, written is not 'of total'", () => {
    const f = figuresOf(job(), progress());
    const labels = f.groups.flatMap((g) => g.rows.map((r) => r.label));
    expect(labels).not.toContain("Mode");
    expect(labels).not.toContain("Left");
    expect(f.groups.find((g) => g.id === "disk")!.rows.find((r) => r.key === "written")!.value).toBe("3.00 GB");
  });

  it("verifying: local reads only, never a network or write figure, no chart", () => {
    const p = progress({
      phase: "verifying",
      network: null,
      disk: null,
      verify: { filesChecked: 605, filesTotal: 1317, bytesChecked: 3.55 * GB, bytesTotal: 7.72 * GB, readBytesPerSecond: 1.8 * GB, badFiles: 0, fixedFiles: 0 },
      filesDone: 605,
      filesTotal: 1317,
    });
    const f = figuresOf(job({ kind: "verify", phase: "verifying" }), p);
    expect(f.headline).toBe("3.55 GB of 7.72 GB checked");
    expect(f.chart).toBeNull();
    const labels = f.groups.flatMap((g) => g.rows.map((r) => r.label));
    expect(labels).toEqual(["Files checked", "Read speed", "Elapsed"]);
    expect(labels).not.toContain("Damaged"); // only when something is damaged
  });

  it("repairing: damaged and fixed counts appear, with the network figures for just those files", () => {
    const p = progress({
      phase: "repairing",
      verify: { filesChecked: 1317, filesTotal: 1317, bytesChecked: 7.72 * GB, bytesTotal: 7.72 * GB, readBytesPerSecond: 0, badFiles: 12, fixedFiles: 5 },
    });
    const f = figuresOf(job({ kind: "verify", phase: "repairing" }), p);
    expect(rows(f).progress).toEqual(["Files", "Damaged", "Fixed"]);
    expect(f.groups[0]!.rows.find((r) => r.key === "fixed")!.value).toBe("5 of 12");
    expect(metaOf(job({ kind: "verify", phase: "repairing" }), p)).toBe("12 files to fix · 5.1 GB");
  });

  it("installing: disk write figures only, chart on; moving: no chart", () => {
    const tail = progress({ phase: "installing", network: null, limiter: "disk" });
    const f = figuresOf(job({ phase: "installing" }), tail);
    expect(f.chart).toBe("disk");
    expect(rows(f).speed).toEqual(["Now", "Average", "Peak"]);
    expect(f.groups.flatMap((g) => g.rows.map((r) => r.label))).not.toContain("Server");
    const mv = figuresOf(job({ kind: "move", phase: "moving" }), progress({ phase: "moving", network: null }));
    expect(mv.chart).toBeNull();
    expect(mv.headline).toBe("3.00 GB of 12.00 GB");
  });

  it("hides rows with nothing to say: a paused job drops its live speed, and no row ever reads a dash", () => {
    const f = figuresOf(job({ status: "paused" }), progress());
    const speed = f.groups.find((g) => g.id === "speed")!.rows.map((r) => r.label);
    expect(speed).not.toContain("Now");
    expect(f.groups.flatMap((g) => g.rows).some((r) => r.value === "—")).toBe(false);
    const idle = figuresOf(job(), progress({ network: { ...progress().network!, averageBytesPerSecond: 0, peakBytesPerSecond: 0, cdn: null } }));
    expect(idle.groups.find((g) => g.id === "speed")!.rows.map((r) => r.label)).toEqual(["Now"]);
  });

  it("timed steps have no groups of their own", () => {
    const f = figuresOf(job({ phase: "preparing" }), progress({ phase: "preparing", network: null, disk: null, filesDone: null, filesTotal: null }));
    expect(f.groups).toEqual([]);
    expect(f.headline).toBeNull();
    expect(f.chart).toBeNull();
  });
});

describe("describeLimiter", () => {
  const ctx = { speedLimitBytesPerSecond: 20 * MB, path: "D:\\Epic Games" };
  it("is silent when the network is the limit (the normal case) or the job is paused", () => {
    expect(describeLimiter(progress({ limiter: "network" }), "network", ctx)).toBeNull();
    expect(describeLimiter(progress({ limiter: "paused" }), "network", ctx)).toBeNull();
    expect(describeLimiter(progress({ limiter: "unknown" }), "network", ctx)).toBeNull();
  });
  it("names the drive when the disk holds a download back, in one short line", () => {
    const n = describeLimiter(progress({ limiter: "disk", disk: { ...progress().disk!, averageBytesPerSecond: 8 * MB } }), "network", ctx)!;
    expect(n.text).toBe("Disk is the limit: drive D: writes about 8 MB/s.");
    expect(n.tone).toBe("warning");
  });
  it("says nothing about disk for verify reads or the install tail", () => {
    expect(describeLimiter(progress({ limiter: "disk", network: null }), "write", ctx)).toBeNull();
    expect(describeLimiter(progress({ limiter: "disk", network: null }), "read", ctx)).toBeNull();
  });
  it("explains a speed limit, a mode, the server and the processor", () => {
    expect(describeLimiter(progress({ limiter: "speedLimit" }), "network", ctx)!.text).toBe("Held to your 20 MB/s limit.");
    expect(describeLimiter(progress({ limiter: "mode", mode: "background" }), "network", ctx)!.text).toMatch(/^Background mode/);
    expect(describeLimiter(progress({ limiter: "cdn" }), "network", ctx)!.text).toBe("Akamai is responding slowly right now.");
    expect(describeLimiter(progress({ limiter: "cpu" }), "network", ctx)!.tone).toBe("warning");
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
