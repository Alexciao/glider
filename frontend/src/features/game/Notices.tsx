import type { ReactNode } from "react";
import { CircleAlert, ExternalLink, Info, TriangleAlert, WifiOff, X } from "lucide-react";
import type { GameDetail, GameSummary, JobInfo, RunningGame } from "../../ipc/contract.gen";
import { actions, pendingKey } from "../../state/actions";
import { useLaunchNote, useLaunchNotes } from "../../state/launch";
import { historyJobs, useSnap } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Button, IconButton, cx } from "../../ui";
import { ErrorView } from "../common/ErrorView";

type Tone = "info" | "warning" | "danger";

const toneBox: Record<Tone, string> = {
  info: "border-border-strong bg-surface-1",
  warning: "border-warning/25 bg-warning-soft",
  danger: "border-danger/25 bg-danger-soft",
};
const toneIcon: Record<Tone, string> = { info: "text-accent-text", warning: "text-warning-text", danger: "text-danger-text" };

/** A compact, calm card for something worth knowing about this game right now: what it is, then the one thing to do. */
export function NoticeCard({
  tone = "info",
  icon,
  title,
  children,
  actions: act,
  onDismiss,
  dismissLabel = "Dismiss",
  className,
}: {
  tone?: Tone;
  icon?: ReactNode;
  title: string;
  children?: ReactNode;
  actions?: ReactNode;
  onDismiss?: () => void;
  dismissLabel?: string;
  className?: string;
}) {
  const Icon = icon ?? (tone === "info" ? <Info size={16} /> : tone === "warning" ? <TriangleAlert size={16} /> : <CircleAlert size={16} />);
  return (
    <div role={tone === "danger" ? "alert" : "status"} className={cx("flex gap-3 rounded-lg border p-3.5", toneBox[tone], className)}>
      <span className={cx("mt-0.5 shrink-0", toneIcon[tone])} aria-hidden>
        {Icon}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-fg">{title}</p>
        {children && <div className="mt-0.5 text-xs leading-[1.5] text-fg-muted">{children}</div>}
        {act && <div className="mt-3 flex flex-wrap items-center gap-2">{act}</div>}
      </div>
      {onDismiss && (
        <IconButton label={dismissLabel} size="sm" tooltip={false} onClick={onDismiss} className="-mr-1 -mt-1 shrink-0">
          <X size={14} />
        </IconButton>
      )}
    </div>
  );
}

/**
 * Only what needs the user's attention about this game right now, in priority order, directly under the hero where
 * theme-aware colours keep it legible: a failure, a note about how the last Play went, one-time setup. Nothing here
 * when all is well. (Save conflicts live in the Cloud saves row, the only surface for them.)
 */
export function GameNotices({ game, detail, failedJob, session }: { game: GameSummary; detail: GameDetail | null; failedJob?: JobInfo; session?: RunningGame }) {
  const { appName } = game;
  const heroError = useUi((s) => s.errors.hero);
  const verifying = usePending(pendingKey("verify", appName));
  const setting = usePending(pendingKey("prereq", appName));
  const hasJob = game.activeJobId !== null;
  const idle = game.runState === "idle" && !hasJob;
  // Errors belong to the game they are about (or to nobody in particular).
  const shownError = heroError && (!heroError.appName || heroError.appName === appName) ? heroError : undefined;
  const cards: ReactNode[] = [];

  if (shownError) {
    cards.push(<ErrorView key="err" error={shownError.error} retry={shownError.retry} path={shownError.path} onDismiss={() => useUi.getState().setError("hero", undefined)} />);
  } else if (failedJob?.error) {
    cards.push(<ErrorView key="failed" error={failedJob.error} retry={() => void actions.retryJob(failedJob)} path={failedJob.targetPath} onDismiss={() => void actions.dismissJob(failedJob.id)} />);
  }

  if (session?.offline && game.runState === "running") {
    cards.push(
      <NoticeCard key="offline" icon={<WifiOff size={16} />} title="Started offline">
        Cloud saves are paused until you start the game online.
      </NoticeCard>,
    );
  }

  if (game.installStatus === "installed" && game.needsVerification && idle) {
    cards.push(
      <NoticeCard
        key="verify"
        title="Not verified yet"
        actions={
          <Button size="sm" variant="secondary" loading={verifying} onClick={() => void actions.verify(appName)}>
            Verify
          </Button>
        }
      >
        This install came from another launcher.
      </NoticeCard>,
    );
  }

  const ac = detail && detail.antiCheat !== "none";
  const missing = (detail?.prerequisites ?? []).filter((p) => !p.installed && !(ac && /anti-?cheat|battleye/i.test(p.name)));
  if (game.installStatus === "installed" && missing.length > 0 && idle) {
    cards.push(
      <NoticeCard
        key="prereq"
        title="One-time setup needed"
        actions={
          <Button size="sm" variant="secondary" loading={setting} onClick={() => void actions.installPrerequisites(appName)}>
            Set up now
          </Button>
        }
      >
        {missing.map((p) => p.name).join(", ")}. Windows may ask for permission. Play does this too.
      </NoticeCard>,
    );
  }

  if (cards.length === 0) return null;
  return <div className="flex flex-col gap-3">{cards}</div>;
}

/**
 * Shown only when the last Play said something about a third-party launcher: activation is needed, or a launcher
 * opened. (Core reports activation only after a Play; see the contract note in the report.)
 */
export function ThirdPartyNotice({ game }: { game: GameSummary }) {
  const note = useLaunchNote(game.appName);
  const activating = usePending(pendingKey("activate", game.appName));
  const signedIn = useSnap((s) => s.auth.status === "signedIn");
  if (!note || game.thirdParty === "none") return null;
  const name = game.thirdPartyName ?? "another launcher";
  const clear = () => useLaunchNotes.getState().clear(game.appName);
  if (note.kind === "needsActivation") {
    return (
      <NoticeCard
        tone="warning"
        icon={<ExternalLink size={16} />}
        title={`Activate with ${name}`}
        onDismiss={clear}
        actions={
          <Button size="sm" variant="secondary" loading={activating} disabled={!signedIn} onClick={() => void actions.activate(game.appName)}>
            Activate
          </Button>
        }
      >
        {note.message ?? "Do this once, then Play starts the game."}
      </NoticeCard>
    );
  }
  return (
    <NoticeCard icon={<ExternalLink size={16} />} title={`Opened ${name}`} onDismiss={clear}>
      {note.message ?? "The game starts there."} Glider can't track play time for it.
    </NoticeCard>
  );
}

/** The first failed job of this game, if any (its error is what the notices stack shows). */
export function useFailedJob(appName: string): JobInfo | undefined {
  return useSnap((s) => historyJobs(s.queue.jobs).find((j) => j.appName === appName && j.status === "failed" && j.error));
}
