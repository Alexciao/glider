import type { CSSProperties, ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { Ellipsis, Star } from "lucide-react";
import type { AppSnapshot, GameDetail, GameSummary, JobInfo, ShortcutState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { formatBytes, formatEta, formatPercent, formatSpeed } from "../../state/format";
import { useJobProgress } from "../../state/progress";
import { DropdownMenu, DropdownMenuContent, DropdownMenuSkin, DropdownMenuTrigger, Spinner, Tooltip, cx, labelSwap, tween } from "../../ui";
import type { GameAction, StatusModel } from "./actionModel";
import { GameMenuItems, ICONS, cancelRefused, pressPrimary, warmFor } from "./GameActions";
import { HeroProgress } from "./HeroProgress";

/**
 * Dev-only design candidates for the hero's action group, picked with `?actions=a|b|c` (no param: the shipped split button).
 * They share the action model, `pressPrimary` and the menu (`GameMenuItems`); only the controls around them differ. The one
 * the owner keeps replaces `GameActions` + `FavoriteButton`; the rest are deleted with this file.
 *
 *   a · Dock: one glass tray holding primary, favorite and menu; the primary lights up (accent) only when it's THE action.
 *   b · Pill + orbs: a soft pearl pill, then round glass buttons (favorite, more) of one material.
 *   c · Command: a wide, tall primary that carries its own cost (size, percent) and becomes the progress bar during a job.
 */
export type ActionsCandidate = "a" | "b" | "c";

const fromUrl = (): ActionsCandidate | null => {
  if (typeof location === "undefined") return null;
  const v = new URLSearchParams(location.search).get("actions");
  return v === "a" || v === "b" || v === "c" ? v : null;
};
/** Read once: a candidate is a page-load choice, like `?theme=`. */
export const ACTIONS_CANDIDATE: ActionsCandidate | null = fromUrl();

/**
 * Candidate c moves the job's cost into the button ("Install · 7.2 GB"), so the status line doesn't say it again.
 */
export function candidateStatus(candidate: ActionsCandidate | null, action: GameAction): StatusModel {
  if (candidate !== "c") return action.status;
  if (action.kind === "install") return { ...action.status, quiet: true };
  if (action.kind === "update") return { ...action.status, sub: undefined };
  return action.status;
}

export interface CandidateProps {
  candidate: ActionsCandidate;
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  moment: boolean;
  /** The job has a bar to show (running or paused, not queued/waiting). */
  showBar: boolean;
  onShortcuts?: (state: ShortcutState) => void;
}

export function CandidateActions(props: CandidateProps) {
  if (props.candidate === "a") return <DockActions {...props} />;
  if (props.candidate === "b") return <PillActions {...props} />;
  return <CommandActions {...props} />;
}

/* ------------------------------------------------------------------ shared parts */

/** Icon (or spinner) and label; crossfades only when the face's identity changes, like the split button. */
function Face({ action, className }: { action: GameAction; className?: string }) {
  return (
    <AnimatePresence mode="popLayout" initial={false}>
      <m.span
        key={action.faceKey}
        initial={labelSwap.initial}
        animate={labelSwap.animate}
        exit={labelSwap.exit}
        transition={tween.base}
        className={cx("relative flex items-center gap-2.5 whitespace-nowrap", className)}
      >
        {action.busy ? <Spinner size={16} /> : ICONS[action.kind]}
        {action.label}
      </m.span>
    </AnimatePresence>
  );
}

/** The one-off "ready" flourish: a sheen clipped to the face and a ring that grows out of it (outside the clip). */
function Moment({ on }: { on: boolean }) {
  if (!on) return null;
  return (
    <>
      <span aria-hidden className="ga-sheen" />
      <span aria-hidden className="g-ready-ring" />
    </>
  );
}

function primaryHandlers(action: GameAction, game: GameSummary) {
  const warm = warmFor(action, game.appName);
  return {
    type: "button" as const,
    "data-kind": action.kind,
    "data-tone": action.tone,
    disabled: action.disabled || cancelRefused(action),
    "aria-busy": action.busy || undefined,
    onClick: action.busy ? undefined : () => pressPrimary(action, game.appName),
    onPointerEnter: warm,
    onFocus: warm,
  };
}

function FavoriteToggle({ game, className, size = 18 }: { game: GameSummary; className: string; size?: number }) {
  const on = game.favorite;
  return (
    <Tooltip content={on ? "Remove from favorites" : "Add to favorites"}>
      <button type="button" aria-pressed={on} aria-label="Favorite" data-on={on || undefined} onClick={() => void actions.setFavorite(game.appName, !on)} className={className}>
        <Star size={size} className={cx("ga-star", on && "fill-current")} />
      </button>
    </Tooltip>
  );
}

function MoreMenu({
  className,
  skin,
  align,
  alignOffset,
  children,
  size = 18,
}: {
  className: string;
  skin: string;
  align: "start" | "end";
  alignOffset?: number;
  children: ReactNode;
  size?: number;
}) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button type="button" aria-label="More actions" className={className}>
          <Ellipsis size={size} />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuSkin value={skin}>
        <DropdownMenuContent align={align} alignOffset={alignOffset} sideOffset={10} className="w-60">
          {children}
        </DropdownMenuContent>
      </DropdownMenuSkin>
    </DropdownMenu>
  );
}

