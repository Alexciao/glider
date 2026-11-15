import type {
  DiskStats,
  DownloadMode,
  JobKind,
  JobPhase,
  JobProgress,
  JobSample,
  Limiter,
  NetworkStats,
  VerifyReport,
  VerifyStats,
} from "../contract.gen";
import { CDNS } from "./constants";
import { clamp, mulberry32 } from "./rng";

export { mulberry32 } from "./rng";

/**
 * Honest job simulation (docs/v0.2-design.md §5). Each phase has its own meaning and its own figures:
 *  - verifying   local reads only: files/bytes checked, read speed, bad files. Never network figures.
 *  - repairing   re-download of the bad files only (network + disk); only exists when verify found something.
 *  - downloading network + disk (writes trail the network).
 *  - installing  the disk-only tail after the network finished.
 *  - preparing / finalizing / removing / importing: short timed steps.
 * The whole-job percent is a weighted sum over phases and is monotonic. Pure: the backend drives it with `advance`.
 */

export const TICK_MS = 100;
export const TICK_S = TICK_MS / 1000;
/** Real seconds a small job should take at minimum when the time scale is the default. */
const MIN_REAL_SECONDS = 12;
/** EMA weight per 100 ms tick (~2 s time constant), like the backend's smoothing. */
const ALPHA = 0.05;
/** Slower average (~5 s) that ETA is derived from, so one CDN hiccup does not swing the countdown. */
const ETA_ALPHA = 0.02;
const AVG_FILE_BYTES = 6 * 1024 ** 2;
/** Disk writes trail the network by this much until the network is done (the rest is the "Installing" tail). */
const WRITE_LAG = 0.86;
/** What a slow drive writes (bytes/s), for the `bottleneck: "disk"` option: about 7.8 MB/s, like the SATA SSD in docs/download-perf.md. */
const SLOW_DRIVE_WRITE = 7.8 * 1024 * 1024;

interface SpeedConfig {
  nominal: number;
  min: number;
  max: number;
  /** Speed during an occasional dip (CDN hiccup), bytes/s. */
  dipTo?: number;
}

const DOWNLOAD: SpeedConfig = { nominal: 62e6, min: 35e6, max: 95e6, dipTo: 8e6 };
const VERIFY: SpeedConfig = { nominal: 1.9e9, min: 1.4e9, max: 2.6e9 };
const WRITE: SpeedConfig = { nominal: 310e6, min: 220e6, max: 420e6 };
const MOVE: SpeedConfig = { nominal: 260e6, min: 180e6, max: 340e6 };

const MODE_FACTOR: Record<DownloadMode, number> = { background: 0.35, balanced: 1, full: 1.12 };

/** Slowly drifting speed with jitter and occasional 1–2 s dips. */
export class SpeedModel {
  raw: number;
  ema: number;
  /** Slow average, used for ETA. */
  slow: number;
  dipping = false;
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
    this.dipping = this.dipLeft > 0;
    const jitter = 1 + (rng() - 0.5) * 0.16;
    this.raw = (this.dipping ? this.dipRaw : this.base) * jitter;
    this.ema += ALPHA * (this.raw - this.ema);
    this.slow += ETA_ALPHA * (this.raw - this.slow);
  }
}

export type PhaseKind = "timed" | "verify" | "network" | "write" | "move";

export interface PhaseSpec {
  phase: JobPhase;
  kind: PhaseKind;
  /** Share of the whole-job percent. */
  weight: number;
  /** timed: real seconds. */
  seconds?: number;
  /** verify/move/write: bytes; network: bytes to download. */
  bytes?: number;
  /** network: bytes written to disk by this job (decompressed). */
  writeBytes?: number;
  files?: number;
}

export interface JobEnv {
  mode: DownloadMode;
  speedLimit: number | null;
  cdn: string | null;
  paused: boolean;
}

export const DEFAULT_ENV: JobEnv = { mode: "balanced", speedLimit: null, cdn: null, paused: false };

