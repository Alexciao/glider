import type { SettingsFocus } from "./router";
import type { DownloadMode, JobInfo, JobKind, JobPhase, JobProgress } from "../ipc/contract.gen";
import { driveOf, formatBytes, formatBytesPrecise, formatClock, formatCount, formatDuration, formatEta, formatSpeed, prettyVersion } from "./format";

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

export type ChartKind = "network+disk" | "disk" | null;

/** One live speed. Doubles as the chart's legend: `series` names the line it labels. */
export interface Readout {
  key: "network" | "disk" | "read" | "copy";
  /** "Download", "Disk write", "Reading", "Copying"; with ", average" while the job is not moving. */
  label: string;
  bytesPerSecond: number;
  /** The job's average (paused, waiting), not a live speed. */
  average: boolean;
  /** The chart line this readout is the legend of; null when the phase has no chart. */
  series: "network" | "disk" | null;
}

export interface Figures {
  /** The phase's own "done of total" ("15.81 GB of 37.64 GB"); `unit` completes the sentence. Null when there is none. */
  headline: string | null;
  /** What the headline's bytes did: "downloaded", "written", "copied", "checked". */
  unit: string | null;
  /** Live speeds, in the order they are drawn. Empty when the phase has none. */
  readouts: Readout[];
  /** Verifying only: "3 damaged so far", once something is. */
  damaged: string | null;
  /** A chart only while bytes are moving (downloading, repairing, installing). */
  chart: ChartKind;
  /** Which throughput figure the speed status talks about. */
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

/** "15.81 GB of 37.64 GB": the numbers a bar is made of, with nothing after them. */
const ofBytes = (done: number, total: number): string => `${formatBytesPrecise(done)} of ${formatBytesPrecise(total)}`;

/**
 * A speed readout. While the job is moving it is the live figure; while it is not (paused, waiting) it is the job's
 * average and says so, so the row keeps its place and the card does not jump when Pause is pressed. A speed that has no
 * value yet (an average of 0) is left out rather than shown as "0 B/s, average".
 */
function readout(key: Readout["key"], label: string, series: Readout["series"], live: number, average: number, moving: boolean): Readout | null {
  const value = moving ? live : average;
  if (!moving && !(value > 0)) return null;
  return { key, label: moving ? label : `${label}, average`, bytesPerSecond: Math.max(0, value), average: !moving, series };
}

const present = <T>(list: (T | null)[]): T[] => list.filter((x): x is T => x !== null);

export function figuresOf(job: JobInfo, p: JobProgress | null): Figures {
  const moving = job.status === "running";
  const phase = p?.phase ?? job.phase;
  const net = p?.network ?? null;
  const disk = p?.disk ?? null;
  const verify = p?.verify ?? null;

  // Network phases: downloading, and the re-download step of a repair.
  if (net && (phase === "downloading" || phase === "repairing")) {
    return {
      headline: ofBytes(net.downloadedBytes, net.totalBytes),
      unit: "downloaded",
      readouts: present([
        readout("network", "Download", "network", net.bytesPerSecond, net.averageBytesPerSecond, moving),
        disk && readout("disk", "Disk write", "disk", disk.bytesPerSecond, disk.averageBytesPerSecond, moving),
      ]),
      damaged: null,
      chart: "network+disk",
      throughput: "network",
    };
  }

  // Verify: local reads only. Never any network figure, and no chart: a verify is one steady read.
  if (verify && phase === "verifying") {
    const average = p && p.elapsedSeconds > 0 ? verify.bytesChecked / p.elapsedSeconds : 0;
    return {
      headline: ofBytes(verify.bytesChecked, verify.bytesTotal),
      unit: "checked",
      readouts: present([readout("read", "Reading", null, verify.readBytesPerSecond, average, moving)]),
      damaged: verify.badFiles > 0 ? `${formatCount(verify.badFiles)} damaged so far` : null,
      chart: null,
      throughput: "read",
    };
  }

  // The install tail: the download is done, files are still landing on the drive.
  if (disk && phase === "installing") {
    return {
      headline: ofBytes(disk.writtenBytes, disk.totalBytes),
      unit: "written",
      readouts: present([readout("disk", "Disk write", "disk", disk.bytesPerSecond, disk.averageBytesPerSecond, moving)]),
      damaged: null,
      chart: "disk",
      throughput: "write",
    };
  }

  // Moving files: one copy, no chart.
  if (disk && phase === "moving") {
    return {
      headline: ofBytes(disk.writtenBytes, disk.totalBytes),
      unit: "copied",
      readouts: present([readout("copy", "Copying", null, disk.bytesPerSecond, disk.averageBytesPerSecond, moving)]),
      damaged: null,
      chart: null,
      throughput: "write",
    };
  }

  // Short timed steps (preparing, finalizing, removing, importing): the card shows the clock next to the percentage.
  return { headline: null, unit: null, readouts: [], damaged: null, chart: null, throughput: null };
}

/** The line at the bar's right edge: "15.81 GB of 37.64 GB downloaded". */
export const headlineOf = (f: Figures): string | null => (f.headline ? (f.unit ? `${f.headline} ${f.unit}` : f.headline) : null);

/**
 * The time beside the big percentage: what is left when there is an estimate (elapsed moves into its tooltip), else the
 * elapsed time itself. Null before the job has started.
 */
export function timeOf(job: JobInfo, p: JobProgress | null): { text: string; tip: string | null } | null {
  if (!p) return null;
  const elapsed = `${formatClock(p.elapsedSeconds)} elapsed`;
  if (job.status === "running" && p.etaSeconds != null) return { text: `${formatEta(p.etaSeconds)} left`, tip: elapsed };
  return { text: elapsed, tip: null };
}

// ------------------------------------------------------------------------------------------------ speed status

export type SpeedCause = "playing" | "disk" | "cpu" | "speedLimit" | "mode" | "cdn";

/** The row in Settings → Downloads that can change a cause. */
/** The Settings → Downloads row a speed explanation links to (the `settings?focus=` route). */
export type SettingFocus = SettingsFocus;

export interface SpeedStatus {
  cause: SpeedCause;
  /** "slow": something the user did not choose is holding the speed back (amber). "limit": a choice they made (muted). */
  kind: "slow" | "limit";
  /** Two or three words: "Slow download", "Speed limit", "Background mode". */
  label: string;
  /** One line, for the tooltip: "Drive D: writes about 117 MB/s, slower than your connection." */
  reason: string;
  /** The setting that changes it, for the dialog's link; null when only the PC or the network can. */
  setting: SettingFocus | null;
}

export interface SpeedContext {
  speedLimitBytesPerSecond: number | null;
  /** The job's target path (or install folder), for naming the drive. */
  path?: string | null;
  /** The mode the user chose, and the queue's current one with its reason (`QueueState`). */
  chosenMode: DownloadMode;
  effectiveMode: DownloadMode;
  modeReason: string | null;
}

/**
 * What is holding this download's speed back, or null when nothing worth saying is (the connection being the limit is
 * the normal case and needs no comment). One cause, picked in this order: a mode change Glider made on its own, the disk,
 * the processor, the user's speed limit, the user's mode, the download server. Idle boost is not a limit: it only adds
 * speed. Null when the job is not running or its phase has no network throughput to hold back.
 */
export function speedStatusOf(job: JobInfo, p: JobProgress | null, throughput: Figures["throughput"], ctx: SpeedContext): SpeedStatus | null {
  if (!p || !throughput || job.status !== "running") return null;
  const drive = driveOf(ctx.path);
  const where = drive ? `Drive ${drive}` : "This drive";
  // Glider throttled itself (the user's "while a game runs" choice): the cause is the game, whatever the limiter measured.
  if (throughput === "network" && ctx.effectiveMode !== ctx.chosenMode && ctx.effectiveMode === "background" && ctx.modeReason) {
    return { cause: "playing", kind: "limit", label: "Throttled while playing", reason: `${ctx.modeReason.replace(/[.\s]+$/, "")}.`, setting: "whilePlaying" };
  }
  switch (p.limiter) {
    case "disk": {
      // Only a drive that holds back a live download is worth saying: reading files or writing the install tail is the work itself.
      if (!p.network) return null;
      const avg = p.disk?.averageBytesPerSecond ?? p.disk?.bytesPerSecond;
      return {
        cause: "disk",
        kind: "slow",
        label: "Slow download",
        reason: avg && avg > 0 ? `${where} writes about ${formatSpeed(avg)}, slower than your connection.` : `${where} is slower than your connection.`,
        setting: null,
      };
    }
    case "cpu":
      return { cause: "cpu", kind: "slow", label: "Slow download", reason: "The processor is the limit while files unpack.", setting: null };
    case "speedLimit":
      return {
        cause: "speedLimit",
        kind: "limit",
        label: "Speed limit",
        reason: ctx.speedLimitBytesPerSecond ? `Held to your ${formatSpeed(ctx.speedLimitBytesPerSecond)} limit.` : "Held to your speed limit.",
        setting: "speedLimit",
      };
    case "mode":
      return { cause: "mode", kind: "limit", label: `${MODE_LABEL[p.mode]} mode`, reason: `${MODE_LABEL[p.mode]} mode is easing off to keep your PC responsive.`, setting: "downloadMode" };
    case "cdn": {
      const cdn = p.network?.cdn;
      return { cause: "cdn", kind: "slow", label: "Slow download", reason: cdn ? `${cdnName(cdn)} is responding slowly right now.` : "The download server is responding slowly right now.", setting: "cdn" };
    }
    default:
      return null;
  }
}

// ------------------------------------------------------------------------------------------------ the full reason

export interface SpeedExplanation {
  title: string;
  /** What is limiting, in one or two sentences. */
  lead: string;
  /** What was measured, one short sentence each: only numbers that exist. */
  facts: string[];
  /** What the user can do about it. */
  advice: string[];
  /** The Settings row that changes it, with the button's text; null when only the PC or the network can. */
  link: { focus: SettingFocus; label: string } | null;
}

/**
 * The dialog behind the speed status: why, with the numbers measured for this job, and what the user can do. The numbers
 * come from the job's own stats (what the connection has reached, what the drive writes), never from guesses.
 */
export function explainSpeed(status: SpeedStatus, p: JobProgress | null, ctx: SpeedContext): SpeedExplanation {
  const now = p?.network?.bytesPerSecond ?? 0;
  const reached = p?.network?.peakBytesPerSecond ?? 0;
  const drive = driveOf(ctx.path);
  const where = drive ? `Drive ${drive}` : "Your drive";
  const facts: string[] = [];
  // The connection's own ceiling is worth a line only when it is clearly above what the job gets now.
  const ceiling = (against: number) => {
    if (reached > against * 1.25 && reached > 0) facts.push(`Your connection has reached about ${formatSpeed(reached)}.`);
  };
  switch (status.cause) {
    case "disk": {
      const writes = p?.disk?.averageBytesPerSecond ?? p?.disk?.bytesPerSecond ?? 0;
      if (writes > 0) facts.push(`${where} writes about ${formatSpeed(writes)}.`);
      if (now > 0) facts.push(`Glider is downloading at ${formatSpeed(now)}, the pace the drive allows.`);
      ceiling(Math.max(now, writes));
      return {
        title: "Your drive is limiting this download",
        lead: "Files can't be written as fast as your connection delivers them, so Glider has to slow down to match the drive.",
        facts,
        advice: [
          "Free up space. Drives slow down as they fill up.",
          "Install games on a faster drive. You can move an installed game from its page.",
          "Pause other work on the same drive: backups, scans and other downloads.",
        ],
        link: null,
      };
    }
    case "cpu":
      if (now > 0) facts.push(`Glider is downloading at ${formatSpeed(now)}.`);
      ceiling(now);
      return {
        title: "Your processor is limiting this download",
        lead: "Unpacking files takes more processor time than your PC has to spare right now.",
        facts,
        advice: ["Close heavy programs while this finishes.", "It is temporary: speed recovers once the files are unpacked."],
        link: null,
      };
    case "cdn": {
      const host = p?.network?.cdn;
      if (host) facts.push(`Server in use: ${cdnName(host)}.`);
      if (now > 0) facts.push(`Glider is downloading at ${formatSpeed(now)}.`);
      ceiling(now);
      return {
        title: "The download server is slow",
        lead: "The server isn't sending data as fast as usual. Glider tries another one on its own if it stays slow.",
        facts,
        advice: ["It usually clears up by itself within a few minutes.", "Or choose a download server yourself."],
        link: { focus: "cdn", label: "Choose a server" },
      };
    }
    case "speedLimit":
      if (ctx.speedLimitBytesPerSecond) facts.push(`Your limit is ${formatSpeed(ctx.speedLimitBytesPerSecond)}.`);
      ceiling(ctx.speedLimitBytesPerSecond ?? now);
      return {
        title: "Held to your speed limit",
        lead: "Downloads stay at the limit you set, so other things on your network keep their share.",
        facts,
        advice: ["Raise or remove the limit to download at full speed."],
        link: { focus: "speedLimit", label: "Change speed limit" },
      };
    case "mode":
      if (now > 0) facts.push(`Glider is downloading at ${formatSpeed(now)}.`);
      ceiling(now);
      return {
        title: `${MODE_LABEL[p?.mode ?? ctx.effectiveMode]} mode is on`,
        lead: "Glider downloads gently with few connections and low priority, so the rest of your PC stays responsive.",
        facts,
        advice: ["Balanced downloads at full network speed and stays gentle on the disk. Full uses everything this PC has."],
        link: { focus: "downloadMode", label: "Change download mode" },
      };
    case "playing":
      if (now > 0) facts.push(`Glider is downloading at ${formatSpeed(now)}.`);
      ceiling(now);
      return {
        title: "Slowed down while you play",
        lead: status.reason,
        facts,
        advice: ["Speed returns to your chosen mode when the game closes.", "Or pick what Glider does while a game runs: pause, slow down or carry on."],
        link: { focus: "whilePlaying", label: "Change this setting" },
      };
  }
}

/** "took 4 min" for finished work. */
export const tookText = (job: JobInfo): string | null => {
  if (!job.finishedAt) return null;
  const secs = (Date.parse(job.finishedAt) - Date.parse(job.startedAt ?? job.createdAt)) / 1000;
  return secs > 0 ? formatDuration(secs) : null;
};