const menuFor = ({ snap, game, detail, action, onShortcuts }: CandidateProps) => (
  <GameMenuItems snap={snap} game={game} detail={detail} action={action} onShortcuts={onShortcuts} />
);

function SideProgress({ job }: { job: JobInfo }) {
  return (
    <div className="ml-4 min-w-0 max-w-[440px] flex-1">
      <HeroProgress job={job} />
    </div>
  );
}

/* ------------------------------------------------------------------ a · Dock */

/**
 * One object, one material: a glass tray (the same blur and hairline as the hero's other glass) with three controls inset
 * 4 px. The primary is lit with the accent only when it's what the page is for (Play, Install, Update, Resume, Sign in);
 * work in progress (Pause, Close game, Cancel) stays glass, so a lit button always means "go".
 */
function DockActions(props: CandidateProps) {
  const { game, action, moment, showBar } = props;
  return (
    <>
      <div role="group" aria-label={`${game.title} actions`} className="ga-dock" data-lit={action.tone === "primary" || undefined}>
        <m.button layout="position" {...primaryHandlers(action, game)} className={cx("ga-dock-primary", (action.busy || action.disabled) && "ga-inert")}>
          <Face action={action} />
          <Moment on={moment} />
        </m.button>
        <m.span layout="position" aria-hidden className="ga-dock-sep" />
        <m.span layout="position" className="flex">
          <FavoriteToggle game={game} className="ga-dock-icon" />
          <MoreMenu className="ga-dock-icon" skin="ga-menu ga-menu-glass" align="end" alignOffset={-4}>
            {menuFor(props)}
          </MoreMenu>
        </m.span>
      </div>
      {showBar && action.job && <SideProgress job={action.job} />}
    </>
  );
}

/* ------------------------------------------------------------------ b · Pill + orbs */

/**
 * Separate objects with clear jobs: the primary is a fully round pill (a soft pearl with a lit top edge when it's THE
 * action, glass otherwise), and everything else is a 48 px glass orb. The chevron is gone; "more" is a peer of favorite.
 */
function PillActions(props: CandidateProps) {
  const { game, action, moment, showBar } = props;
  return (
    <>
      <div role="group" aria-label={`${game.title} actions`} className="flex items-center gap-2.5">
        <m.button layout="position" {...primaryHandlers(action, game)} className={cx("gb-pill", (action.busy || action.disabled) && "ga-inert")}>
          <Face action={action} />
          <Moment on={moment} />
        </m.button>
        <m.span layout="position" className="flex gap-2.5">
          <FavoriteToggle game={game} className="gb-orb" />
          <MoreMenu className="gb-orb" skin="ga-menu gb-menu" align="start">
            {menuFor(props)}
          </MoreMenu>
        </m.span>
      </div>
      {showBar && action.job && <SideProgress job={action.job} />}
    </>
  );
}

