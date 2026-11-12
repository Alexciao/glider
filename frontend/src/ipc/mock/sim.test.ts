import { describe, expect, it } from "vitest";
import { GAME_ART, heroArt, tallArt } from "./art";
import { Errors, errorForCode, formatBytes } from "./errors";
import { GiB } from "./constants";
import { ERROR_CODES } from "./options";
import { JobSim, SpeedModel, TICK_S, buildJobSim, mulberry32 } from "./sim";

describe("mulberry32", () => {
  it("is deterministic and in [0, 1)", () => {
    const a = mulberry32(5);
    const b = mulberry32(5);
    for (let i = 0; i < 100; i++) {
      const v = a();
      expect(v).toBe(b());
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(1);
    }
  });
});

describe("SpeedModel", () => {
  it("stays in a plausible range, with occasional dips", () => {
    const m = new SpeedModel(mulberry32(1), { nominal: 62e6, min: 35e6, max: 95e6, dipTo: 8e6 });
    let dips = 0;
    let max = 0;
    for (let i = 0; i < 6000; i++) {
      m.step(TICK_S);
      if (m.raw < 20e6) dips++;
      max = Math.max(max, m.raw);
      expect(m.raw).toBeGreaterThan(5e6);
      expect(m.raw).toBeLessThan(110e6);
    }
    expect(dips).toBeGreaterThan(5);
    expect(dips).toBeLessThan(1500);
    expect(max).toBeGreaterThan(62e6);
  });
});

describe("JobSim", () => {
  const install = () => buildJobSim("job-1", "install", { installBytes: 0 }, 12, false, 7);

  it("walks install phases in order and finishes at 100%", () => {
    const sim = install();
    const phases: string[] = [];
    let last = "";
    let guard = 0;
    while (!sim.done && guard++ < 20_000) {
      sim.advance(TICK_S);
      const p = sim.progress();
      if (p.phase !== last) phases.push((last = p.phase));
    }
    expect(phases).toEqual(["preparing", "downloading", "verifying", "finalizing"]);
    expect(sim.done).toBe(true);
    const p = sim.progress();
    expect(p.percent).toBe(100);
    expect(p.etaSeconds).toBe(0);
  });

  it("keeps percent in 0..100 and byte counters monotonic within the download phase", () => {
    const sim = install();
    let prev = sim.progress();
    for (let i = 0; i < 6000 && !sim.done; i++) {
      sim.advance(TICK_S);
      const p = sim.progress();
      expect(p.percent).toBeGreaterThanOrEqual(0);
      expect(p.percent).toBeLessThanOrEqual(100);
      if (p.phase === prev.phase) {
        expect(p.percent).toBeGreaterThanOrEqual(prev.percent);
        expect(p.downloadedBytes).toBeGreaterThanOrEqual(prev.downloadedBytes);
        expect(p.writtenBytes).toBeGreaterThanOrEqual(prev.writtenBytes);
      }
      expect(p.writtenBytes).toBeLessThanOrEqual(p.totalWriteBytes + 1);
      prev = p;
    }
  });

  it("writes trail downloads at the manifest ratio (~3.45x)", () => {
    const sim = install();
    sim.seek(0.5);
    for (let i = 0; i < 50; i++) sim.advance(TICK_S);
    const p = sim.progress();
    expect(p.phase).toBe("downloading");
    expect(p.writtenBytes / p.downloadedBytes).toBeGreaterThan(3.2);
    expect(p.writtenBytes / p.downloadedBytes).toBeLessThan(3.5);
    // Disk speed ≈ download speed × ratio.
    expect(p.diskBytesPerSecond / p.downloadBytesPerSecond).toBeGreaterThan(2.6);
    expect(p.diskBytesPerSecond / p.downloadBytesPerSecond).toBeLessThan(4.4);
  });

  it("ETA counts down truthfully in real seconds (within ±35%)", () => {
    const sim = install();
    sim.seek(0.25);
    sim.advance(TICK_S);
    const etaAtStart = sim.progress().etaSeconds!;
    let ticks = 0;
    while (!sim.done && ticks < 30_000) {
      sim.advance(TICK_S);
      ticks++;
    }
    const actual = ticks * TICK_S;
    expect(etaAtStart).toBeGreaterThan(actual * 0.65);
    expect(etaAtStart).toBeLessThan(actual * 1.35);
  });

  it("seek() puts the job at the requested fraction of the download", () => {
    const sim = install();
    sim.seek(0.42);
    const p = sim.progress();
    expect(p.phase).toBe("downloading");
    expect(p.percent).toBeCloseTo(42, 1);
    expect(p.downloadBytesPerSecond).toBeGreaterThan(50e6);
    expect(p.elapsedSeconds).toBeGreaterThan(10);
  });

  it("history() ends exactly at the current progress and counts up", () => {
    const sim = install();
    sim.seek(0.42);
    const h = sim.history(90, 1);
    expect(h).toHaveLength(90);
    expect(h[89]).toEqual(sim.progress());
    for (let i = 1; i < h.length; i++) {
      expect(h[i]!.downloadedBytes).toBeGreaterThanOrEqual(h[i - 1]!.downloadedBytes);
      expect(h[i]!.percent).toBeGreaterThanOrEqual(h[i - 1]!.percent);
    }
    const raws = new Set(h.map((p) => p.rawDownloadBytesPerSecond));
    expect(raws.size).toBeGreaterThan(40);
  });

  it("small jobs stay watchable (≥ ~10 s real) unless a time scale is forced", () => {
    const run = (sim: JobSim): number => {
      let ticks = 0;
      while (!sim.done && ticks < 100_000) {
        sim.advance(TICK_S);
        ticks++;
      }
      return ticks * TICK_S;
    };
    expect(run(buildJobSim("u", "update", { installBytes: 0 }, 12, false, 3))).toBeGreaterThan(10);
    expect(run(buildJobSim("u", "update", { installBytes: 0 }, 200, true, 3))).toBeLessThan(6);
  });

  it("verify, move, uninstall and import jobs finish", () => {
    for (const kind of ["verify", "move", "uninstall", "import", "repair"] as const) {
      const sim = buildJobSim("j", kind, { installBytes: 94.8 * GiB }, 60, true, 2);
      let n = 0;
      while (!sim.done && n++ < 50_000) sim.advance(TICK_S);
      expect(sim.done, kind).toBe(true);
    }
  });
});

