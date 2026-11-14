import type { DownloadMode, JobInfo, JobKind, JobPhase, JobProgress, Limiter } from "../ipc/contract.gen";
import type { StatGroupData, StatRowData } from "../ui/Stats";
import { driveOf, formatBytes, formatBytesPrecise, formatClock, formatCount, formatDuration, formatSpeed, formatSpeedCompact, prettyVersion } from "./format";

/**
 * Everything the download card says, as pure data: one function per question ("what is this job doing?",
 * "what are its numbers?", "what is limiting it?"), so the card is layout only and phases cannot leak into each other
 * (a verify never shows a download speed; installing shows disk writes; docs/v0.2-design.md §5).
 * Rule of the module: a figure appears only when it has a real value in this phase, and never twice.
 */

/** The state a job is in, one word. Also the stepper's labels. */
export const PHASE_STATE: Record<JobPhase, string> = {
  preparing: "Preparing",
  verifying: "Verifying",
  repairing: "Repairing",
  downloading: "Downloading",
  installing: "Installing",
  finalizing: "Finalizing",
  moving: "Moving",
  removing: "Removing",
  importing: "Importing",
};

export const MODE_LABEL: Record<DownloadMode, string> = { background: "Background", balanced: "Balanced", full: "Full" };

/** The phase whose name already says what the job is ("Verifying" for a verify): the kind adds nothing there. */
const SELF_NAMING: Partial<Record<JobKind, JobPhase>> = {
  verify: "verifying",
  repair: "repairing",
  move: "moving",
  uninstall: "removing",
  import: "importing",
};

export const KIND_NOUN: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verify",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
  modify: "Change components",
  overlay: "Epic overlay",
};

export interface JobStateLabel {
  /** What the job is doing now: "Downloading", "Paused", "Queued". */
  label: string;
  tone: "accent" | "warning" | "neutral";
}

export function stateOf(job: JobInfo, p: JobProgress | null): JobStateLabel {
  switch (job.status) {
    case "paused":
      return { label: "Paused", tone: "warning" };
    case "waiting":
      return { label: "Waiting", tone: "warning" };
    case "queued":
      return { label: "Queued", tone: "neutral" };
    default: {
      const phase = p?.phase ?? job.phase;
      return { label: phase ? PHASE_STATE[phase] : "Starting", tone: "accent" };
    }
  }
}

/**
 * The one muted line next to the state: what kind of job this is (when the state doesn't already say so) and the detail
 * that distinguishes it. Never a restatement of the title or the state.
 */
export function metaOf(job: JobInfo, p: JobProgress | null): string | null {
  const phase = p?.phase ?? job.phase;
  if (phase === "repairing" && p?.verify && p.verify.badFiles > 0) {
    const bytes = p.network?.totalBytes;
    return `${formatCount(p.verify.badFiles)} ${p.verify.badFiles === 1 ? "file" : "files"} to fix${bytes ? ` · ${formatBytes(bytes)}` : ""}`;
  }
  const parts: string[] = [];
  if (!phase || SELF_NAMING[job.kind] !== phase) parts.push(KIND_NOUN[job.kind]);
  if (job.kind === "update" && job.fromVersion && job.toVersion) parts.push(`${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`);
  else if ((job.kind === "install" || job.kind === "update") && job.toVersion) parts.push(prettyVersion(job.toVersion));
  return parts.length ? parts.join(" · ") : null;
}

// ------------------------------------------------------------------------------------------------ figures

export type StatRow = StatRowData;
export type StatGroup = StatGroupData;

export type ChartKind = "network+disk" | "disk" | null;

export interface Figures {
  /** The phase's own "done of total" ("2.41 GB of 5.12 GB"); the card adds the percentage and time left. Null when there is none. */
  headline: string | null;
  groups: StatGroup[];
  /** A chart only while bytes are moving (downloading, repairing, installing). */
  chart: ChartKind;
  /** Which throughput figure the limiter talks about. */
  throughput: "network" | "write" | "read" | null;
}