/* ------------------------------------------------------------------ c · Command */

/** The button's own cost, said once: download size before a job, live percent during one. */
function staticMeta(action: GameAction, game: GameSummary): string | null {
  if ((action.kind === "install" || action.kind === "update") && !action.busy && game.downloadSizeBytes) return formatBytes(game.downloadSizeBytes);
  return null;
}

/**
 * One big, wide command (56 px) in glide blue, carrying its cost on the right ("Update · 421 MB"). While a job runs the
 * button *is* the progress bar: it fills as the job goes, says the percent, and the speed and time left sit beside it.
 * "More" is a matching round button; favorite is a bare star that only shows a fill when hovered or set.
 */
function CommandActions(props: CandidateProps) {
  const { game, action, moment, showBar } = props;
  const job = showBar ? action.job : undefined;
  const meta = staticMeta(action, game);
  return (
    <>
      <div role="group" aria-label={`${game.title} actions`} className="flex items-center gap-3">
        <m.button
          layout="position"
          {...primaryHandlers(action, game)}
          data-progress={job ? "" : undefined}
          data-meta={meta || job ? "" : undefined}
          className={cx("gc-cmd", (action.busy || action.disabled) && "ga-inert")}
        >
          {job && <CommandFill job={job} />}
          <Face action={action} className="z-[1]" />
          {job ? <CommandPercent job={job} /> : meta && <span className="gc-meta">{meta}</span>}
          <Moment on={moment} />
        </m.button>
        <m.span layout="position" className="flex items-center gap-1">
          <MoreMenu className="gc-orb" skin="ga-menu gc-menu" align="start" size={20}>
            {menuFor(props)}
          </MoreMenu>
          <FavoriteToggle game={game} className="gc-star" size={20} />
        </m.span>
      </div>
      {job && <CommandCaption job={job} />}
    </>
  );
}

/** These three re-render at the progress rate (~10 Hz); the button and its menu don't. */
function CommandFill({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const v = Math.max(0, Math.min(100, p?.percent ?? 0)) / 100;
  return <span aria-hidden className="gc-fill" data-paused={job.status === "paused" || undefined} style={{ "--p": v } as CSSProperties} />;
}

function CommandPercent({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  return <span className="gc-meta tabular-nums">{p ? formatPercent(p.percent) : "…"}</span>;
}

function CommandCaption({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const net = p?.network ?? null;
  const verify = p?.verify ?? null;
  const paused = job.status === "paused";
  let amount: string | null = null;
  if (net) amount = `${formatBytes(net.downloadedBytes)} of ${formatBytes(net.totalBytes)}`;
  else if (verify && p?.phase === "verifying") amount = `${verify.filesChecked.toLocaleString()} of ${verify.filesTotal.toLocaleString()} files`;
  else if (p?.filesTotal) amount = `${(p.filesDone ?? 0).toLocaleString()} of ${p.filesTotal.toLocaleString()} files`;
  const speed = net ? net.bytesPerSecond : verify && p?.phase === "verifying" ? verify.readBytesPerSecond : null;
  // Paused needs no rate line: the status line above already says so.
  const rate = paused ? null : p ? `${speed != null && speed > 0 ? `${formatSpeed(speed)} · ` : ""}${formatEta(p.etaSeconds)} left` : "Starting…";
  return (
    <div
      role="progressbar"
      aria-label={job.title}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={p ? Math.round(p.percent) : undefined}
      className="ml-3 flex min-w-0 flex-col text-xs leading-5 tabular-nums"
    >
      <span className="truncate text-fg">{amount ?? " "}</span>
      {rate && <span className="truncate text-fg-muted">{rate}</span>}
    </div>
  );
}
