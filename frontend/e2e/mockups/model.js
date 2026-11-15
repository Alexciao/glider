// What the mockup cards say, as data (scratch; the spec in docs/design-download-card.md is the source of truth).
// Two tiers: the whole job (time left, percent, bar) never resets across steps; the current step (name, its bytes, its speed)
// is one line under the bar. Figures marked NEW need Core data that does not exist yet; here they are derived from what the
// mock sends so the mockups show realistic values.
import { driveOf, formatEta, formatSpeed, prettyVersion } from "../../src/state/format";
import { KIND_NOUN, MODE_LABEL, explainSpeed, speedStatusOf } from "../../src/state/transfer";

const UNITS = ["B", "KB", "MB", "GB", "TB"];
const scale = (n) => {
  let u = 0;
  let v = Math.max(0, n);
  while (v >= 1024 && u < UNITS.length - 1) {
    v /= 1024;
    u++;
  }
  return { v, u };
};
const num = (v) => (v >= 100 ? Math.round(v).toString() : v.toFixed(1));

/** "15.8 of 37.6 GB", "659 MB of 1.5 GB": the unit once when both sides share it. */
export function ofBytes(done, total) {
  const a = scale(done);
  const b = scale(total);
  if (a.u === b.u) return `${num(a.v)} of ${num(b.v)} ${UNITS[b.u]}`;
  return `${num(a.v)} ${UNITS[a.u]} of ${num(b.v)} ${UNITS[b.u]}`;
}

/** "6.8 MB/s" with two significant figures under 10, whole numbers from 100 (calm: changes less often). */
export function speedText(bps) {
  if (!(bps > 0)) return null;
  const { v, u } = scale(bps);
  return `${v >= 100 ? Math.round(v) : v.toFixed(1)} ${UNITS[u]}/s`;
}

const STEP = {
  preparing: "Preparing",
  downloading: "Downloading",
  repairing: "Repairing",
  verifying: "Checking files",
  installing: "Installing",
  finalizing: "Finishing up",
  moving: "Moving",
  removing: "Removing files",
  importing: "Linking the install",
};

/** The quiet line under the title: what kind of job, and the version it brings. */
export function metaOf(job) {
  if (job.kind === "update" && job.fromVersion && job.toVersion) return `Update · ${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`;
  if (job.kind === "install" && job.toVersion) return `Install · ${prettyVersion(job.toVersion)}`;
  if (job.kind === "move" && job.targetPath) return `Move to ${job.targetPath}`;
  if (job.kind === "verify") return "Verify files";
  return KIND_NOUN[job.kind];
}

/** Short cause labels: name the cause, never a verdict ("Slow download" told nobody what to do). */
function causeLabel(status, ctx, p) {
  switch (status.cause) {
    case "disk": {
      const d = driveOf(ctx.path);
      return d ? `Limited by drive ${d}` : "Limited by your drive";
    }
    case "cpu":
      return "Limited by the processor";
    case "cdn":
      return "Server is slow";
    case "speedLimit":
      return ctx.speedLimitBytesPerSecond ? `Your ${speedText(ctx.speedLimitBytesPerSecond)} limit` : "Your speed limit";
    case "mode":
      return `${MODE_LABEL[p?.mode ?? ctx.effectiveMode]} mode`;
    case "playing":
      return "Slowed while you play";
    default:
      return status.label;
  }
}

/**
 * Why a job is not moving although nobody paused it. NEW (Core, in progress per docs/review-v0.2-ui-final.md): a hold with a
 * kind and a subject, from which the card composes its copy; today Core only has `blockedReason` (free text) for EGL and nothing
 * for a game or a lost connection. The mockups take it from `?hold=playing|offline`.
 */
export function holdOf(job, ctx) {
  if (ctx.hold === "playing") return { kind: "gameRunning", subject: ctx.holdSubject ?? "Fortnite" };
  if (ctx.hold === "offline") return { kind: "offline", subject: null };
  if (job.status === "waiting") return { kind: "otherLauncher", subject: job.blockedReason ?? null };
  return null;
}

