import { Check } from "lucide-react";
import { cx } from "./cx";

export interface Step {
  id: string;
  label: string;
}

export interface PhaseStepperProps {
  steps: readonly Step[];
  /** The step in progress; null before the first one starts. A current step missing from `steps` marks nothing as current. */
  current: string | null;
  /** Warning colours for the current step of a paused job. */
  paused?: boolean;
  /** Accessible name of the list. */
  label?: string;
  className?: string;
}

/**
 * A read-only stepper: numbered circles joined by a line, in order. Finished steps are filled and checked, the current one
 * is ringed, the rest are hollow. It is deliberately not a control (no track, no thumb, no hover), so it cannot be mistaken
 * for tabs. Built from the job's planned phases, so a list that shrinks (a clean verify drops "Repairing") just renumbers.
 */
export function PhaseStepper({ steps, current, paused, label = "Steps", className }: PhaseStepperProps) {
  const n = steps.length;
  const at = current ? steps.findIndex((s) => s.id === current) : -1;
  if (n < 2) return null;
  return (
    <ol aria-label={label} className={cx("flex items-center text-xs", className)}>
      {steps.map((s, i) => {
        const done = at >= 0 && i < at;
        const isCurrent = i === at;
        const last = i === n - 1;
        return (
          <li key={s.id} aria-current={isCurrent ? "step" : undefined} className={cx("flex min-w-0 items-center", last ? "shrink-0" : "flex-1")}>
            <span
              aria-hidden
              className={cx(
                "grid size-5 shrink-0 place-items-center rounded-full text-2xs font-semibold tabular-nums transition-colors duration-200",
                done && "bg-accent text-accent-fg",
                isCurrent && (paused ? "border-2 border-warning bg-warning-soft text-warning-text" : "border-2 border-accent bg-accent-soft text-accent-text"),
                !done && !isCurrent && "border border-border-strong text-fg-subtle",
              )}
            >
              {done ? <Check size={12} strokeWidth={3} /> : i + 1}
            </span>
            <span className={cx("ml-2 truncate transition-colors duration-200", isCurrent ? "font-medium text-fg" : done ? "text-fg-muted" : "text-fg-subtle")}>{s.label}</span>
            <span className="sr-only">{done ? ", done" : isCurrent ? ", in progress" : ""}</span>
            {!last && <span aria-hidden className={cx("mx-3 h-px min-w-4 flex-1 transition-colors duration-200", done ? "bg-accent" : "bg-border-strong")} />}
          </li>
        );
      })}
    </ol>
  );
}