export interface JobSimInit {
  jobId: string;
  appName: string;
  kind: JobKind;
  /** Bytes advance `scale`× faster than displayed speeds; ETA/elapsed are real seconds. */
  scale: number;
  seed: number;
  /** Installed size of the game: what verify reads and move copies. */
  installBytes: number;
  /** Network bytes for install/update/modify/overlay. */
  downloadBytes: number;
  /** Bytes written for install/update/modify/overlay. */
  writeBytes: number;
  /** Damaged files a verify finds (0 = a clean game). */
  badFiles: number;
  badBytes: number;
  /** Seconds a removal takes. */
  uninstallSeconds?: number;
  /** Import jobs: also verify (Full), else only link. */
  importVerify?: boolean;
  /** Mock only: pretend the drive, the processor or the download server is the weak link (screenshots, dev). */
  bottleneck?: "disk" | "cpu" | "cdn" | null;
}

const filesOf = (bytes: number): number => Math.max(1, Math.round(bytes / AVG_FILE_BYTES));

export function buildPlan(init: JobSimInit): PhaseSpec[] {
  const dl = init.downloadBytes;
  const wr = init.writeBytes;
  switch (init.kind) {
    case "install":
    case "update":
    case "modify":
    case "overlay":
      return [
        { phase: "preparing", kind: "timed", weight: 3, seconds: init.kind === "overlay" ? 0.6 : 1.2 },
        { phase: "downloading", kind: "network", weight: 72, bytes: dl, writeBytes: wr, files: filesOf(wr) },
        { phase: "installing", kind: "write", weight: 20, bytes: wr },
        { phase: "finalizing", kind: "timed", weight: 5, seconds: 0.7 },
      ];
    case "verify": {
      const bad = init.badFiles > 0;
      return [
        { phase: "verifying", kind: "verify", weight: bad ? 55 : 92, bytes: init.installBytes, files: filesOf(init.installBytes) },
        { phase: "repairing", kind: "network", weight: bad ? 38 : 0, bytes: init.badBytes, writeBytes: init.badBytes, files: init.badFiles },
        { phase: "finalizing", kind: "timed", weight: bad ? 7 : 8, seconds: 1.4 },
      ];
    }
    case "repair": {
      const files = Math.max(1, init.badFiles);
      const bytes = Math.max(init.badBytes, 24 * 1024 ** 2);
      return [
        { phase: "verifying", kind: "verify", weight: 45, bytes: init.installBytes, files: filesOf(init.installBytes) },
        { phase: "repairing", kind: "network", weight: 48, bytes, writeBytes: bytes, files },
        { phase: "finalizing", kind: "timed", weight: 7, seconds: 1.4 },
      ];
    }
    case "move":
      return [
        { phase: "preparing", kind: "timed", weight: 2, seconds: 0.8 },
        { phase: "moving", kind: "move", weight: 95, bytes: init.installBytes, files: filesOf(init.installBytes) },
        { phase: "finalizing", kind: "timed", weight: 3, seconds: 0.6 },
      ];
    case "uninstall":
      return [{ phase: "removing", kind: "timed", weight: 100, seconds: init.uninstallSeconds ?? 1.5 }];
    case "import":
      return init.importVerify
        ? [
            { phase: "importing", kind: "timed", weight: 25, seconds: 1.4 },
            { phase: "verifying", kind: "verify", weight: 75, bytes: init.installBytes, files: filesOf(init.installBytes) },
          ]
        : [{ phase: "importing", kind: "timed", weight: 100, seconds: 1.6 }];
  }
}

export class JobSim {
  readonly jobId: string;
  readonly appName: string;
  readonly kind: JobKind;
  readonly scale: number;
  private plan: PhaseSpec[];
  private readonly init: JobSimInit;
  private readonly rng: () => number;
  private readonly dl: SpeedModel;
  private readonly vf: SpeedModel;
  private readonly wr: SpeedModel;
  private readonly mv: SpeedModel;

  private idx = 0;
  private phaseT = 0;
  private elapsed = 0;
  private netTime = 0;
  private downloaded = 0;
  private written = 0;
  private verified = 0;
  private moved = 0;
  private fixed = 0;
  private diskEma = 0;
  private peakNet = 0;
  private peakDisk = 0;
  private verifyDone = false;
  private nextSample = 1;
  private samples: JobSample[] = [];
  private lastPercent = 0;
  private env: JobEnv = DEFAULT_ENV;

