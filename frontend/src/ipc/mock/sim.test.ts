import { describe, expect, it } from "vitest";
import type { JobPhase } from "../contract.gen";
import { GiB, MiB } from "./constants";
import { Errors, errorForCode, formatBytes } from "./errors";
import { ERROR_CODES } from "./options";
import { JobSim, SpeedModel, TICK_S, buildJobSim, mulberry32, type JobSimInit } from "./sim";
import { generatedArt } from "./art";

const base: Omit<JobSimInit, "scale"> = {
  jobId: "job-1",
  appName: "Game",
  kind: "install",
  seed: 7,
  installBytes: 40 * GiB,
  downloadBytes: 20 * GiB,
  writeBytes: 40 * GiB,
  badFiles: 0,
  badBytes: 0,
};

const make = (over: Partial<Omit<JobSimInit, "scale">> = {}, scale = 400): JobSim => new JobSim({ ...base, ...over, scale });

/** Run to completion; returns the sequence of phases seen and every progress sample. */
function run(sim: JobSim, maxTicks = 200_000) {
  const phases: JobPhase[] = [];
  const samples = [sim.progress()];
  for (let i = 0; i < maxTicks && !sim.done; i++) {
    const r = sim.advance(TICK_S);
    const p = sim.progress();
    if (phases.at(-1) !== p.phase) phases.push(p.phase);
    samples.push(p);
    if (r === "done") break;
  }
  return { phases, samples };
}

describe("mulberry32 / SpeedModel", () => {
  it("is deterministic and in [0, 1)", () => {
    const a = mulberry32(42);
    const b = mulberry32(42);
    for (let i = 0; i < 100; i++) {
      const v = a();
      expect(v).toBe(b());
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThan(1);
    }
  });

  it("stays in a plausible range, with occasional dips", () => {
    const m = new SpeedModel(mulberry32(3), { nominal: 62e6, min: 35e6, max: 95e6, dipTo: 8e6 });
    let dips = 0;
    for (let i = 0; i < 5000; i++) {
      m.step(0.1);
      expect(m.raw).toBeGreaterThan(5e6);
      expect(m.raw).toBeLessThan(110e6);
      if (m.raw < 20e6) dips++;
    }
    expect(dips).toBeGreaterThan(0);
  });
});

