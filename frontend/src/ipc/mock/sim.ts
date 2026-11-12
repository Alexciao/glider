import type { JobKind, JobPhase, JobProgress } from "../contract.gen";
import {
  FULL_DOWNLOAD_BYTES,
  FULL_INSTALL_BYTES,
  REPAIR_DOWNLOAD_BYTES,
  REPAIR_WRITE_BYTES,
  UPDATE_DOWNLOAD_BYTES,
  UPDATE_WRITE_BYTES,
} from "./constants";

export const TICK_MS = 100;
export const TICK_S = TICK_MS / 1000;
/** Real seconds a small job (update, verify, repair) should take at minimum when the time scale is the default. */
const MIN_REAL_SECONDS = 12;
/** EMA weight per 100 ms tick (~2 s time constant), like the backend's smoothing. */
const ALPHA = 0.05;
/** Slower average (~5 s) that ETA is derived from, so one CDN hiccup does not swing the countdown. */
const ETA_ALPHA = 0.02;

export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
const pct = (done: number, total: number): number => (total <= 0 ? 100 : clamp((done / total) * 100, 0, 100));

interface SpeedConfig {
  nominal: number;
  min: number;
  max: number;
  /** Speed during an occasional dip (CDN hiccup), bytes/s. */
  dipTo?: number;
}

const DOWNLOAD: SpeedConfig = { nominal: 62e6, min: 35e6, max: 95e6, dipTo: 8e6 };
const VERIFY: SpeedConfig = { nominal: 2.0e9, min: 1.5e9, max: 2.6e9 };
const MOVE: SpeedConfig = { nominal: 260e6, min: 180e6, max: 340e6 };

/** Slowly drifting speed with jitter and occasional 1–2 s dips. */
export class SpeedModel {
  raw: number;
  ema: number;
  /** Slow average, used for ETA. */
  slow: number;
  private base: number;
  private target: number;
  private dipLeft = 0;
  private dipRaw = 0;

  constructor(
    private readonly rng: () => number,
    private readonly cfg: SpeedConfig,
  ) {
    this.base = this.target = cfg.nominal;
    // Deterministic, plausible starting values (stable under `freeze`).
    this.raw = cfg.nominal * 1.03;
    this.ema = cfg.nominal * 0.995;
    this.slow = cfg.nominal * 0.995;
  }

  step(dt: number): void {
    const { rng, cfg } = this;
    if (rng() < 0.01) this.target = cfg.min * 1.25 + rng() * (cfg.max * 0.85 - cfg.min * 1.25);
    this.base = clamp(this.base + (this.target - this.base) * 0.03 + (rng() - 0.5) * cfg.nominal * 0.02, cfg.min, cfg.max);
    if (this.dipLeft > 0) {
      this.dipLeft -= dt;
    } else if (cfg.dipTo !== undefined && rng() < 0.005) {
      this.dipLeft = 1 + rng();
      this.dipRaw = cfg.dipTo * (0.85 + rng() * 0.4);
    }
    const jitter = 1 + (rng() - 0.5) * 0.16;
    this.raw = (this.dipLeft > 0 ? this.dipRaw : this.base) * jitter;
    this.ema += ALPHA * (this.raw - this.ema);
    this.slow += ETA_ALPHA * (this.raw - this.slow);
  }
}

export type PhaseSpec =
  | { phase: JobPhase; mode: "timed"; seconds: number }
  | { phase: JobPhase; mode: "download" | "verify" | "move" };

export interface JobSimInit {
  jobId: string;
  plan: PhaseSpec[];
  downloadBytes: number;
  writeBytes: number;
  verifyBytes: number;
  moveBytes: number;
  /** Bytes advance `scale`× faster than displayed speeds; ETA/elapsed are real seconds. */
  scale: number;
  seed: number;
}

/** One job's progress: phases, byte counters and smoothed speeds. Pure; the backend drives it with `advance`. */
export class JobSim {
  readonly jobId: string;
  readonly scale: number;
  private readonly plan: PhaseSpec[];
  private readonly downloadBytes: number;
  private readonly writeBytes: number;
  private readonly verifyBytes: number;
  private readonly moveBytes: number;
  private readonly ratio: number;
  private readonly rng: () => number;
  private readonly dl: SpeedModel;
  private readonly vf: SpeedModel;
  private readonly mv: SpeedModel;

