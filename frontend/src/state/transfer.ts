import type { SettingsFocus } from "./router";
import type { DownloadMode, HoldInfo, JobInfo, JobKind, JobPhase, JobProgress } from "../ipc/contract.gen";
import { driveOf, formatDuration, formatEta, prettyVersion } from "./format";

/**
 * Everything the download card says, as pure data (docs/design-download-card.md §5, §7). One function answers one question
 * ("what is the headline?", "what does the step line say?", "what limits the speed?"), so the card is layout only and phases
 * cannot leak into each other (a verify never shows a download speed). Rule of the module: a figure appears only when it has a
 * real value in this phase, never twice, and a time phrase is one phrase ("1 min left when you resume"), never stacked words.
 */

/** The state a job is in, one word (the activity pill's labels). */
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

// ------------------------------------------------------------------------------------------------ number formats

const UNITS = ["B", "KB", "MB", "GB", "TB"];
function scale(n: number): { v: number; u: number } {
  let u = 0;
  let v = Math.max(0, n);
  while (v >= 1024 && u < UNITS.length - 1) {
    v /= 1024;
    u++;
  }
  return { v, u };
}
const digits = (v: number): string => (v >= 100 ? Math.round(v).toString() : v.toFixed(1));

/** "15.8 of 37.6 GB" (the unit once when both sides share it), "659 MB of 1.5 GB" otherwise. */
export function ofBytes(done: number, total: number): string {
  const a = scale(done);
  const b = scale(total);
  if (a.u === b.u) return `${digits(a.v)} of ${digits(b.v)} ${UNITS[b.u]}`;
  return `${digits(a.v)} ${UNITS[a.u]} of ${digits(b.v)} ${UNITS[b.u]}`;
}

/** "3.9 MB/s", "247 MB/s": one decimal under 100, whole numbers from 100 (it changes less often, so it never flickers). Null when there is no speed. */
export function speedText(bytesPerSecond: number | null | undefined): string | null {
  if (bytesPerSecond == null || !(bytesPerSecond > 0)) return null;
  const { v, u } = scale(bytesPerSecond);
  return `${digits(v)} ${UNITS[u]}/s`;
}

/** The time left in words a screen reader says well: "less than a minute", "7 minutes", "1 hour 37 minutes". */
export function etaWords(seconds: number): string {
  if (seconds < 45) return "less than a minute";
  const minutes = Math.round(seconds / 60);
  const plural = (n: number, w: string) => `${n} ${w}${n === 1 ? "" : "s"}`;
  if (minutes < 60) return plural(minutes, "minute");
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h < 24) return m === 0 ? plural(h, "hour") : `${plural(h, "hour")} ${plural(m, "minute")}`;
  return `${plural(Math.floor(h / 24), "day")} ${plural(h % 24, "hour")}`;
}

// ------------------------------------------------------------------------------------------------ speed status

export type SpeedCause = "playing" | "disk" | "cpu" | "speedLimit" | "mode" | "cdn";

/** The Settings → Downloads row a speed explanation links to (the `settings?focus=` route). */
export type SettingFocus = SettingsFocus;