  constructor(init: JobSimInit) {
    this.init = init;
    this.jobId = init.jobId;
    this.appName = init.appName;
    this.kind = init.kind;
    this.scale = init.scale;
    this.plan = buildPlan(init);
    this.rng = mulberry32(init.seed);
    this.dl = new SpeedModel(this.rng, DOWNLOAD);
    this.vf = new SpeedModel(this.rng, VERIFY);
    this.wr = new SpeedModel(this.rng, WRITE);
    this.mv = new SpeedModel(this.rng, MOVE);
    const net = this.plan.find((p) => p.kind === "network");
    const ratio = net && net.bytes ? (net.writeBytes ?? 0) / net.bytes : 1;
    this.diskEma = this.dl.ema * ratio * 0.98;
  }

  get done(): boolean {
    return this.idx >= this.plan.length;
  }

  setEnv(env: JobEnv): void {
    this.env = env;
  }

  get phase(): JobPhase | null {
    return this.plan[this.idx]?.phase ?? null;
  }

  /** Planned phases in order (what JobInfo.Phases carries). A repair step that verify found unnecessary is dropped. */
  plannedPhases(): JobPhase[] {
    return this.plan.map((p) => p.phase);
  }

  /** What a finished/ongoing verify found; null before the first verify completes. */
  report(): VerifyReport | null {
    if (!this.verifyDone) return null;
    const verifyPhase = this.plan.find((p) => p.kind === "verify");
    const bad = this.init.badFiles;
    const repairAt = this.plan.findIndex((p) => p.phase === "repairing");
    const repaired = bad === 0 || repairAt < 0 || this.done || this.idx > repairAt;
    return {
      filesChecked: verifyPhase?.files ?? filesOf(this.init.installBytes),
      filesBad: bad,
      filesFixed: repaired ? bad : this.fixed,
      verified: this.done && repaired,
      bytesToFix: this.init.badBytes,
    };
  }

  private cur(): PhaseSpec | undefined {
    return this.plan[this.idx];
  }

  private rate(base: number): number {
    return base * MODE_FACTOR[this.env.mode];
  }

  /** What a slow drive lets through of the download (bytes/s of network), when the drive is the mock's bottleneck. */
  private netCap(ratio: number): number {
    return this.init.bottleneck === "disk" ? SLOW_DRIVE_WRITE / Math.max(0.01, ratio) : Infinity;
  }

  /** What a slow drive writes (bytes/s), when the drive is the mock's bottleneck. */
  private diskCap(): number {
    return this.init.bottleneck === "disk" ? SLOW_DRIVE_WRITE : Infinity;
  }

  /**
   * Jump into a phase at `fraction` (0..1) of it, as if the job had been running for a while: earlier phases count as
   * done. Without `phase` it picks the main byte phase (download, else verify, else the first byte phase).
   */
  seek(fraction: number, phase?: JobPhase): void {
    const f = clamp(fraction, 0, 1);
    let at = phase ? this.plan.findIndex((p) => p.phase === phase) : -1;
    if (at < 0) at = this.plan.findIndex((p) => p.kind === "network" && p.weight > 0);
    if (at < 0) at = this.plan.findIndex((p) => p.kind === "verify");
    if (at < 0) at = this.plan.findIndex((p) => p.kind !== "timed");
    if (at < 0) at = 0;
    const spec = this.plan[at];
    if (!spec) return;
    let t = 0;
    for (const p of this.plan.slice(0, at)) {
      if (p.kind === "timed") t += p.seconds ?? 0;
      else if (p.kind === "network") {
        this.downloaded = p.bytes ?? 0;
        this.written = (p.writeBytes ?? 0) * WRITE_LAG;
        this.netTime = this.downloaded / (DOWNLOAD.nominal * this.scale);
        t += this.netTime;
      } else if (p.kind === "verify") {
        this.verified = p.bytes ?? 0;
        this.verifyDone = true;
        t += this.verified / (VERIFY.nominal * this.scale);
      } else if (p.kind === "write") {
        this.written = p.bytes ?? 0;
        t += ((p.bytes ?? 0) * (1 - WRITE_LAG)) / (WRITE.nominal * this.scale);
      } else if (p.kind === "move") {
        this.moved = p.bytes ?? 0;
        t += this.moved / (MOVE.nominal * this.scale);
      }
    }
    this.idx = at;
    this.phaseT = 0;
    if (spec.kind === "timed") {
      this.phaseT = f * (spec.seconds ?? 0);
      t += this.phaseT;
    } else if (spec.kind === "network") {
      this.downloaded = f * (spec.bytes ?? 0);
      this.written = Math.min(spec.writeBytes ?? 0, this.downloaded * ratioOf(spec) * WRITE_LAG);
      this.netTime = this.downloaded / (DOWNLOAD.nominal * this.scale);
      this.fixed = spec.phase === "repairing" ? Math.floor(this.init.badFiles * f) : 0;
      t += this.netTime;
    } else if (spec.kind === "verify") {
      this.verified = f * (spec.bytes ?? 0);
      t += this.verified / (VERIFY.nominal * this.scale);
    } else if (spec.kind === "write") {
      const total = spec.bytes ?? 0;
      this.written = total * (WRITE_LAG + f * (1 - WRITE_LAG));
      t += (this.written - total * WRITE_LAG) / (WRITE.nominal * this.scale);
    } else if (spec.kind === "move") {
      this.moved = f * (spec.bytes ?? 0);
      t += this.moved / (MOVE.nominal * this.scale);
    }
    this.elapsed = t;
    this.samples = [];
    this.backfillSamples(Math.floor(t), spec.kind);
    this.nextSample = Math.floor(t) + 1;
    this.lastPercent = this.percentNow();
  }