/** "epicgames-download1.akamaized.net" → "Akamai": the provider, not the host name. Unknown hosts pass through. */
export function cdnName(host: string): string {
  const h = host.toLowerCase();
  if (h.includes("akamai")) return "Akamai";
  if (h.includes("fastly")) return "Fastly";
  if (h.includes("cloudflare")) return "Cloudflare";
  if (h === "download.epicgames.com") return "Epic";
  return host;
}

const row = (key: string, label: string, value: string, extra: Partial<StatRow> = {}): StatRow => ({ key, label, value, ...extra });
const of = (done: number | null, total: number | null): string | null => (done == null || total == null ? null : `${formatCount(done)} of ${formatCount(total)}`);
const known = (bytesPerSecond: number): string | null => (bytesPerSecond > 0 ? formatSpeed(bytesPerSecond) : null);

/** Only rows with something to say: no "—", no empty groups. */
function prune(groups: StatGroup[]): StatGroup[] {
  return groups.filter((g) => g.rows.length > 0);
}

/** Add a row only when the value exists. */
function add(rows: StatRow[], key: string, label: string, value: string | null, extra: Partial<StatRow> = {}): void {
  if (value) rows.push(row(key, label, value, extra));
}

const group = (id: string, title: string, rows: StatRow[]): StatGroup => ({ id, title, rows });

export function figuresOf(job: JobInfo, p: JobProgress | null): Figures {
  const running = job.status === "running";
  const phase = p?.phase ?? job.phase;
  const net = p?.network ?? null;
  const disk = p?.disk ?? null;
  const verify = p?.verify ?? null;
  const files = of(p?.filesDone ?? null, p?.filesTotal ?? null);
  const elapsed = p ? [row("elapsed", "Elapsed", formatClock(p.elapsedSeconds))] : [];
  // The mode lives in the page header and the limit in the limiter line: neither is repeated here.

  // Network phases: downloading, and the re-download step of a repair.
  if (net && (phase === "downloading" || phase === "repairing")) {
    const progress: StatRow[] = [];
    add(progress, "files", "Files", files);
    if (phase === "repairing" && verify) {
      add(progress, "damaged", "Damaged", formatCount(verify.badFiles), { tone: verify.badFiles > 0 ? "warning" : undefined });
      add(progress, "fixed", "Fixed", `${formatCount(verify.fixedFiles)} of ${formatCount(verify.badFiles)}`);
    }
    const speed: StatRow[] = [];
    // Average and peak are the chart's own annotations (same numbers, drawn where they apply): not repeated in the list.
    if (running) add(speed, "now", "Now", known(net.bytesPerSecond));
    if (net.cdn) add(speed, "cdn", "Server", cdnName(net.cdn), { title: net.cdn });
    return {
      headline: `${formatBytesPrecise(net.downloadedBytes)} of ${formatBytesPrecise(net.totalBytes)}`,
      groups: prune([group("progress", "Progress", progress), group("speed", "Speed", speed), group("time", "Time", elapsed), diskGroup(disk, running, true)]),
      chart: "network+disk",
      throughput: "network",
    };
  }

  // Verify: local reads only. Never any network figure.
  if (verify && phase === "verifying") {
    const progress: StatRow[] = [];
    add(progress, "files", "Files checked", files ?? `${formatCount(verify.filesChecked)} of ${formatCount(verify.filesTotal)}`);
    if (verify.badFiles > 0) add(progress, "damaged", "Damaged", formatCount(verify.badFiles), { tone: "warning" });
    const speed: StatRow[] = [];
    if (running) add(speed, "read", "Read speed", known(verify.readBytesPerSecond));
    return {
      headline: `${formatBytesPrecise(verify.bytesChecked)} of ${formatBytesPrecise(verify.bytesTotal)} checked`,
      groups: prune([group("progress", "Progress", progress), group("speed", "Speed", speed), group("time", "Time", elapsed)]),
      chart: null,
      throughput: "read",
    };
  }

  // Disk-only phases: the install tail, and moving files.
  if (disk && (phase === "installing" || phase === "moving")) {
    const progress: StatRow[] = [];
    add(progress, "files", "Files", files);
    const speed: StatRow[] = [];
    if (running) add(speed, "now", "Now", known(disk.bytesPerSecond));
    return {
      headline: `${formatBytesPrecise(disk.writtenBytes)} of ${formatBytesPrecise(disk.totalBytes)}`,
      groups: prune([group("progress", "Progress", progress), group("speed", "Speed", speed), group("time", "Time", elapsed), phase === "installing" ? diskGroup(disk, running, false) : group("disk", "Disk", [])]),
      chart: phase === "installing" ? "disk" : null,
      throughput: "write",
    };
  }

  // Short timed steps (preparing, finalizing, removing, importing): the card shows the clock next to the percentage.
  const progress: StatRow[] = [];
  add(progress, "files", "Files", files);
  return { headline: null, groups: prune([group("progress", "Progress", progress)]), chart: null, throughput: null };
}

