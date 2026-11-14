import type { AppSnapshot, EglInstall, GameSummary, JobInfo, JobKind, JobPhase } from "../ipc/contract.gen";
import type { BadgeTone } from "../ui";
import { formatBytes, prettyVersion } from "./format";
import { jobForGame } from "./store";

export const JOB_VERB: Record<JobKind, string> = {
  install: "Installing",
  update: "Updating",
  repair: "Repairing",
  verify: "Verifying",
  move: "Moving",
  uninstall: "Uninstalling",
  import: "Importing",
  modify: "Changing",
  overlay: "Installing overlay",
};

/** Imperative, for things that haven't finished ("Update Fortnite"). */
export const JOB_ACTION: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verify",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
  modify: "Change components of",
  overlay: "Install",
};

/** Past tense, for finished history entries ("Updated Fortnite"). */
export const JOB_PAST: Record<JobKind, string> = {
  install: "Installed",
  update: "Updated",
  repair: "Repaired",
  verify: "Verified",
  move: "Moved",
  uninstall: "Uninstalled",
  import: "Imported",
  modify: "Changed components of",
  overlay: "Installed",
};

export const JOB_NOUN: Record<JobKind, string> = {
  install: "Install",
  update: "Update",
  repair: "Repair",
  verify: "Verification",
  move: "Move",
  uninstall: "Uninstall",
  import: "Import",
  modify: "Change",
  overlay: "Overlay install",
};

/** The verb for what a job is doing right now: a verify that moved on to repairing says so. */
export function jobVerb(kind: JobKind, phase?: JobPhase | null): string {
  if ((kind === "verify" || kind === "repair") && phase === "repairing") return "Repairing";
  if (kind === "repair" && phase === "verifying") return "Verifying";
  return JOB_VERB[kind];
}

export const PHASE_LABEL: Record<JobPhase, string> = {
  preparing: "Preparing",
  verifying: "Verifying files",
  repairing: "Repairing files",
  downloading: "Downloading",
  installing: "Installing",
  finalizing: "Finalizing",
  moving: "Moving files",
  removing: "Removing files",
  importing: "Importing",
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
  | "closing"
  | "unavailable";

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

/** Anything that blocks a new write operation on this game. */
export function isBusy(s: AppSnapshot, appName: string): boolean {
  return jobForGame(s.queue.jobs, appName) !== undefined || s.sessions.some((x) => x.appName === appName);
}

/** Slow-changing progress facts only (whole percent, phase), so the hero re-renders ~1×/s, not at 10 Hz. */
export interface HeroContext {
  phase?: JobPhase;
  percent?: number;
}

export function deriveHero(s: AppSnapshot, game: GameSummary, ctx: HeroContext = {}): HeroModel {
  const { auth } = s;
  const job = jobForGame(s.queue.jobs, game.appName);
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
      // Same face as "launching": a 300 ms "Preparing…" in between would just flicker.
      return quiet("launching", "Launching…", { tone: "accent", text: `Starting ${game.title}`, spinner: true });
    case "syncingSaves":
      return quiet("launching", "Launching…", { tone: "accent", text: "Syncing cloud saves", sub: game.title, spinner: true });
    case "installingPrerequisites":
      return quiet("preparing", "Setting up…", { tone: "accent", text: "Setting up the game", sub: "Windows may ask for permission", spinner: true });
    case "launching":
      return quiet("launching", "Launching…", { tone: "accent", text: `Starting ${game.title}`, spinner: true });
    case "running":
      return quiet("playing", "Playing", { tone: "success", text: `${game.title} is running` });
    case "closing":
      return quiet("closing", "Closing…", { tone: "neutral", text: `${game.title} is closing`, spinner: true });
    case "idle":
      break;
  }

  // 2. A job is in flight (or parked).
  if (job) {
    const percent = ctx.percent ?? job.progress?.percent ?? 0;
    const phase = ctx.phase ?? job.progress?.phase ?? job.phase ?? undefined;
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
          status: { tone: "warning", text: `${JOB_NOUN[job.kind]} paused` },
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

  // 3. Entitlement-only content has nothing to do.
  if (game.installStatus === "notInstallable" && game.thirdParty === "none") {
    return quiet("unavailable", "Included", { tone: "neutral", text: "Included with your purchase", sub: game.kind === "dlc" ? "Nothing to install" : "Managed outside Epic" });
  }

  // 4. Account gates everything that talks to Epic.
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

  const offline = s.connectivity === "offline";

  // 5. Install state.
  switch (game.installStatus) {
    case "installed": {
      if (offline && !game.canRunOffline) {
        return quiet("unavailable", "Play", { tone: "warning", text: "Needs an internet connection", sub: `${game.title} can't run offline` });
      }
      if (game.updateAvailable && !offline) {
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
        status: offline
          ? { tone: "warning", text: "Offline", sub: "Playing without Epic's services" }
          : { tone: "success", text: "Ready to play", sub: game.installedVersion ? `Version ${prettyVersion(game.installedVersion)}` : undefined },
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
    case "notInstallable":
      // Third-party titles: Play hands over to their own launcher.
      return {
        kind: "play",
        label: "Play",
        emphasis: "primary",
        disabled: false,
        target: "launch",
        status: { tone: "neutral", text: `Starts in ${game.thirdPartyName ?? "its launcher"}` },
      };
    default:
      if (offline) return quiet("unavailable", "Install", { tone: "warning", text: "You're offline", sub: "Connect to install" });
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

// ----- EGL installs -----

/** An EGL install that Glider can take over without downloading anything. */
export const isImportable = (i: EglInstall): boolean => i.status === "importable";

export const importableInstalls = (installs: readonly EglInstall[]): EglInstall[] => installs.filter(isImportable);

/** Badge for an install row. The backend's `reason` explains anything that is not importable. */
export function eglInstallStatus(i: EglInstall): { tone: BadgeTone; text: string } {
  switch (i.status) {
    case "importable":
      return i.drift === "eglNewer" ? { tone: "warning", text: "Older version" } : { tone: "success", text: "Ready to import" };
    case "imported":
      return { tone: "neutral", text: "Already in Glider" };
    case "stale":
      return { tone: "danger", text: "Folder missing" };
    case "busy":
      return { tone: "warning", text: "Epic is busy with it" };
    case "notOwned":
      return { tone: "neutral", text: "Not in your library" };
  }
}