  private phaseIndex = 0;
  private phaseT = 0;
  private elapsed = 0;
  private downloaded = 0;
  private written = 0;
  private verified = 0;
  private moved = 0;
  private diskRaw: number;
  private diskEma: number;

  constructor(init: JobSimInit) {
    this.jobId = init.jobId;
    this.scale = init.scale;
    this.plan = init.plan;
    this.downloadBytes = init.downloadBytes;
    this.writeBytes = init.writeBytes;
    this.verifyBytes = init.verifyBytes;
    this.moveBytes = init.moveBytes;
    this.ratio = init.downloadBytes > 0 ? init.writeBytes / init.downloadBytes : 1;
    this.rng = mulberry32(init.seed);
    this.dl = new SpeedModel(this.rng, DOWNLOAD);
    this.vf = new SpeedModel(this.rng, VERIFY);
    this.mv = new SpeedModel(this.rng, MOVE);
    this.diskRaw = this.dl.raw * this.ratio;
    this.diskEma = this.dl.ema * this.ratio * 0.98;
  }

  get done(): boolean {
    return this.phaseIndex >= this.plan.length;
  }

  /** Jump into the first byte phase at `fraction` (0..1), as if the job had been running for a while. */
  seek(fraction: number): void {
    const f = clamp(fraction, 0, 1);
    let idx = this.plan.findIndex((p) => p.mode === "download");
    if (idx < 0) idx = this.plan.findIndex((p) => p.mode !== "timed");
    const spec = this.plan[idx];
    if (!spec) return;
    this.phaseIndex = idx;
    this.phaseT = 0;
    const before = this.plan.slice(0, idx).reduce((s, p) => s + (p.mode === "timed" ? p.seconds : 0), 0);
    if (spec.mode === "download") {
      this.downloaded = f * this.downloadBytes;
      this.written = this.downloaded * this.ratio * 0.992;
      this.elapsed = before + this.downloaded / (DOWNLOAD.nominal * this.scale);
    } else if (spec.mode === "verify") {
      this.verified = f * this.verifyBytes;
      this.elapsed = before + this.verified / (VERIFY.nominal * this.scale);
    } else if (spec.mode === "move") {
      this.moved = f * this.moveBytes;
      this.elapsed = before + this.moved / (MOVE.nominal * this.scale);
    }
  }

  /** Advance by `dt` real seconds. Returns whether the phase changed or the whole job finished. */
  advance(dt: number): "tick" | "phase" | "done" {
    const spec = this.plan[this.phaseIndex];
    if (!spec) return "done";
    this.elapsed += dt;
    let finished = false;
    switch (spec.mode) {
      case "timed":
        this.phaseT += dt;
        finished = this.phaseT >= spec.seconds;
        break;
      case "download": {
        this.dl.step(dt);
        this.diskRaw = this.dl.raw * this.ratio * (1 + (this.rng() - 0.5) * 0.24);
        this.diskEma += ALPHA * (this.diskRaw - this.diskEma);
        this.downloaded = Math.min(this.downloadBytes, this.downloaded + this.dl.raw * dt * this.scale);
        // Writes trail downloads (decompress + write) and can never run ahead of what has arrived.
        this.written = Math.min(this.writeBytes, this.downloaded * this.ratio, this.written + this.diskRaw * dt * this.scale);
        finished = this.downloaded >= this.downloadBytes && this.written >= this.writeBytes - 1;
        break;
      }
      case "verify":
        this.vf.step(dt);
        this.verified = Math.min(this.verifyBytes, this.verified + this.vf.raw * dt * this.scale);
        finished = this.verified >= this.verifyBytes;
        break;
      case "move":
        this.mv.step(dt);
        this.moved = Math.min(this.moveBytes, this.moved + this.mv.raw * dt * this.scale);
        finished = this.moved >= this.moveBytes;
        break;
    }
    if (!finished) return "tick";
    this.phaseIndex++;
    this.phaseT = 0;
    return this.done ? "done" : "phase";
  }