/** The disk side of a download: how fast the drive is writing, how much has landed, how big the install ends up. */
function diskGroup(disk: JobProgress["disk"], running: boolean, withSpeed: boolean): StatGroup {
  const rows: StatRow[] = [];
  if (!disk) return group("disk", "Disk", rows);
  const final = disk.installSizeBytes;
  if (withSpeed && running) add(rows, "write", "Write speed", known(disk.bytesPerSecond));
  rows.push(row("written", "Written", formatBytesPrecise(disk.writtenBytes)));
  if (final != null) rows.push(row("final", "Final size", formatBytesPrecise(final)));
  return group("disk", "Disk", rows);
}

// ------------------------------------------------------------------------------------------------ limiter

export interface LimiterNote {
  limiter: Limiter;
  /** One short line: "Disk is the limit: drive D: writes about 8 MB/s." */
  text: string;
  /** Disk and CPU limits are about the PC (the user can act on them); the rest is the outside world or a setting. */
  tone: "neutral" | "warning";
}

export interface LimiterContext {
  speedLimitBytesPerSecond: number | null;
  /** The job's target path, for naming the drive. */
  path?: string | null;
}

/**
 * What is limiting this job's speed, in one short line, when it is something other than the network (the network being
 * the limit is the normal case and needs no comment). Null when paused, unknown, or the phase has no throughput.
 * The disk case follows docs/download-perf.md: the drive is very often the real limit, and the user should be told.
 */
export function describeLimiter(p: JobProgress | null, throughput: Figures["throughput"], ctx: LimiterContext): LimiterNote | null {
  if (!p || !throughput) return null;
  const drive = driveOf(ctx.path);
  switch (p.limiter) {
    case "cdn": {
      const cdn = p.network?.cdn;
      return { limiter: "cdn", text: cdn ? `${cdnName(cdn)} is responding slowly right now.` : "The server is responding slowly right now.", tone: "neutral" };
    }
    case "cpu":
      return { limiter: "cpu", text: "Processor is the limit while files unpack.", tone: "warning" };
    case "disk": {
      // Only a drive that holds back a live download is worth saying: reading files or writing the install tail is the work itself.
      if (!p.network) return null;
      const avg = p.disk?.averageBytesPerSecond ?? p.disk?.bytesPerSecond;
      const where = drive ? `drive ${drive}` : "this drive";
      return {
        limiter: "disk",
        text: avg && avg > 0 ? `Disk is the limit: ${where} writes about ${formatSpeedCompact(avg)}.` : `Disk is the limit: ${where} is slower than your connection.`,
        tone: "warning",
      };
    }
    case "speedLimit":
      return { limiter: "speedLimit", text: ctx.speedLimitBytesPerSecond ? `Held to your ${formatSpeedCompact(ctx.speedLimitBytesPerSecond)} limit.` : "Held to your speed limit.", tone: "neutral" };
    case "mode":
      return { limiter: "mode", text: `${MODE_LABEL[p.mode]} mode is easing off to keep your PC responsive.`, tone: "neutral" };
    default:
      return null;
  }
}

/** "took 4 min" for finished work. */
export const tookText = (job: JobInfo): string | null => {
  if (!job.finishedAt) return null;
  const secs = (Date.parse(job.finishedAt) - Date.parse(job.startedAt ?? job.createdAt)) / 1000;
  return secs > 0 ? formatDuration(secs) : null;
};