  /** Synthetic past samples so a UI that joins mid-job has a history to draw. */
  private backfillSamples(seconds: number, kind: PhaseKind): void {
    const model = new SpeedModel(mulberry32(this.init.seed ^ 0x9e3779b9), kind === "verify" ? VERIFY : kind === "write" ? WRITE : kind === "move" ? MOVE : DOWNLOAD);
    const ratio = ratioOf(this.cur());
    let ema = model.ema;
    for (let s = 1; s <= seconds; s++) {
      for (let k = 0; k < 10; k++) {
        model.step(TICK_S);
        ema += ALPHA * (model.raw - ema);
      }
      const warm = Math.min(1, s / 3);
      this.samples.push({
        t: s,
        // A slow drive only pushes back once its write queue is full: the first seconds still run at the connection's pace.
        networkBytesPerSecond: kind === "network" ? Math.round((s > 6 ? Math.min(ema, this.netCap(ratio)) : ema) * warm) : 0,
        diskBytesPerSecond: Math.round(Math.min(kind === "network" ? (s > 6 ? Math.min(ema, this.netCap(ratio)) : ema) * ratio * WRITE_LAG : ema, s > 6 ? this.diskCap() : Infinity) * warm),
      });
    }
  }

  /** Advance by `dt` real seconds. Returns whether the phase changed or the whole job finished. */
  advance(dt: number): "tick" | "phase" | "done" {
    const spec = this.cur();
    if (!spec) return "done";
    this.elapsed += dt;
    let finished = false;
    switch (spec.kind) {
      case "timed":
        this.phaseT += dt;
        finished = this.phaseT >= (spec.seconds ?? 0);
        break;
      case "network": {
        this.dl.step(dt);
        this.netTime += dt;
        const limit = this.env.speedLimit;
        const total = spec.bytes ?? 0;
        const ratio = ratioOf(spec);
        // A slow drive holds the whole download down to what it can write.
        const net = Math.min(this.rate(this.dl.raw), limit ?? Infinity, this.netCap(ratio));
        this.downloaded = Math.min(total, this.downloaded + net * dt * this.scale);
        const diskRaw = net * ratio * (1 + (this.rng() - 0.5) * 0.24);
        this.diskEma += ALPHA * (diskRaw - this.diskEma);
        // Writes trail downloads (decompress + write) and never run ahead of what has arrived.
        this.written = Math.min(spec.writeBytes ?? 0, this.downloaded * ratio * WRITE_LAG, this.written + diskRaw * dt * this.scale);
        if (spec.phase === "repairing") this.fixed = Math.floor((this.init.badFiles * this.downloaded) / Math.max(1, total));
        finished = this.downloaded >= total;
        this.peakNet = Math.max(this.peakNet, net);
        this.peakDisk = Math.max(this.peakDisk, diskRaw);
        break;
      }
      case "write": {
        this.wr.step(dt);
        const total = spec.bytes ?? 0;
        this.written = Math.min(total, this.written + this.rate(this.wr.raw) * dt * this.scale);
        this.diskEma += ALPHA * (this.wr.raw - this.diskEma);
        this.peakDisk = Math.max(this.peakDisk, this.wr.raw);
        finished = this.written >= total;
        break;
      }
      case "verify":
        this.vf.step(dt);
        this.verified = Math.min(spec.bytes ?? 0, this.verified + this.vf.raw * dt * this.scale);
        finished = this.verified >= (spec.bytes ?? 0);
        break;
      case "move":
        this.mv.step(dt);
        this.moved = Math.min(spec.bytes ?? 0, this.moved + this.mv.raw * dt * this.scale);
        finished = this.moved >= (spec.bytes ?? 0);
        break;
    }
    while (this.elapsed >= this.nextSample) {
      this.samples.push(this.sampleNow(this.nextSample));
      this.nextSample++;
    }
    if (!finished) return "tick";
    return this.finishPhase(spec);
  }