export interface SpeedStatus {
  cause: SpeedCause;
  /** "slow": something the user did not choose is holding the speed back (amber). "limit": a choice they made (neutral). */
  kind: "slow" | "limit";
  /** One line: Core's sentence for a throttle ("Background mode while Fortnite is running."). */
  reason: string;
  /** The setting that changes it; null when only the PC or the network can. */
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

/** "epicgames-download1.akamaized.net" → "Akamai": the provider, not the host name. Unknown hosts pass through. */
export function cdnName(host: string): string {
  const h = host.toLowerCase();
  if (h.includes("akamai")) return "Akamai";
  if (h.includes("fastly")) return "Fastly";
  if (h.includes("cloudflare")) return "Cloudflare";
  if (h === "download.epicgames.com") return "Epic";
  return host;
}

/**
 * What is holding this download's speed back, or null when nothing worth saying is (the connection being the limit is
 * the normal case and needs no comment). One cause, picked in this order: a mode change Glider made on its own, the disk,
 * the processor, the user's speed limit, the user's mode, the download server. Idle boost is not a limit: it only adds
 * speed. Null when the job is not running or its phase has no network throughput to hold back.
 */
export function speedStatusOf(job: JobInfo, p: JobProgress | null, throughput: "network" | "write" | "read" | null, ctx: SpeedContext): SpeedStatus | null {
  if (!p || !throughput || job.status !== "running") return null;
  // Glider throttled itself (the user's "while a game runs" choice): the cause is the game, whatever the limiter measured.
  if (throughput === "network" && ctx.effectiveMode !== ctx.chosenMode && ctx.effectiveMode === "background" && ctx.modeReason) {
    return { cause: "playing", kind: "limit", reason: `${ctx.modeReason.replace(/[.\s]+$/, "")}.`, setting: "whilePlaying" };
  }
  switch (p.limiter) {
    case "disk":
      // Only a drive that holds back a live download is worth saying: reading files or writing the install tail is the work itself.
      return p.network ? { cause: "disk", kind: "slow", reason: "", setting: null } : null;
    case "cpu":
      return { cause: "cpu", kind: "slow", reason: "", setting: null };
    case "speedLimit":
      return { cause: "speedLimit", kind: "limit", reason: "", setting: "speedLimit" };
    case "mode":
      return { cause: "mode", kind: "limit", reason: "", setting: "downloadMode" };
    case "cdn":
      return { cause: "cdn", kind: "slow", reason: "", setting: "cdn" };
    default:
      return null;
  }
}

/** The short name of the cause, on the disclosure: names the cause, never a verdict. */
export function causeLabel(status: SpeedStatus, ctx: SpeedContext, p: JobProgress | null): string {
  switch (status.cause) {
    case "disk": {
      const d = driveOf(ctx.path);
      return d ? `Limited by drive ${d}` : "Limited by your drive";
    }
    case "cpu":
      return "Limited by the processor";
    case "cdn":
      return "Server is slow";
    case "speedLimit": {
      const t = speedText(ctx.speedLimitBytesPerSecond);
      return t ? `Your ${t} limit` : "Your speed limit";
    }
    case "mode":
      return `${MODE_LABEL[p?.mode ?? ctx.effectiveMode]} mode`;
    case "playing":
      return "Slowed while you play";
  }
}

export interface Explanation {
  title: string;
  body: string;
  advice: string[];
  /** The setting that changes it, with the button's text; null when only the PC or the network can. */
  link: { focus: SettingFocus; label: string } | null;
}

/** What is behind the disclosure, per cause: why, what to do, and the one setting that changes it. Copy: spec §5.5. */
export function explainCause(status: SpeedStatus, ctx: SpeedContext): Explanation {
  const drive = driveOf(ctx.path) || "your drive";
  switch (status.cause) {
    case "disk":
      return {
        title: `Drive ${drive} is the limit`,
        body: "It can't write game files as fast as your connection delivers them, so Glider downloads at the pace the drive allows.",
        advice: [`Free up space on ${drive}. Drives slow down as they fill up.`, `Pause backups, scans or other downloads that use ${drive}.`, "Put big games on a faster drive. You can move this one later from its page."],
        link: null,
      };
    case "cpu":
      return { title: "The processor is busy", body: "Unpacking files needs more processor time than your PC has free right now. Speed comes back as it settles.", advice: ["Close heavy programs while this finishes."], link: null };
    case "cdn":
      return { title: "Epic's server is slow right now", body: "Glider switches to another server on its own if it stays slow.", advice: [], link: { focus: "cdn", label: "Choose a server" } };
    case "speedLimit": {
      const t = speedText(ctx.speedLimitBytesPerSecond);
      return {
        title: t ? `Held to your ${t} limit` : "Held to your speed limit",
        body: "Downloads stay under the limit you set, so the rest of your network keeps its share.",
        advice: [],
        link: { focus: "speedLimit", label: "Change limit" },
      };
    }
    case "mode":
      return { title: "Background mode is on", body: "Glider downloads gently, with few connections and low priority, so games and apps stay smooth.", advice: [], link: { focus: "downloadMode", label: "Change mode" } };
    case "playing":
      return { title: "Slowed while you play", body: `${status.reason} Full speed returns when the game closes.`, advice: [], link: { focus: "whilePlaying", label: "Change" } };
  }
}

// ------------------------------------------------------------------------------------------------ the card

/** The step's name on the line under the band. Preparing, finalizing, removing and importing say it in the headline instead. */
const STEP_NAME: Partial<Record<JobPhase, string>> = {
  downloading: "Downloading",
  repairing: "Repairing",
  verifying: "Checking files",
  installing: "Installing",
  moving: "Moving",
};

/** The same step while paused: one phrase with "paused" ("Downloading paused", "Install paused"). */
const PAUSED_NAME: Partial<Record<JobPhase, string>> = {
  downloading: "Downloading paused",
  repairing: "Repair paused",
  verifying: "File check paused",
  installing: "Install paused",
  moving: "Move paused",
};

/** The headline when there is no time to give: the step, still going. */
const WORKING: Record<JobPhase, string> = {
  preparing: "Preparing…",
  downloading: "Downloading…",
  repairing: "Repairing…",
  verifying: "Checking files…",
  installing: "Installing…",
  finalizing: "Almost done",
  moving: "Moving…",
  removing: "Removing files…",
  importing: "Linking the install…",
};

export type Lead = { kind: "time"; text: string } | { kind: "state"; text: string };

export interface HoldCopy {
  /** The headline: "Waiting for Fortnite to close". */
  lead: string;
  /** The sentence on the step line; it always says the job continues on its own. */
  line: string;
  /** The Settings row that decides this hold ("While a game is running"); null when nothing the user set causes it. */
  setting: SettingFocus | null;
}

/** What a hold says, composed from its kind and subject so the words stay short and consistent. Never "Queued". */
export function holdCopy(hold: HoldInfo, blockedReason?: string | null): HoldCopy {
  switch (hold.kind) {
    case "game":
      return { lead: hold.subject ? `Waiting for ${hold.subject} to close` : "Waiting for a game to close", line: "Downloads pause while you play and continue on their own", setting: "whilePlaying" };
    case "offline":
      return { lead: "Waiting for an internet connection", line: "Continues on its own when you're back online", setting: null };
    case "egl":
      return { lead: "Waiting for Epic Games Launcher", line: `It's ${/installing/i.test(blockedReason ?? "") ? "installing" : "updating"} this game. Glider continues on its own`, setting: null };
    case "paused":
      return { lead: "Paused", line: "Downloads are paused", setting: null };
    case "other":
      return { lead: (hold.subject ?? "Waiting for another task").replace(/[.\s]+$/, ""), line: "Glider continues on its own", setting: null };
  }
}

export interface CardContext extends SpeedContext {
  /** `QueueState.hold`: why queued work isn't starting. */
  hold: HoldInfo | null;
}

export interface CardCause {
  status: SpeedStatus;
  /** The disclosure's label. */
  label: string;
  explain: Explanation;
}

export interface CardView {
  /** Bytes are moving (running, not held). */
  moving: boolean;
  paused: boolean;
  /** Waiting on something outside: a game, no connection, the Epic launcher. */
  held: HoldCopy | null;
  phase: JobPhase | null;
  /** Whole-job percent, whole number. */
  percent: number;
  /** Show the percent: the job has begun. */
  started: boolean;
  /** The bar has nothing to show yet (starting, preparing). */
  indeterminate: boolean;
  lead: Lead;
  /** The step line's parts, joined with " · ". */
  line: string[];
  /** Found damage ("3 to repair"), set apart in the line. */
  extra: string | null;
  /** The speed the bar's sheen follows. */
  speed: number | null;
  cause: CardCause | null;
  /** The chart in Details: the download while downloading or repairing, the drive's writes while installing. */
  chart: "network" | "disk" | null;
  /** The connection's ceiling, for the chart's dashed line; only while a cause is shown. */
  ceiling: number | null;
  /** The step in words for the bar's name. */
  stepName: string;
  /** Time left in words, for assistive tech; null when there is none. */
  etaWords: string | null;
}

/** What is left at the job's average pace, for a job that is not moving (Core clears the live estimate while it is paused). */
function etaAtPace(p: JobProgress): number | null {
  if (p.etaSeconds != null) return p.etaSeconds;
  const parts: number[] = [];
  const net = p.network;
  const disk = p.disk;
  if ((p.phase === "downloading" || p.phase === "repairing") && net && net.averageBytesPerSecond > 0) parts.push(Math.max(0, net.totalBytes - net.downloadedBytes) / net.averageBytesPerSecond);
  if ((p.phase === "downloading" || p.phase === "repairing" || p.phase === "installing" || p.phase === "moving") && disk && disk.totalBytes > 0 && disk.averageBytesPerSecond > 0) {
    parts.push(Math.max(0, disk.totalBytes - disk.writtenBytes) / disk.averageBytesPerSecond);
  }
  return parts.length ? Math.max(...parts) : null;
}

/**
 * The card's content for one job: the headline (time left, or the state in words), the one line for the current step, the
 * cause of a slow speed, and what Details can show. `ctx.hold` is `QueueState.hold`; a job Core still calls `queued` while a
 * hold stops it (it never started) reads as waiting, never as "Queued".
 */
export function cardOf(job: JobInfo, p: JobProgress | null, ctx: CardContext): CardView {
  const paused = job.status === "paused";
  const heldStatus = job.status === "waiting" || job.status === "queued";
  const hold = !paused && heldStatus ? (ctx.hold ?? (job.status === "waiting" ? { kind: "other" as const, subject: job.blockedReason } : null)) : null;
  const held = hold ? holdCopy(hold, job.blockedReason) : null;
  const moving = job.status === "running";
  const phase = p?.phase ?? job.phase;
  const percent = Math.floor(Math.min(100, Math.max(0, p?.percent ?? 0)));
  const net = p?.network ?? null;
  const disk = p?.disk ?? null;
  const verify = p?.verify ?? null;

  // The current step: its own bytes and its own speed.
  let bytes: string | null = null;
  let speed: number | null = null;
  let extra: string | null = null;
  if (net && (phase === "downloading" || phase === "repairing")) {
    bytes = ofBytes(net.downloadedBytes, net.totalBytes);
    speed = net.bytesPerSecond;
  } else if (verify && phase === "verifying") {
    bytes = ofBytes(verify.bytesChecked, verify.bytesTotal);
    speed = verify.readBytesPerSecond;
    if (verify.badFiles > 0) extra = `${verify.badFiles.toLocaleString()} to repair`;
  } else if (disk && (phase === "installing" || phase === "moving")) {
    bytes = ofBytes(disk.writtenBytes, disk.totalBytes);
    speed = disk.bytesPerSecond;
  }
  let step = (phase && STEP_NAME[phase]) ?? null;
  if (phase === "repairing" && verify && verify.badFiles > 0) step = `Repairing ${verify.badFiles.toLocaleString()} ${verify.badFiles === 1 ? "file" : "files"}`;

  const eta = moving ? (p?.etaSeconds ?? null) : null;

  let lead: Lead;
  let line: string[] = [];
  if (paused) {
    lead = { kind: "state", text: "Paused" };
    const left = p ? etaAtPace(p) : null;
    const pausedStep = phase ? PAUSED_NAME[phase] : undefined;
    // One phrase for the time: "1 min left when you resume" (never "about under a minute left").
    if (pausedStep) line = [pausedStep, ...(bytes ? [bytes] : []), ...(left != null ? [`${formatEta(left)} left when you resume`] : [])];
  } else if (held) {
    lead = { kind: "state", text: held.lead };
    // The bytes so far, then the promise; a lone promise is a sentence.
    line = p && bytes ? [bytes, held.line] : [`${held.line}.`];
  } else if (!p) {
    lead = { kind: "state", text: "Starting…" };
  } else if (phase === "finalizing" || phase === "preparing" || phase === "removing" || phase === "importing" || eta == null) {
    lead = { kind: "state", text: WORKING[phase ?? "preparing"] };
  } else {
    lead = { kind: "time", text: formatEta(eta) };
  }
  if (moving && step) {
    const speedLabel = speedText(speed);
    line = [step, ...(bytes ? [bytes] : []), ...(speedLabel ? [speedLabel] : [])];
  }

  const throughput = net && (phase === "downloading" || phase === "repairing") ? "network" : phase === "installing" || phase === "moving" ? "write" : phase === "verifying" ? "read" : null;
  const status = moving ? speedStatusOf(job, p, throughput, ctx) : null;
  const cause: CardCause | null = status ? { status, label: causeLabel(status, ctx, p), explain: explainCause(status, ctx) } : null;
  const chart = moving && (phase === "downloading" || phase === "repairing") && net ? "network" : moving && phase === "installing" ? "disk" : null;
  const ceiling = chart === "network" && cause && net?.ceilingBytesPerSecond ? net.ceilingBytesPerSecond : null;

  return {
    moving,
    paused,
    held,
    phase: phase ?? null,
    percent,
    started: p !== null && phase !== "preparing",
    indeterminate: !paused && !held && (p === null || phase === "preparing"),
    lead,
    line,
    extra,
    speed: moving ? speed : null,
    cause,
    chart,
    ceiling,
    stepName: held ? held.lead : paused ? "Paused" : (phase && PHASE_STATE[phase]) || "Starting",
    etaWords: eta != null && lead.kind === "time" ? etaWords(eta) : null,
  };
}

/** What kind of job this is, for the Task field in Details. */
export function taskOf(job: JobInfo): string {
  if (job.kind === "install" && job.toVersion) return `Install, version ${prettyVersion(job.toVersion)}`;
  if (job.kind === "update" && job.fromVersion && job.toVersion) return `Update, ${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`;
  if (job.kind === "move" && job.targetPath) return `Move to ${job.targetPath}`;
  if (job.kind === "verify") return "Verify files, repair what's damaged";
  return KIND_NOUN[job.kind];
}

// ------------------------------------------------------------------------------------------------ ready

const DONE_VERB: Partial<Record<JobKind, string>> = { install: "Installed", update: "Updated", repair: "Repaired", verify: "Checked", move: "Moved", modify: "Changed" };

/** "took 4 min" for finished work. */
export const tookText = (job: JobInfo): string | null => {
  if (!job.finishedAt) return null;
  const secs = (Date.parse(job.finishedAt) - Date.parse(job.startedAt ?? job.createdAt)) / 1000;
  return secs > 0 ? formatDuration(secs) : null;
};

export interface ReadyView {
  /** The headline: "Ready to play", "All files are fine", "Moved to E:". */
  lead: string;
  /** "Installed in 28 min." */
  line: string | null;
  /** Play is offered (an install or an update leaves a game to play). */
  play: boolean;
}

/** Jobs whose end the card marks and keeps for a while. Removing and importing have nothing to celebrate or play. */
export const READY_KINDS: readonly JobKind[] = ["install", "update", "repair", "verify", "move", "modify"];

export function readyOf(job: JobInfo): ReadyView {
  const took = tookText(job);
  const verb = DONE_VERB[job.kind];
  const line = verb ? `${verb}${took ? ` in ${took}` : ""}.` : null;
  switch (job.kind) {
    case "verify":
    case "repair": {
      const fixed = job.verify?.filesFixed ?? 0;
      return { lead: fixed > 0 ? `Repaired ${fixed.toLocaleString()} ${fixed === 1 ? "file" : "files"}` : "All files are fine", line, play: false };
    }
    case "move": {
      const drive = driveOf(job.targetPath);
      return { lead: drive ? `Moved to ${drive}` : "Moved", line, play: false };
    }
    case "modify":
      return { lead: "Changes applied", line, play: false };
    default:
      return { lead: "Ready to play", line, play: true };
  }
}
