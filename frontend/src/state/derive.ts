import type { AppSnapshot, EglState, InstallCandidate, JobInfo, JobKind, JobPhase } from "../ipc/contract.gen";
import type { BadgeTone } from "../ui";
import { currentJob } from "./store";
import { formatBytes, formatPercent, prettyVersion } from "./format";

export const JOB_VERB: Record<JobKind, string> = {
  install: "Installing",
  update: "Updating",
  repair: "Repairing",
  verify: "Verifying",
  move: "Moving",
  uninstall: "Removing",
  import: "Importing",
};

export const JOB_NOUN: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verification",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
};

export const PHASE_LABEL: Record<JobPhase, string> = {
  preparing: "Preparing",
  downloading: "Downloading",
  verifying: "Verifying files",
  finalizing: "Finalizing",
  moving: "Moving files",
  removing: "Removing files",
};

export type HeroKind =
  | "loading"
  | "signIn"
  | "signingIn"
  | "install"
  | "update"
  | "play"
  | "repair"
  | "resume"
  | "working"
  | "waiting"
  | "queued"
  | "preparing"
  | "launching"
  | "playing"
  | "closing";

export type HeroTarget = "signIn" | "install" | "update" | "launch" | "repair" | "resume" | "downloads" | "none";

export interface HeroModel {
  kind: HeroKind;
  /** Button label (Playing's elapsed time is appended by the component, it ticks). */
  label: string;
  emphasis: "primary" | "quiet";
  disabled: boolean;
  target: HeroTarget;
  job?: JobInfo;
  /** 0–100 when the label carries progress. */
  percent?: number;
  status: { tone: BadgeTone; text: string; sub?: string; spinner?: boolean };
}

/** Anything that blocks a new write operation. */
export function isBusy(s: AppSnapshot): boolean {
  return currentJob(s.jobs) !== undefined || s.game.runState !== "idle";
}

/** Slow-changing progress facts only (whole percent, phase), so the hero re-renders ~1×/s, not at 10 Hz. */
export interface HeroContext {
  phase?: JobPhase;
  percent?: number;
}