describe("errors", () => {
  it("formats bytes like Format.Bytes in Errors.cs", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(Math.round(96.3 * GiB))).toBe("96.3 GB");
    expect(formatBytes(Math.round(18.4 * GiB))).toBe("18.4 GB");
    expect(formatBytes(2 * 1024 ** 4)).toBe("2 TB");
  });

  it("insufficientSpace copy matches the C# text", () => {
    const e = Errors.insufficientSpace(Math.round(96.3 * GiB), Math.round(18.4 * GiB));
    expect(e.title).toBe("Not enough disk space");
    expect(e.message).toBe("This needs 96.3 GB but only 18.4 GB is free. Free up space or choose another drive.");
    expect(e.fix).toBe("freeSpace");
  });

  it("builds a well-formed error for every code", () => {
    for (const code of ERROR_CODES) {
      const e = errorForCode(code);
      expect(e.code).toBe(code);
      expect(e.title.length).toBeGreaterThan(0);
      expect(e.message.length).toBeGreaterThan(0);
    }
    expect(errorForCode("authExpired").fix).toBe("signIn");
    expect(errorForCode("eglBusy").fix).toBe("waitForEgl");
    expect(errorForCode("gameRunning").fix).toBe("closeGame");
    expect(errorForCode("corrupted").fix).toBe("repair");
    expect(errorForCode("permissionDenied").fix).toBe("chooseFolder");
  });
});

describe("art", () => {
  it("produces compact SVG data URIs", () => {
    for (const uri of [heroArt, tallArt]) {
      expect(uri.startsWith("data:image/svg+xml")).toBe(true);
      expect(uri.length).toBeLessThan(12_000);
      const svg = decodeURIComponent(uri.slice(uri.indexOf(",") + 1));
      expect(svg).toContain("<svg");
      expect(svg).not.toMatch(/<text/i);
    }
    expect(decodeURIComponent(heroArt)).toContain('viewBox="0 0 1920 1080"');
    expect(decodeURIComponent(tallArt)).toContain('viewBox="0 0 1200 1600"');
    expect(GAME_ART.logoUrl).toBeNull();
  });
});