  /** Real seconds left in phases after `index`. */
  private remainingAfter(index: number): number {
    let t = 0;
    for (const p of this.plan.slice(index + 1)) {
      if (p.mode === "timed") t += p.seconds;
      else if (p.mode === "download") t += this.downloadBytes / (DOWNLOAD.nominal * this.scale);
      else if (p.mode === "verify") t += this.verifyBytes / (VERIFY.nominal * this.scale);
      else t += this.moveBytes / (MOVE.nominal * this.scale);
    }
    return t;
  }

  progress(): JobProgress {
    const last = this.plan.length - 1;
    const done = this.done;
    const index = done ? last : this.phaseIndex;
    const spec = this.plan[index]!;
    let percent = 0;
    let written = this.written;
    let totalWrite = this.writeBytes;
    let dlSpeed = 0;
    let rawSpeed = 0;
    let diskSpeed = 0;
    let eta: number | null = null;
    const later = this.remainingAfter(index);

    switch (spec.mode) {
      case "timed":
        percent = Math.min(100, (this.phaseT / spec.seconds) * 100);
        eta = spec.phase === "preparing" ? null : Math.max(0, spec.seconds - this.phaseT) + later;
        break;
      case "download":
        percent = pct(this.downloaded, this.downloadBytes);
        dlSpeed = this.dl.ema;
        rawSpeed = this.dl.raw;
        diskSpeed = this.diskEma;
        eta = (this.downloadBytes - this.downloaded) / (this.dl.slow * this.scale) + later;
        break;
      case "verify":
        percent = pct(this.verified, this.verifyBytes);
        written = this.verified;
        totalWrite = this.verifyBytes;
        diskSpeed = this.vf.ema;
        eta = (this.verifyBytes - this.verified) / (this.vf.slow * this.scale) + later;
        break;
      case "move":
        percent = pct(this.moved, this.moveBytes);
        written = this.moved;
        totalWrite = this.moveBytes;
        diskSpeed = this.mv.ema;
        eta = (this.moveBytes - this.moved) / (this.mv.slow * this.scale) + later;
        break;
    }
    if (done) {
      percent = 100;
      dlSpeed = rawSpeed = diskSpeed = 0;
      eta = 0;
    }
    return {
      jobId: this.jobId,
      phase: spec.phase,
      percent: Math.round(percent * 100) / 100,
      downloadedBytes: Math.round(this.downloaded),
      totalDownloadBytes: this.downloadBytes,
      writtenBytes: Math.round(written),
      totalWriteBytes: totalWrite,
      downloadBytesPerSecond: Math.round(dlSpeed),
      diskBytesPerSecond: Math.round(diskSpeed),
      rawDownloadBytesPerSecond: Math.round(rawSpeed),
      etaSeconds: eta === null ? null : Math.round(eta * 10) / 10,
      elapsedSeconds: Math.round(this.elapsed),
    };
  }

  /**
   * `count` synthetic progress events ending exactly at the current progress (oldest first), so a UI that
   * joins mid-download can pre-seed its sparkline. Only meaningful in the download phase.
   */
  history(count: number, seed: number): JobProgress[] {
    const cur = this.progress();
    const spec = this.plan[Math.min(this.phaseIndex, this.plan.length - 1)];
    if (!spec || spec.mode !== "download" || this.done || count <= 1) return [cur];
    const model = new SpeedModel(mulberry32(seed ^ 0x9e3779b9), DOWNLOAD);
    const raws: number[] = [];
    for (let i = 0; i < count - 1; i++) {
      model.step(TICK_S);
      raws.push(model.raw);
    }
    // Smoothed series, oldest → newest, anchored so it lands near the current smoothed speed.
    const emas: number[] = [];
    let ema = cur.downloadBytesPerSecond || DOWNLOAD.nominal;
    for (const raw of raws) {
      ema += ALPHA * (raw - ema);
      emas.push(ema);
    }
    const out: JobProgress[] = new Array<JobProgress>(count);
    out[count - 1] = cur;
    let backBytes = 0;
    for (let k = 1; k < count; k++) {
      const idx = count - 1 - k;
      backBytes += (raws[idx] ?? cur.rawDownloadBytesPerSecond) * TICK_S * this.scale;
      const downloaded = Math.max(0, cur.downloadedBytes - backBytes);
      const written = Math.max(0, cur.writtenBytes - backBytes * this.ratio);
      out[idx] = {
        ...cur,
        percent: Math.round(pct(downloaded, this.downloadBytes) * 100) / 100,
        downloadedBytes: Math.round(downloaded),
        writtenBytes: Math.round(written),
        downloadBytesPerSecond: Math.round(emas[idx] ?? cur.downloadBytesPerSecond),
        rawDownloadBytesPerSecond: Math.round(raws[idx] ?? cur.rawDownloadBytesPerSecond),
        diskBytesPerSecond: Math.round((emas[idx] ?? cur.downloadBytesPerSecond) * this.ratio),
        etaSeconds: cur.etaSeconds === null ? null : Math.round((cur.etaSeconds + k * TICK_S) * 10) / 10,
        elapsedSeconds: Math.max(0, Math.round(cur.elapsedSeconds - k * TICK_S)),
      };
    }
    return out;
  }
}

