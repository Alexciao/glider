import type { AppSnapshot, GameSummary, JobInfo, JobKind, JobPhase, RunningGame } from "../../ipc/contract.gen";
import type { BadgeTone } from "../../ui";
import { JOB_NOUN, PHASE_LABEL, jobVerb } from "../../state/derive";
import { formatBytes } from "../../state/format";
import { jobForGame } from "../../state/store";
import { statusTone } from "./statusLevels";

/**
 * What the game page's primary button does, and the one status line that goes with it. Pure: everything the page
 * shows about "what can I do with this game right now" comes from here, so it is testable without rendering.
 *
 * While a job runs, the primary segment is the relevant control (Pause / Resume, or Cancel for jobs that cannot
 * pause), never a disabled "Verifying 0%" label: progress belongs to the bar and the status line.
 */
export type ActionIntent = "signIn" | "install" | "update" | "launch" | "verify" | "pause" | "resume" | "cancel" | "close" | "none";

export type ActionKind =
  | "loading"
  | "signIn"
  | "install"
  | "update"
  | "play"
  | "repair"
  | "pause"
  | "resume"
  | "cancel"
  | "launching"
  | "closing"
  | "closeGame"
  | "unavailable";

export interface StatusModel {
  tone: BadgeTone;
  text: string;
  sub?: string;
  /** The button already says it ("Play" under "Ready to play"): the line is shown only during the one-off ready moment. */
  quiet?: boolean;
  /** Set while the game runs: the component renders "Playing for …" and keeps it ticking. */
  since?: string | null;
}

export interface GameAction {
  kind: ActionKind;
  label: string;
  tone: "primary" | "quiet";
  /** Heard, not done yet: spinner, no further presses. */
  busy: boolean;
  disabled: boolean;
  /** Identity of the face: label and icon crossfade only when this changes. */
  faceKey: string;
  intent: ActionIntent;
  status: StatusModel;
  job?: JobInfo;
}

/** Jobs Core can pause (it kills the process and resumes from disk). Everything else can only be cancelled. */
const PAUSABLE: readonly JobKind[] = ["install", "update", "repair", "verify"];
export const canPause = (kind: JobKind): boolean => PAUSABLE.includes(kind);

export interface ActionContext {
  session?: RunningGame;
  /** A click on one of these is in flight (between the press and the backend's first state change). */
  pending: { launch?: boolean; update?: boolean; verify?: boolean; signIn?: boolean; close?: boolean; plan?: boolean };
  /** Slow-changing job facts (whole percent, phase) so the model changes about once a second, not at 10 Hz. */
  phase?: JobPhase;
}

const inert = (kind: ActionKind, label: string, status: StatusModel, extra: Partial<GameAction> = {}): GameAction => ({
  kind,
  label,
  tone: "quiet",
  busy: false,
  disabled: true,
  faceKey: kind,
  intent: "none",
  status,
  ...extra,
});

/**
 * Starting up: the button holds one face (spinner + "Launching…") while the status line names the stage before it. The line
 * never spins too, and the last stage ("Launching") is the button's own word, so the line steps aside for it.
 */
const starting = (status: StatusModel): GameAction => inert("launching", "Launching…", status, { tone: "primary", busy: true, disabled: false });

/** The sub-line of a running job: only the phase, and only when it says something the verb does not. */
function phaseSub(kind: JobKind, phase: JobPhase | null | undefined): string | undefined {
  if (!phase) return undefined;
  if (phase === "verifying" || phase === "repairing" || phase === "moving" || phase === "removing" || phase === "importing") return undefined;
  const label = PHASE_LABEL[phase];
  return label === jobVerb(kind, phase) ? undefined : label;
}

