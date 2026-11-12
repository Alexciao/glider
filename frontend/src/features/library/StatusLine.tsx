import { AnimatePresence, m } from "motion/react";
import type { HeroModel } from "../../state/derive";
import { Spinner, StatusDot, cx, labelSwap } from "../../ui";

const text: Record<HeroModel["status"]["tone"], string> = {
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

export function StatusLine({ status, moment, className }: { status: HeroModel["status"]; moment: boolean; className?: string }) {
  return (
    <div className={cx("flex min-h-6 items-center gap-2.5 text-base", className)} aria-live="polite">
      <AnimatePresence mode="popLayout" initial={false}>
        <m.div
          key={`${status.text}|${moment}`}
          initial={labelSwap.initial}
          animate={labelSwap.animate}
          exit={labelSwap.exit}
          transition={{ duration: 0.2, ease: [0.23, 1, 0.32, 1] }}
          className="flex items-center gap-2.5"
        >
          {status.spinner ? <Spinner size={14} className="text-fg-muted" /> : moment ? <ReadyCheck /> : <StatusDot tone={status.tone} />}
          <span className={cx("font-medium", text[status.tone])}>{status.text}</span>
          {status.sub && (
            <>
              <span aria-hidden className="h-3 w-px bg-border-strong" />
              <span className="text-fg-muted tabular-nums">{status.sub}</span>
            </>
          )}
        </m.div>
      </AnimatePresence>
    </div>
  );
}