export interface SimSizes {
  /** Installed size on disk, for verify/move. */
  installBytes: number;
  /** Seconds the `removing` phase takes. */
  uninstallSeconds?: number;
}

/** Choose the byte-speed scale: the global time scale for big jobs, but small jobs still take a watchable ~12 s. */
export function chooseScale(timeScale: number, explicit: boolean, plan: PhaseSpec[], bytes: Pick<JobSimInit, "downloadBytes" | "verifyBytes" | "moveBytes">): number {
  if (explicit) return timeScale;
  let nominal = 0;
  for (const p of plan) {
    if (p.mode === "download") nominal += bytes.downloadBytes / DOWNLOAD.nominal;
    else if (p.mode === "verify") nominal += bytes.verifyBytes / VERIFY.nominal;
    else if (p.mode === "move") nominal += bytes.moveBytes / MOVE.nominal;
  }
  return Math.min(timeScale, Math.max(1, nominal / MIN_REAL_SECONDS));
}

export function buildJobSim(
  jobId: string,
  kind: JobKind,
  sizes: SimSizes,
  timeScale: number,
  explicitScale: boolean,
  seed: number,
): JobSim {
  let plan: PhaseSpec[];
  let downloadBytes = 0;
  let writeBytes = 0;
  let verifyBytes = 0;
  let moveBytes = 0;
  switch (kind) {
    case "install":
      plan = [
        { phase: "preparing", mode: "timed", seconds: 1.2 },
        { phase: "downloading", mode: "download" },
        { phase: "verifying", mode: "verify" },
        { phase: "finalizing", mode: "timed", seconds: 0.6 },
      ];
      downloadBytes = FULL_DOWNLOAD_BYTES;
      writeBytes = verifyBytes = FULL_INSTALL_BYTES;
      break;
    case "update":
      plan = [
        { phase: "preparing", mode: "timed", seconds: 0.8 },
        { phase: "downloading", mode: "download" },
        { phase: "verifying", mode: "verify" },
        { phase: "finalizing", mode: "timed", seconds: 0.5 },
      ];
      downloadBytes = UPDATE_DOWNLOAD_BYTES;
      writeBytes = verifyBytes = UPDATE_WRITE_BYTES;
      break;
    case "verify":
      plan = [{ phase: "verifying", mode: "verify" }];
      verifyBytes = sizes.installBytes;
      break;
    case "repair":
      plan = [
        { phase: "verifying", mode: "verify" },
        { phase: "downloading", mode: "download" },
        { phase: "finalizing", mode: "timed", seconds: 0.5 },
      ];
      verifyBytes = sizes.installBytes;
      downloadBytes = REPAIR_DOWNLOAD_BYTES;
      writeBytes = REPAIR_WRITE_BYTES;
      break;
    case "move":
      plan = [{ phase: "moving", mode: "move" }];
      moveBytes = sizes.installBytes;
      break;
    case "uninstall":
      plan = [{ phase: "removing", mode: "timed", seconds: sizes.uninstallSeconds ?? 1.5 }];
      break;
    case "import":
      plan = [{ phase: "preparing", mode: "timed", seconds: 1.2 }];
      break;
  }
  const scale = chooseScale(timeScale, explicitScale, plan, { downloadBytes, verifyBytes, moveBytes });
  return new JobSim({ jobId, plan, downloadBytes, writeBytes, verifyBytes, moveBytes, scale, seed });
}