export function deriveGameAction(snap: AppSnapshot, game: GameSummary, ctx: ActionContext): GameAction {
  const { auth } = snap;
  const job = jobForGame(snap.queue.jobs, game.appName);
  const offline = snap.connectivity === "offline";

  // 1. The game itself starting, running or closing outranks everything.
  switch (game.runState) {
    case "preparing":
      return starting({ tone: statusTone("working"), text: "Preparing" });
    case "syncingSaves":
      return starting({ tone: statusTone("working"), text: "Syncing saves" });
    case "installingPrerequisites":
      return starting({ tone: statusTone("working"), text: "Installing prerequisites", sub: "Windows may ask for permission" });
    case "launching":
      return starting({ tone: statusTone("working"), text: "Launching", quiet: true });
    case "running":
      return {
        kind: "closeGame",
        label: "Close game",
        tone: "quiet",
        busy: ctx.pending.close ?? false,
        disabled: false,
        faceKey: "closeGame",
        intent: "close",
        status: ctx.session?.untracked
          ? { tone: statusTone("running"), text: `Started in ${game.thirdPartyName ?? "its launcher"}`, sub: "Glider can't track play time here" }
            : { tone: statusTone("running"), text: "Playing", since: ctx.session?.since ?? null },
      };
    case "closing":
      return inert("closing", "Closing…", { tone: statusTone("working"), text: "Closing", quiet: true }, { busy: true, disabled: false });
    case "idle":
      break;
  }

  // 2. A job is in flight (or parked): the primary segment is its control.
  if (job) {
    const phase = ctx.phase ?? job.progress?.phase ?? job.phase ?? undefined;
    const base = { busy: false, disabled: false, job } as const;
    switch (job.status) {
      case "paused":
        return { ...base, kind: "resume", label: "Resume", tone: "primary", faceKey: "resume", intent: "resume", status: { tone: statusTone("paused"), text: `${JOB_NOUN[job.kind]} paused` } };
      case "waiting": {
        const reason = (job.blockedReason ?? "Another task is using the game files").replace(/[.\s]+$/, "");
        return { ...base, kind: "cancel", label: "Cancel", tone: "quiet", faceKey: "cancel", intent: "cancel", status: { tone: statusTone("waiting"), text: `${reason}, so Glider will wait` } };
      }
      case "queued":
        return {
          ...base,
          kind: "cancel",
          label: "Cancel",
          tone: "quiet",
          faceKey: "cancel",
          intent: "cancel",
          status: { tone: statusTone("queued"), text: `${JOB_NOUN[job.kind]} queued`, sub: "Starts when the current task finishes" },
        };
      default: {
        const pausable = canPause(job.kind);
        return {
          ...base,
          kind: pausable ? "pause" : "cancel",
          label: pausable ? "Pause" : "Cancel",
          tone: "quiet",
          faceKey: pausable ? "pause" : "cancel",
          intent: pausable ? "pause" : "cancel",
          // The bar beside the button already moves: the line doesn't spin as well.
          status: { tone: statusTone("working"), text: jobVerb(job.kind, phase), sub: phaseSub(job.kind, phase) },
        };
      }
    }
  }

  // 3. Entitlement-only content has nothing to do.
  if (game.installStatus === "notInstallable" && game.thirdParty === "none") {
    return inert("unavailable", "Included", {
      tone: "neutral",
      text: game.kind === "dlc" ? "Included with the base game" : "Included with your purchase",
      sub: "Nothing to install",
    });
  }

  // 4. The account gates everything that talks to Epic.
  if (auth.status === "unknown" || auth.status === "signingIn") {
    const signing = auth.status === "signingIn";
    return inert(
      "loading",
      signing ? "Signing in…" : "Loading…",
      { tone: statusTone("working"), text: signing ? "Finish signing in in the Epic window" : "Checking your account" },
      { busy: true, disabled: false },
    );
  }
  if (auth.status === "signedOut" || auth.status === "expired") {
    const expired = auth.status === "expired";
    return {
      kind: "signIn",
      label: expired ? "Sign in again" : "Sign in",
      tone: "primary",
      busy: ctx.pending.signIn ?? false,
      disabled: false,
      faceKey: "signIn",
      intent: "signIn",
      // One treatment for both: signed out is not a state to be mildly informed about, it is the next thing to do. The button
      // says what to do (Sign in), so the line only says why.
      status: expired
        ? { tone: statusTone("needsSignIn"), text: "Your session expired" }
        : { tone: statusTone("needsSignIn"), text: "You're signed out" },
    };
  }

  // 5. Install state.
  switch (game.installStatus) {
    case "installed": {
      if (offline && !game.canRunOffline) {
        return inert("unavailable", "Play", { tone: statusTone("needsInternet"), text: "Needs an internet connection" });
      }
      if (game.updateAvailable && !offline) {
        const pending = ctx.pending.update ?? false;
        return {
          kind: "update",
          label: pending ? "Updating…" : "Update",
          tone: "primary",
          busy: pending,
          disabled: false,
          faceKey: "update",
          intent: "update",
          status: { tone: statusTone("updateAvailable"), text: "Update available", sub: game.downloadSizeBytes ? `${formatBytes(game.downloadSizeBytes)} download` : undefined },
        };
      }
      const pending = ctx.pending.launch ?? false;
      return {
        kind: "play",
        label: pending ? "Launching…" : "Play",
        tone: "primary",
        busy: pending,
        disabled: false,
        faceKey: pending ? "launching" : "play",
        intent: "launch",
        status: offline
          ? { tone: statusTone("offline"), text: "You're offline", sub: "Playing without Epic's services" }
          : { tone: statusTone("ready"), text: "Ready to play", quiet: true },
      };
    }
    case "broken": {
      const pending = ctx.pending.verify ?? false;
      return {
        kind: "repair",
        label: pending ? "Repairing…" : "Repair",
        tone: "primary",
        busy: pending,
        disabled: false,
        faceKey: "repair",
        intent: "verify",
        status: { tone: statusTone("needsRepair"), text: "Game files need repair", sub: "Only damaged files are downloaded again" },
      };
    }
    case "notInstallable": {
      const pending = ctx.pending.launch ?? false;
      return {
        kind: "play",
        label: pending ? "Opening…" : "Play",
        tone: "primary",
        busy: pending,
        disabled: false,
        faceKey: pending ? "launching" : "play",
        intent: "launch",
        status: { tone: statusTone("ready"), text: `Plays through ${game.thirdPartyName ?? "its launcher"}` },
      };
    }
    default:
      if (offline) return inert("unavailable", "Install", { tone: statusTone("offline"), text: "You're offline", sub: "Connect to install" });
      return {
        kind: "install",
        label: "Install",
        tone: "primary",
        busy: ctx.pending.plan ?? false,
        disabled: false,
        faceKey: "install",
        intent: "install",
        // The Install button says what to do; the line only adds what it can't (how much it downloads).
        status: game.downloadSizeBytes ? { tone: statusTone("notInstalled"), text: `${formatBytes(game.downloadSizeBytes)} download` } : { tone: statusTone("notInstalled"), text: "Not installed", quiet: true },
      };
  }
}