describe("JobSim phases (honest phases)", () => {
  it("install: preparing → downloading → installing → finalizing, ending at exactly 100%", () => {
    const sim = make();
    expect(sim.plannedPhases()).toEqual(["preparing", "downloading", "installing", "finalizing"]);
    const { phases, samples } = run(sim);
    expect(phases).toEqual(["preparing", "downloading", "installing", "finalizing", "finalizing"].slice(0, phases.length));
    expect(phases).toContain("installing");
    const last = sim.progress();
    expect(last.percent).toBe(100);
    expect(samples.some((s) => s.percent === 100)).toBe(true);
  });

  it("whole-job percent is monotonic across every phase, including the repair of a verify", () => {
    for (const over of [{}, { kind: "update" as const }, { kind: "verify" as const, badFiles: 12, badBytes: 340 * MiB }, { kind: "repair" as const, badFiles: 5, badBytes: 80 * MiB }, { kind: "move" as const }]) {
      const { samples } = run(make(over));
      let prev = -1;
      for (const s of samples) {
        expect(s.percent).toBeGreaterThanOrEqual(prev);
        expect(s.percent).toBeLessThanOrEqual(100);
        prev = s.percent;
      }
      expect(prev).toBe(100);
    }
  });

  it("verifying fills only Verify (never network figures); downloading fills network + disk; installing is disk only", () => {
    const { samples } = run(make({ kind: "verify", badFiles: 9, badBytes: 200 * MiB }));
    const verifying = samples.filter((s) => s.phase === "verifying");
    expect(verifying.length).toBeGreaterThan(0);
    for (const s of verifying) {
      expect(s.network).toBeNull();
      expect(s.disk).toBeNull();
      expect(s.verify).not.toBeNull();
    }
    const repairing = samples.filter((s) => s.phase === "repairing");
    expect(repairing.length).toBeGreaterThan(0);
    for (const s of repairing) {
      expect(s.network).not.toBeNull();
      expect(s.disk).not.toBeNull();
      expect(s.verify?.badFiles).toBe(9);
    }

    const inst = run(make()).samples;
    for (const s of inst.filter((x) => x.phase === "downloading")) {
      expect(s.network).not.toBeNull();
      expect(s.disk).not.toBeNull();
      expect(s.verify).toBeNull();
    }
    const tail = inst.filter((x) => x.phase === "installing");
    expect(tail.length).toBeGreaterThan(0);
    for (const s of tail) {
      expect(s.network).toBeNull();
      expect(s.disk).not.toBeNull();
      expect(s.disk!.writtenBytes).toBeLessThanOrEqual(s.disk!.totalBytes);
    }
  });

  it("a clean verify has no repair step (it leaves the stepper once verify finds nothing)", () => {
    const sim = make({ kind: "verify" });
    expect(sim.plannedPhases()).toEqual(["verifying", "repairing", "finalizing"]);
    const { phases } = run(sim);
    expect(phases).not.toContain("repairing");
    expect(sim.report()).toMatchObject({ filesBad: 0, filesFixed: 0, verified: true, bytesToFix: 0 });
  });

  it("a verify that finds bad files repairs only them and reports the result", () => {
    const sim = make({ kind: "verify", badFiles: 12, badBytes: 340 * MiB }, 20);
    expect(sim.report()).toBeNull();
    const seen: JobPhase[] = [sim.progress().phase];
    let duringRepair: ReturnType<JobSim["report"]> = null;
    while (!sim.done) {
      sim.advance(TICK_S);
      const p = sim.progress();
      if (seen.at(-1) !== p.phase) seen.push(p.phase);
      if (p.phase === "repairing" && p.phasePercent > 30 && !duringRepair) duringRepair = sim.report();
    }
    expect(seen).toEqual(["verifying", "repairing", "finalizing"]);
    expect(duringRepair).toMatchObject({ filesBad: 12, verified: false, bytesToFix: 340 * MiB });
    expect(duringRepair!.filesFixed).toBeLessThan(12);
    expect(sim.report()).toMatchObject({ filesBad: 12, filesFixed: 12, verified: true });
  });

  it("network figures: downloaded/total, current/average/peak speed, CDN, connections; disk trails the network", () => {
    const sim = make({}, 20);
    sim.seek(0.5, "downloading");
    for (let i = 0; i < 30; i++) sim.advance(TICK_S);
    const p = sim.progress();
    expect(p.phase).toBe("downloading");
    expect(p.network!.downloadedBytes).toBeGreaterThan(9 * GiB);
    expect(p.network!.totalBytes).toBe(20 * GiB);
    expect(p.network!.peakBytesPerSecond).toBeGreaterThanOrEqual(p.network!.bytesPerSecond);
    expect(p.network!.averageBytesPerSecond).toBeGreaterThan(20e6);
    expect(p.network!.cdn).toMatch(/\./);
    expect(p.network!.connections).toBeGreaterThan(0);
    expect(p.disk!.writtenBytes).toBeLessThan(p.disk!.totalBytes * (p.network!.downloadedBytes / p.network!.totalBytes));
    expect(p.disk!.installSizeBytes).toBe(40 * GiB);
    expect(p.limiter).toMatch(/network|cdn/);
  });

  it("limiter follows the settings: speed limit, mode, paused", () => {
    const sim = make();
    sim.seek(0.4, "downloading");
    sim.setEnv({ mode: "balanced", speedLimit: 5e6, cdn: "fastly-download.epicgames.com", paused: false });
    for (let i = 0; i < 5; i++) sim.advance(TICK_S);
    const limited = sim.progress();
    expect(limited.limiter).toBe("speedLimit");
    expect(limited.network!.bytesPerSecond).toBeLessThanOrEqual(5e6);
    expect(limited.network!.cdn).toBe("fastly-download.epicgames.com");
    sim.setEnv({ mode: "background", speedLimit: null, cdn: null, paused: false });
    expect(sim.progress().limiter).toBe("mode");
    expect(sim.progress().mode).toBe("background");
    sim.setEnv({ mode: "balanced", speedLimit: null, cdn: null, paused: true });
    expect(sim.progress().limiter).toBe("paused");
  });

  it("ETA counts down in real seconds (within ±40%)", () => {
    const sim = make({}, 12);
    sim.seek(0.1, "downloading");
    for (let i = 0; i < 100; i++) sim.advance(TICK_S);
    const eta0 = sim.progress().etaSeconds!;
    const steps = 300;
    for (let i = 0; i < steps; i++) sim.advance(TICK_S);
    const eta1 = sim.progress().etaSeconds!;
    const dropped = eta0 - eta1;
    expect(dropped).toBeGreaterThan(30 * 0.6);
    expect(dropped).toBeLessThan(30 * 1.4);
  });

  it("seek() can put the job in any phase, with earlier phases counted as done", () => {
    for (const [kind, phase] of [
      ["install", "preparing"], ["install", "downloading"], ["install", "installing"], ["update", "finalizing"],
      ["move", "moving"], ["uninstall", "removing"], ["import", "importing"],
    ] as const) {
      const sim = make({ kind, uninstallSeconds: 4 });
      sim.seek(0.5, phase);
      const p = sim.progress();
      expect(p.phase).toBe(phase);
      expect(p.phasePercent).toBeGreaterThan(30);
      expect(p.phasePercent).toBeLessThan(70);
    }
    const repair = make({ kind: "verify", badFiles: 7, badBytes: 90 * MiB });
    repair.seek(0.5, "repairing");
    expect(repair.progress().phase).toBe("repairing");
    expect(repair.report()).toMatchObject({ filesBad: 7, verified: false });
    const before = make();
    before.seek(0.6, "downloading");
    expect(before.progress().percent).toBeGreaterThan(30);
  });

  it("emits ≈1 Hz samples, backfills them on seek, and downsamples to at most 600", () => {
    const sim = make({}, 20);
    sim.seek(0.5, "downloading");
    const seeded = sim.history();
    expect(seeded.length).toBeGreaterThan(5);
    expect(seeded[0]!.t).toBe(1);
    expect(seeded.at(-1)!.t - seeded[0]!.t).toBe(seeded.length - 1);
    for (let i = 0; i < 50; i++) sim.advance(TICK_S);
    expect(sim.history().length).toBe(seeded.length + 5);
    const big = make({}, 1);
    big.seek(0.5, "downloading");
    for (let i = 0; i < 10_000; i++) big.advance(TICK_S);
    expect(big.history(600).length).toBeLessThanOrEqual(600);
    expect(big.history().every((s) => s.diskBytesPerSecond >= 0 && s.networkBytesPerSecond >= 0)).toBe(true);
  });

  it("small jobs stay watchable (≥ ~10 s real) unless a time scale is forced", () => {
    const sim = buildJobSim({ ...base, kind: "update", downloadBytes: 1.2 * GiB, writeBytes: 4 * GiB, installBytes: 90 * GiB }, 12, false);
    let t = 0;
    while (!sim.done && t < 600) {
      sim.advance(TICK_S);
      t += TICK_S;
    }
    expect(t).toBeGreaterThan(9);
    const forced = buildJobSim({ ...base, kind: "update", downloadBytes: 1.2 * GiB, writeBytes: 4 * GiB, installBytes: 90 * GiB }, 400, true);
    expect(forced.scale).toBe(400);
  });

  it("move, uninstall and import jobs finish", () => {
    for (const kind of ["move", "uninstall", "import", "overlay"] as const) {
      const sim = make({ kind, installBytes: 8 * GiB, downloadBytes: 180 * MiB, writeBytes: 420 * MiB });
      run(sim);
      expect(sim.done).toBe(true);
      expect(sim.progress().percent).toBe(100);
    }
  });
});