  private finishPhase(spec: PhaseSpec): "phase" | "done" {
    if (spec.kind === "verify") {
      this.verifyDone = true;
      // Clean verify: there is nothing to repair, so the step disappears from the stepper.
      if (this.init.badFiles === 0) this.plan = this.plan.filter((p) => p.phase !== "repairing");
    }
    this.idx = this.plan.indexOf(spec) + 1;
    this.phaseT = 0;
    const next = this.cur();
    // Entering the install tail: the writes that trailed the network are where the tail starts.
    if (next?.kind === "write") this.written = Math.max(this.written, (next.bytes ?? 0) * WRITE_LAG);
    if (this.done) return "done";
    this.lastPercent = Math.max(this.lastPercent, this.percentNow());
    return "phase";
  }

  private sampleNow(t: number): JobSample {
    const spec = this.cur();
    const net = spec?.kind === "network";
    return {
      t,
      networkBytesPerSecond: net ? Math.round(Math.min(this.rate(this.dl.ema), this.netCap(ratioOf(spec)))) : 0,
      diskBytesPerSecond: Math.round(net ? Math.min(this.diskEma, this.diskCap()) : spec?.kind === "verify" ? this.vf.ema : spec?.kind === "write" ? this.wr.ema : spec?.kind === "move" ? this.mv.ema : 0),
    };
  }

  /** ≈1 Hz samples so far, downsampled to at most `max` points (the backend keeps a ring of the same size). */
  history(max = 600): JobSample[] {
    const s = this.samples;
    if (s.length <= max) return s.slice();
    const out: JobSample[] = [];
    const step = s.length / max;
    for (let i = 0; i < max; i++) out.push(s[Math.min(s.length - 1, Math.floor(i * step))]!);
    return out;
  }

  /** Whole-job percent from weights and the live fraction of the current phase. */
  private percentNow(): number {
    if (this.done) return 100;
    const total = this.plan.reduce((s, p) => s + p.weight, 0) || 1;
    let before = 0;
    for (let i = 0; i < this.idx; i++) before += this.plan[i]!.weight;
    const spec = this.cur()!;
    return clamp(((before + spec.weight * this.phaseFrac(spec)) / total) * 100, 0, 100);
  }

  private phaseFrac(spec: PhaseSpec): number {
    switch (spec.kind) {
      case "timed":
        return clamp(this.phaseT / (spec.seconds || 1), 0, 1);
      case "network":
        return clamp(this.downloaded / (spec.bytes || 1), 0, 1);
      case "write":
        return clamp((this.written - (spec.bytes ?? 0) * WRITE_LAG) / Math.max(1, (spec.bytes ?? 0) * (1 - WRITE_LAG)), 0, 1);
      case "verify":
        return clamp(this.verified / (spec.bytes || 1), 0, 1);
      case "move":
        return clamp(this.moved / (spec.bytes || 1), 0, 1);
    }
  }

  /** Real seconds left in phases after `index`, at nominal speeds. */
  private remainingAfter(index: number): number {
    let t = 0;
    for (const p of this.plan.slice(index + 1)) {
      if (p.kind === "timed") t += p.seconds ?? 0;
      else if (p.kind === "network") t += (p.bytes ?? 0) / (DOWNLOAD.nominal * this.scale);
      else if (p.kind === "verify") t += (p.bytes ?? 0) / (VERIFY.nominal * this.scale);
      else if (p.kind === "write") t += ((p.bytes ?? 0) * (1 - WRITE_LAG)) / (WRITE.nominal * this.scale);
      else t += (p.bytes ?? 0) / (MOVE.nominal * this.scale);
    }
    return t;
  }

