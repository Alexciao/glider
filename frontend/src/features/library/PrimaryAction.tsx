import type { ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { ArrowDownToLine, Clock, Download, LogIn, Play, Wrench } from "lucide-react";
import type { HeroKind, HeroModel } from "../../state/derive";
import { formatClock, formatPercent } from "../../state/format";
import { useNow } from "../../state/hooks";
import { useProgress } from "../../state/progress";
import { actions } from "../../state/actions";
import { Spinner, cx, labelSwap, spring } from "../../ui";

const icons: Partial<Record<HeroKind, ReactNode>> = {
  signIn: <LogIn size={17} />,
  install: <Download size={17} />,
  update: <ArrowDownToLine size={17} />,
  play: <Play size={15} fill="currentColor" strokeWidth={0} />,
  resume: <Play size={15} fill="currentColor" strokeWidth={0} />,
  repair: <Wrench size={16} />,
  waiting: <Clock size={16} />,
  queued: <Clock size={16} />,
  working: <Spinner size={16} />,
  loading: <Spinner size={16} />,
  signingIn: <Spinner size={16} />,
  preparing: <Spinner size={16} />,
  launching: <Spinner size={16} />,
  closing: <Spinner size={16} />,
  playing: <span aria-hidden className="size-2 rounded-full bg-success shadow-[0_0_0_3px_var(--g-success-soft)]" />,
};

/** Whole-percent text that re-renders only when the integer changes. */
function JobPercent({ jobId, fallback }: { jobId: string; fallback: number }) {
  const p = useProgress((s) => Math.floor(s.latest[jobId]?.percent ?? fallback));
  return <span className="tabular-nums">{formatPercent(p)}</span>;
}

function Elapsed({ since }: { since: string | null }) {
  const now = useNow(1000);
  if (!since) return null;
  return <span className="tabular-nums">{formatClock((now - Date.parse(since)) / 1000)}</span>;
}

/** Progress shown as a fill behind the label while an install/update runs. */
function JobFill({ jobId, fallback }: { jobId: string; fallback: number }) {
  const p = useProgress((s) => s.latest[jobId]?.percent ?? fallback);
  return (
    <span aria-hidden className="absolute inset-0 overflow-hidden rounded-[inherit]">
      <span
        className="absolute inset-0 origin-left bg-accent/25 transition-transform duration-[120ms] ease-linear"
        style={{ transform: `scaleX(${Math.min(100, Math.max(0, p)) / 100})` }}
      />
    </span>
  );
}

/** What the button says in the 50–200 ms between a click and the backend's first state change. */
const PENDING_LABEL: Partial<Record<HeroKind, string>> = {
  play: "Launching…",
  signIn: "Signing in…",
  install: "Starting…",
  update: "Updating…",
  resume: "Resuming…",
  repair: "Repairing…",
};

/** Shared by Play and the More button so the pair reads as one split button: same fill, border, motion. */
export const heroButtonBase =
  "relative flex h-12 items-center justify-center overflow-hidden text-md font-semibold " +
  "transition-[background-color,color,box-shadow,border-color,scale] duration-200 ease-out " +
  "enabled:active:scale-[0.97] disabled:cursor-default";
export const heroButtonTone = (primary: boolean) =>
  primary
    ? "bg-primary text-primary-fg shadow-md enabled:hover:bg-primary-hover data-[open]:bg-primary-hover"
    : "border border-border-strong bg-surface-3 text-fg backdrop-blur-md enabled:hover:bg-surface-active data-[open]:bg-surface-active";
/** Outer corners 16px (radius-xl); the inner edge of a joined pair is tight (6px). */
export const heroRadius = { outer: 16, inner: 6 } as const;

export interface PrimaryActionProps {
  appName: string;
  hero: HeroModel;
  runningSince: string | null;
  pending: boolean;
  /** Play the one-off "ready to play" flourish. */
  moment: boolean;
  /** A More button follows: square off the trailing corners so the two read as one group. */
  joined?: boolean;
  onPress: () => void;
}

/**
 * The one big button. It morphs between Sign in / Install / Update / Resume / Play / Launching /
 * Playing / Waiting: width reflows with a critically damped spring (layout projection, transform-only)
 * while the label crossfades with a hair of blur so the two states read as one object changing.
 */
/** Warms the launch command on hover/focus so Play starts the game almost instantly. Safe to repeat. */
export function PrimaryAction({ appName, hero, runningSince, pending, moment, joined, onPress }: PrimaryActionProps) {
  const prefetchLaunch = () => void actions.prepareLaunch(appName);
  const primary = hero.emphasis === "primary";
  const busy = pending && primary;
  // Pending reuses the next state's face, so click → pending → running reads as one continuous morph.
  const kind: HeroKind = busy ? (hero.kind === "play" ? "launching" : "loading") : hero.kind;
  const label = busy ? (PENDING_LABEL[hero.kind] ?? "Starting…") : hero.label;
  const interactive = !hero.disabled && !busy;

  return (
    <m.button
      layout
      type="button"
      transition={spring.snappy}
      style={{
        borderTopLeftRadius: heroRadius.outer,
        borderBottomLeftRadius: heroRadius.outer,
        borderTopRightRadius: joined ? heroRadius.inner : heroRadius.outer,
        borderBottomRightRadius: joined ? heroRadius.inner : heroRadius.outer,
      }}
      disabled={!interactive}
      aria-busy={busy || undefined}
      onClick={onPress}
      onPointerEnter={hero.kind === "play" ? prefetchLaunch : undefined}
      onFocus={hero.kind === "play" ? prefetchLaunch : undefined}
      data-kind={hero.kind}
      className={cx(heroButtonBase, "min-w-[132px] px-6", heroButtonTone(primary), moment && "g-sheen")}
    >
      {hero.kind === "working" && hero.job && <JobFill jobId={hero.job.id} fallback={hero.percent ?? 0} />}
      {moment && <span className="g-ready-ring" aria-hidden />}
      <AnimatePresence mode="popLayout" initial={false}>
        <m.span
          key={kind}
          layout="position"
          initial={labelSwap.initial}
          animate={labelSwap.animate}
          exit={labelSwap.exit}
          transition={{ duration: 0.18, ease: [0.23, 1, 0.32, 1] }}
          className="relative flex items-center gap-2.5 whitespace-nowrap"
        >
          {icons[kind]}
          <span>{label}</span>
          {hero.kind === "working" && hero.job && !busy && <JobPercent jobId={hero.job.id} fallback={hero.percent ?? 0} />}
          {hero.kind === "playing" && <Elapsed since={runningSince} />}
        </m.span>
      </AnimatePresence>
    </m.button>
  );
}