const HOLD_COPY = {
  gameRunning: (h) => ({ lead: `Waiting for ${h.subject} to close`, line: "Downloads pause while you play and continue on their own.", setting: "whilePlaying" }),
  offline: () => ({ lead: "Waiting for an internet connection", line: "Continues on its own when you're back online.", setting: null }),
  otherLauncher: () => ({ lead: "Waiting for Epic Games Launcher", line: "It's updating this game. Glider continues on its own.", setting: null }),
};

export function viewOf(job, p, ctx) {
  const hold = holdOf(job, ctx);
  const running = job.status === "running" && !hold;
  const paused = job.status === "paused";
  const waiting = !!hold;
  const phase = p?.phase ?? job.phase;
  const percent = Math.floor(Math.min(100, Math.max(0, p?.percent ?? 0)));
  const net = p?.network ?? null;
  const disk = p?.disk ?? null;
  const verify = p?.verify ?? null;

  // Whole-job time left. NEW (Core): EtaSeconds covers the rest of the job (download, then the write tail at the drive's
  // measured pace), and keeps a value while installing. Paused: what is left at the job's average pace.
  let eta = running ? (p?.etaSeconds ?? null) : null;
  if (running && eta == null && phase === "installing" && disk && disk.bytesPerSecond > 0) eta = (disk.totalBytes - disk.writtenBytes) / disk.bytesPerSecond;
  let etaAtPace = null;
  if ((paused || hold) && net && net.averageBytesPerSecond > 0) etaAtPace = (net.totalBytes - net.downloadedBytes) / net.averageBytesPerSecond;

  // The current step: its name, its own bytes, its own speed.
  let bytes = null;
  let speed = null;
  let extra = null;
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
  let step = STEP[phase] ?? "Starting";
  if (phase === "repairing" && verify?.badFiles) step = `Repairing ${verify.badFiles.toLocaleString()} ${verify.badFiles === 1 ? "file" : "files"}`;

  const throughput = net && (phase === "downloading" || phase === "repairing") ? "network" : phase === "installing" || phase === "moving" ? "write" : phase === "verifying" ? "read" : null;
  const status = running ? speedStatusOf(job, p, throughput, ctx) : null;
  const cause = status ? { ...status, short: causeLabel(status, ctx, p), why: explainSpeed(status, p, ctx) } : null;

  // NEW (Core, cheap): the connection's ceiling = max(this job's peak, the CDN probe), as LimiterDecision already computes.
  const ceiling = net ? Math.max(net.peakBytesPerSecond, net.bytesPerSecond) : null;

  let lead;
  const held = hold ? HOLD_COPY[hold.kind](hold) : null;
  if (paused) lead = { kind: "state", text: "Paused" };
  else if (held) lead = { kind: "state", text: held.lead };
  else if (!p) lead = { kind: "state", text: "Starting…" };
  else if (phase === "finalizing") lead = { kind: "state", text: "Almost done" };
  else if (phase === "preparing" || eta == null) lead = { kind: "state", text: `${STEP[phase] ?? "Working"}…` };
  else lead = { kind: "time", text: formatEta(eta) };

  return {
    running,
    paused,
    waiting,
    phase,
    percent,
    started: p !== null && phase !== "preparing",
    lead,
    etaAtPace: etaAtPace != null ? formatEta(etaAtPace).replace(/^Under/, "under") : null,
    held,
    step: phase === "preparing" || phase === "finalizing" ? null : step,
    stepName: step,
    bytes,
    speed: running ? speed : null,
    speedLabel: running ? speedText(speed) : null,
    extra,
    cause,
    ceiling,
    net,
    disk,
    meta: metaOf(job),
    blocked: held?.line ?? null,
  };
}

export { formatSpeed };