  private limiter(spec: PhaseSpec): Limiter {
    if (this.env.paused) return "paused";
    switch (spec.kind) {
      case "network": {
        const cap = this.env.speedLimit;
        if (cap !== null && this.rate(this.dl.raw) >= cap) return "speedLimit";
        if (this.env.mode === "background") return "mode";
        if (this.init.bottleneck) return this.init.bottleneck;
        if (this.dl.dipping) return "cdn";
        return "network";
      }
      case "write":
      case "move":
      case "verify":
        return this.env.mode === "background" ? "mode" : "disk";
      default:
        return "unknown";
    }
  }

  private limiterReason(limiter: Limiter): string | null {
    switch (limiter) {
      case "speedLimit":
        return "Limited by your speed limit.";
      case "mode":
        return "Background mode keeps the download out of your way.";
      case "cdn":
        return "The download server is slow right now; Glider will try another one.";
      case "network":
        return "Limited by your connection or Epic's servers.";
      case "disk":
        return "Limited by your drive. A nearly full or slow drive writes more slowly than your connection delivers.";
      case "paused":
        return "Paused.";
      default:
        return null;
    }
  }

  progress(): JobProgress {
    const last = this.plan.length - 1;
    const done = this.done;
    const index = done ? last : this.idx;
    const spec = this.plan[index]!;
    const kind = spec.kind;
    // Never report a percent below an earlier one, even if a counter is re-based by a plan change.
    const percent = done ? 100 : Math.max(this.lastPercent, this.percentNow());
    this.lastPercent = percent;
    const later = this.remainingAfter(index);
    const phasePercent = done ? 100 : this.phaseFrac(spec) * 100;
    let eta: number | null = null;
    let network: NetworkStats | null = null;
    let disk: DiskStats | null = null;
    let verify: VerifyStats | null = null;
    let filesDone: number | null = null;
    let filesTotal: number | null = null;
    const bytesTotal = this.init.installBytes;

    switch (kind) {
      case "timed":
        eta = spec.phase === "preparing" || done ? null : Math.max(0, (spec.seconds ?? 0) - this.phaseT) + later;
        if (spec.phase === "importing") eta = null;
        break;
      case "network": {
        const total = spec.bytes ?? 0;
        const raw = this.rate(this.dl.ema);
        const netNow = Math.min(raw, this.env.speedLimit ?? Infinity, this.netCap(ratioOf(spec)));
        const avg = this.netTime > 0 ? this.downloaded / (this.netTime * this.scale) : raw;
        network = {
          downloadedBytes: Math.round(this.downloaded),
          totalBytes: total,
          bytesPerSecond: Math.round(done ? 0 : netNow),
          averageBytesPerSecond: Math.round(Math.min(avg, this.netCap(ratioOf(spec)))),
          peakBytesPerSecond: Math.round(Math.max(this.peakNet, raw)),
          cdn: this.env.cdn ?? CDNS[1],
          connections: this.env.mode === "background" ? 4 : this.env.mode === "full" ? 24 : 12,
        };
        disk = {
          writtenBytes: Math.round(this.written),
          totalBytes: spec.writeBytes ?? 0,
          bytesPerSecond: Math.round(done ? 0 : Math.min(this.diskEma, this.diskCap(), netNow * ratioOf(spec))),
          averageBytesPerSecond: Math.round(Math.min(this.netTime > 0 ? this.written / (this.netTime * this.scale) : this.diskEma, this.diskCap())),
          peakBytesPerSecond: Math.round(Math.max(this.peakDisk, this.diskEma)),
          installSizeBytes: this.init.installBytes,
        };
        if (spec.phase === "repairing") {
          verify = this.verifyStats(this.plan.find((p) => p.kind === "verify")?.bytes ?? bytesTotal, this.init.badFiles, this.fixed);
        }
        filesTotal = spec.files ?? null;
        filesDone = filesTotal === null ? null : Math.floor((filesTotal * this.downloaded) / Math.max(1, total));
        eta = (total - this.downloaded) / Math.max(1, Math.min(this.dl.slow, this.env.speedLimit ?? Infinity, this.netCap(ratioOf(spec))) * this.scale) + later;
        break;
      }
      case "write": {
        const total = spec.bytes ?? 0;
        disk = {
          writtenBytes: Math.round(this.written),
          totalBytes: total,
          bytesPerSecond: Math.round(done ? 0 : this.rate(this.wr.ema)),
          averageBytesPerSecond: Math.round(this.wr.slow),
          peakBytesPerSecond: Math.round(Math.max(this.peakDisk, this.wr.ema)),
          installSizeBytes: this.init.installBytes,
        };
        eta = (total - this.written) / Math.max(1, this.wr.slow * this.scale) + later;
        break;
      }
      case "verify": {
        const total = spec.bytes ?? 0;
        const found = Math.floor(this.init.badFiles * Math.pow(this.verified / Math.max(1, total), 1.3));
        verify = this.verifyStats(total, found, 0);
        filesTotal = spec.files ?? null;
        filesDone = verify.filesChecked;
        eta = (total - this.verified) / Math.max(1, this.vf.slow * this.scale) + later;
        break;
      }
      case "move": {
        const total = spec.bytes ?? 0;
        disk = {
          writtenBytes: Math.round(this.moved),
          totalBytes: total,
          bytesPerSecond: Math.round(done ? 0 : this.mv.ema),
          averageBytesPerSecond: Math.round(this.mv.slow),
          peakBytesPerSecond: Math.round(Math.max(this.peakDisk, this.mv.ema)),
          installSizeBytes: total,
        };
        filesTotal = spec.files ?? null;
        filesDone = filesTotal === null ? null : Math.floor((filesTotal * this.moved) / Math.max(1, total));
        eta = (total - this.moved) / Math.max(1, this.mv.slow * this.scale) + later;
        break;
      }
    }
    if (done) eta = 0;
    return {
      jobId: this.jobId,
      appName: this.appName,
      phase: spec.phase,
      percent: Math.round(percent * 100) / 100,
      phasePercent: Math.round(phasePercent * 100) / 100,
      elapsedSeconds: Math.round(this.elapsed),
      etaSeconds: eta === null ? null : Math.round(eta * 10) / 10,
      network,
      disk,
      verify,
      filesDone,
      filesTotal,
      mode: this.env.mode,
      limiter: done ? "unknown" : this.limiter(spec),
      limiterReason: done ? null : this.limiterReason(this.limiter(spec)),
    };
  }

