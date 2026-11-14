import type { ReactNode } from "react";
import { cx } from "./cx";

export interface ProgressRingProps {
  /** 0–100. `null` renders an indeterminate arc. */
  value: number | null;
  label: string;
  size?: number;
  stroke?: number;
  /** Calmer: dimmed fill, no motion. */
  paused?: boolean;
  /** For use over art: a light track and a pale-blue fill that stay legible on any cover. */
  onArt?: boolean;
  className?: string;
  /** Centered content (the percentage). */
  children?: ReactNode;
}

/**
 * A circular progress indicator. Determinate rings move the stroke offset with a short linear transition so 10 Hz
 * updates read as continuous; indeterminate rings spin a quarter arc.
 */
export function ProgressRing({ value, label, size = 44, stroke = 3, paused, onArt, className, children }: ProgressRingProps) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const indeterminate = value === null;
  const v = indeterminate ? 0 : Math.min(100, Math.max(0, value));
  return (
    <div
      role="progressbar"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={indeterminate ? undefined : Math.floor(v)}
      data-paused={paused || undefined}
      className={cx("relative grid shrink-0 place-items-center", className)}
      style={{ width: size, height: size }}
    >
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} fill="none" aria-hidden className="absolute inset-0 -rotate-90">
        <circle cx={size / 2} cy={size / 2} r={r} stroke={onArt ? "oklch(1 0 0 / 0.28)" : "var(--g-surface-3)"} strokeWidth={stroke} />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={r}
          stroke={onArt ? "oklch(0.86 0.09 252)" : "var(--g-accent)"}
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={indeterminate ? `${c * 0.25} ${c}` : c}
          strokeDashoffset={indeterminate ? 0 : c * (1 - v / 100)}
          opacity={paused ? 0.55 : 1}
          className={cx(indeterminate && !paused && "g-spinner origin-center", !indeterminate && "transition-[stroke-dashoffset] duration-[120ms] ease-linear")}
        />
      </svg>
      {children && <span className="relative">{children}</span>}
    </div>
  );
}