describe("errors", () => {
  it("formats bytes like Format.Bytes in Errors.cs", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1024)).toBe("1 KB");
    expect(formatBytes(Math.round(4.8 * GiB))).toBe("4.8 GB");
  });

  it("insufficientSpace copy matches the C# text", () => {
    expect(Errors.insufficientSpace(Math.round(4.8 * GiB), Math.round(0.9 * GiB)).message).toBe(
      "This needs 4.8 GB but only 921.6 MB is free. Free up space or choose another drive.",
    );
  });

  it("builds a well-formed error for every code", () => {
    for (const code of ERROR_CODES) {
      const e = errorForCode(code, "Test Game", "TestApp");
      expect(e.code).toBe(code);
      expect(e.title.length).toBeGreaterThan(0);
      expect(e.message.length).toBeGreaterThan(0);
      expect(e).toHaveProperty("appName");
      expect(e).toHaveProperty("detail");
    }
  });

  it("names the game instead of hard-coding Fortnite", () => {
    expect(Errors.eglBusy("Orchard Tactics").message).toContain("Orchard Tactics");
    expect(Errors.gameRunning("Orchard Tactics").title).toBe("Orchard Tactics is running");
  });
});

describe("generated art", () => {
  it("produces compact, deterministic SVG data URIs with tall, wide, thumb and a flat placeholder", () => {
    const a = generatedArt("Foxglove1A2");
    expect(a.tall).toMatch(/^data:image\/svg\+xml/);
    expect(a.wide).toMatch(/^data:image\/svg\+xml/);
    expect(a.tallThumb).toMatch(/^data:image\/svg\+xml/);
    expect(a.placeholder).toMatch(/^hsl\(/);
    expect(a.tall!.length).toBeLessThan(12_000);
    expect(a.tallThumb!.length).toBeLessThan(a.tall!.length);
    expect(generatedArt("Foxglove1A2")).toEqual(a);
    expect(generatedArt("Other")).not.toEqual(a);
    expect(generatedArt("NoTall", { tall: false }).tall).toBeNull();
  });
});