  private verifyStats(total: number, bad: number, fixed: number): VerifyStats {
    const spec = this.plan.find((p) => p.kind === "verify") ?? ({ files: filesOf(this.init.installBytes) } as PhaseSpec);
    const doneVerify = this.verifyDone || this.cur()?.kind !== "verify";
    const checkedBytes = doneVerify ? total : Math.round(this.verified);
    const files = spec.files ?? filesOf(total);
    return {
      filesChecked: doneVerify ? files : Math.floor((files * this.verified) / Math.max(1, total)),
      filesTotal: files,
      bytesChecked: checkedBytes,
      bytesTotal: total,
      readBytesPerSecond: doneVerify ? 0 : Math.round(this.vf.ema),
      badFiles: bad,
      fixedFiles: fixed,
    };
  }
}

function ratioOf(spec: PhaseSpec | undefined): number {
  return spec && spec.bytes ? (spec.writeBytes ?? 0) / spec.bytes : 1;
}

/** Choose the byte-speed scale: the global time scale for big jobs, but small jobs still take a watchable ~12 s. */
export function chooseScale(timeScale: number, explicit: boolean, init: Omit<JobSimInit, "scale">): number {
  if (explicit) return timeScale;
  const plan = buildPlan({ ...init, scale: 1 });
  let nominal = 0;
  for (const p of plan) {
    if (p.kind === "network") nominal += (p.bytes ?? 0) / DOWNLOAD.nominal;
    else if (p.kind === "verify") nominal += (p.bytes ?? 0) / VERIFY.nominal;
    else if (p.kind === "write") nominal += ((p.bytes ?? 0) * (1 - WRITE_LAG)) / WRITE.nominal;
    else if (p.kind === "move") nominal += (p.bytes ?? 0) / MOVE.nominal;
  }
  return Math.min(timeScale, Math.max(1, nominal / MIN_REAL_SECONDS));
}

export function buildJobSim(init: Omit<JobSimInit, "scale">, timeScale: number, explicitScale: boolean): JobSim {
  return new JobSim({ ...init, scale: chooseScale(timeScale, explicitScale, init) });
}
