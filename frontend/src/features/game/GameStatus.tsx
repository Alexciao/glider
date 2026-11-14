import { AnimatePresence, m } from "motion/react";
import { useNow } from "../../state/hooks";
import { Spinner, StatusDot, cx, labelSwap, tween } from "../../ui";
import type { StatusModel } from "./actionModel";
import { formatPlayingFor } from "./format";

const text: Record<StatusModel["tone"], string> = {
  neutral: "text-fg-muted",
  accent: "text-accent-text",
  success: "text-success-text",
  warning: "text-warning-text",
  danger: "text-danger-text",
};

/** The success mark, drawn once with a stroke animation when a download finishes. */
function ReadyCheck() {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden className="shrink-0 text-success">
      <circle cx="9" cy="9" r="8" fill="currentColor" fillOpacity="0.16" />
      <path d="M5.4 9.3l2.4 2.4 4.8-5.1" pathLength={1} stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" className="g-draw" />
    </svg>
  );
}

/** "Playing for 1 h 12 min": ticks every 30 s, which is all the precision a duration this long needs. */
function PlayingFor({ since }: { since: string | null }) {
  const now = useNow(30_000);
  return <>Playing for {formatPlayingFor(since, now)}</>;
}

/**
 * The one status line of the hero: a dot or spinner, what is happening, one quiet detail. A line that changes
 * crossfades with a hair of blur so "Preparing → Syncing saves → Launching" reads as one object updating.
 */
export function GameStatus({ status, moment, className }: { status: StatusModel; moment: boolean; className?: string }) {
  const key = `${status.since !== undefined ? "playing" : status.text}|${moment}`;
  return (
    <div className={cx("flex min-h-6 items-center gap-2.5 text-base", className)} aria-live="polite">
      <AnimatePresence mode="popLayout" initial={false}>
        <m.div
          key={key}
          initial={labelSwap.initial}
          animate={labelSwap.animate}
          exit={labelSwap.exit}
          transition={tween.base}
          className="flex min-w-0 items-center gap-2.5"
        >
          {status.spinner ? <Spinner size={14} className="shrink-0 text-fg-muted" /> : moment ? <ReadyCheck /> : <StatusDot tone={status.tone} />}
          <span className={cx("font-medium", text[status.tone])}>{status.since !== undefined ? <PlayingFor since={status.since} /> : status.text}</span>
          {status.sub && (
            <>
              <span aria-hidden className="h-3 w-px shrink-0 bg-border-strong" />
              <span className="min-w-0 truncate text-fg-muted tabular-nums">{status.sub}</span>
            </>
          )}
        </m.div>
      </AnimatePresence>
    </div>
  );
}