export function deriveHero(s: AppSnapshot, ctx: HeroContext = {}): HeroModel {
  const { game, auth } = s;
  const job = currentJob(s.jobs);
  const quiet = (kind: HeroKind, label: string, status: HeroModel["status"], extra: Partial<HeroModel> = {}): HeroModel => ({
    kind,
    label,
    emphasis: "quiet",
    disabled: true,
    target: "none",
    status,
    ...extra,
  });

  // 1. The game itself is starting, running or closing: that outranks everything.
  switch (game.runState) {
    case "preparing":
      return quiet("preparing", "Preparing…", { tone: "accent", text: "Getting ready to launch", spinner: true });
    case "installingPrerequisites":
      return quiet("preparing", "Setting up…", { tone: "accent", text: "Setting up anti-cheat", sub: "Windows may ask for permission", spinner: true });
    case "launching":
      return quiet("launching", "Launching…", { tone: "accent", text: "Starting Fortnite", spinner: true });
    case "running":
      return quiet("playing", "Playing", { tone: "success", text: "Fortnite is running" });
    case "closing":
      return quiet("closing", "Closing…", { tone: "neutral", text: "Fortnite is closing", spinner: true });
    case "idle":
      break;
  }

  // 2. A job is in flight (or parked).
  if (job) {
    const percent = ctx.percent ?? job.progress?.percent ?? 0;
    const phase = ctx.phase ?? job.progress?.phase;
    switch (job.status) {
      case "waiting": {
        const reason = (job.blockedReason ?? "Another task is using the game files").replace(/[.\s]+$/, "");
        return {
          kind: "waiting",
          label: "Waiting",
          emphasis: "quiet",
          disabled: false,
          target: "downloads",
          job,
          status: { tone: "warning", text: `${reason}, so Glider will wait` },
        };
      }
      case "paused":
        return {
          kind: "resume",
          label: "Resume",
          emphasis: "primary",
          disabled: false,
          target: "resume",
          job,
          percent,
          status: { tone: "warning", text: `${JOB_NOUN[job.kind]} paused`, sub: formatPercent(percent) },
        };
      case "queued":
        return {
          kind: "queued",
          label: "Queued",
          emphasis: "quiet",
          disabled: false,
          target: "downloads",
          job,
          status: { tone: "neutral", text: `${JOB_NOUN[job.kind]} queued`, sub: "Starts when the current task finishes" },
        };
      default:
        return {
          kind: "working",
          label: JOB_VERB[job.kind],
          emphasis: "quiet",
          disabled: false,
          target: "downloads",
          job,
          percent,
          status: { tone: "accent", text: JOB_VERB[job.kind], sub: phase ? PHASE_LABEL[phase] : undefined, spinner: true },
        };
    }
  }

  // 3. Account gates everything that talks to Epic.
  if (auth.status === "unknown" || auth.status === "signingIn") {
    return quiet(
      auth.status === "signingIn" ? "signingIn" : "loading",
      auth.status === "signingIn" ? "Signing in…" : "Loading…",
      { tone: "neutral", text: auth.status === "signingIn" ? "Finish signing in in the Epic window" : "Checking your account", spinner: true },
    );
  }
  if (auth.status === "signedOut") {
    return {
      kind: "signIn",
      label: "Sign in",
      emphasis: "primary",
      disabled: false,
      target: "signIn",
      status: { tone: "neutral", text: "Sign in with Epic to install and play" },
    };
  }
  if (auth.status === "expired") {
    return {
      kind: "signIn",
      label: "Sign in again",
      emphasis: "primary",
      disabled: false,
      target: "signIn",
      status: { tone: "warning", text: "Your session expired", sub: "Sign in to keep playing" },
    };
  }

  // 4. Install state.
  switch (game.installStatus) {
    case "installed": {
      if (game.updateAvailable) {
        return {
          kind: "update",
          label: "Update",
          emphasis: "primary",
          disabled: false,
          target: "update",
          status: { tone: "accent", text: "Update available", sub: game.downloadSizeBytes ? formatBytes(game.downloadSizeBytes) : undefined },
        };
      }
      return {
        kind: "play",
        label: "Play",
        emphasis: "primary",
        disabled: false,
        target: "launch",
        status: game.checkingForUpdates
          ? { tone: "neutral", text: "Checking for updates", spinner: true }
          : { tone: "success", text: "Ready to play", sub: s.game.installed ? `Version ${prettyVersion(s.game.installed.version)}` : undefined },
      };
    }
    case "broken":
      return {
        kind: "repair",
        label: "Repair",
        emphasis: "primary",
        disabled: false,
        target: "repair",
        status: { tone: "danger", text: "Game files need repair", sub: "Only damaged files are downloaded again" },
      };
    case "installing":
      return quiet("working", "Installing…", { tone: "accent", text: "Installing", spinner: true });
    default:
      return {
        kind: "install",
        label: "Install",
        emphasis: "primary",
        disabled: false,
        target: "install",
        status: { tone: "neutral", text: "Not installed", sub: game.downloadSizeBytes ? `${formatBytes(game.downloadSizeBytes)} download` : undefined },
      };
  }
}

// ----- EGL candidates -----

/** An EGL candidate we can adopt as-is. */
export const isUsableCandidate = (c: InstallCandidate): boolean => !c.isStale && c.folderExists && c.executableFound;

export function candidateIssue(c: InstallCandidate): string | null {
  if (isUsableCandidate(c)) return null;
  if (!c.folderExists) return "This folder no longer exists";
  if (!c.executableFound) return "Game files are missing from this folder";
  return "Epic Games Launcher points to a folder that no longer holds the game";
}

export const usableCandidates = (egl: EglState): InstallCandidate[] => egl.candidates.filter(isUsableCandidate);

export function candidateStatus(c: InstallCandidate, latest: string | null): { tone: BadgeTone; text: string } {
  if (!isUsableCandidate(c)) return { tone: "danger", text: "Unavailable" };
  if (c.version && latest && c.version !== latest) return { tone: "warning", text: "Update needed" };
  return { tone: "success", text: "Up to date" };
}
