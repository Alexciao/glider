import { Progress } from "radix-ui";
import { cx } from "./cx";

type Tone = "accent" | "success" | "warning" | "danger" | "neutral";

const fills: Record<Tone, string> = {
  accent: "bg-accent",
  success: "bg-success",
  warning: "bg-warning",
  danger: "bg-danger",
  neutral: "bg-fg-muted",
};
const heights = { xs: "h-0.5", sm: "h-1", md: "h-1.5", lg: "h-2" } as const;

export interface ProgressBarProps {
  /** 0–100. `null`/undefined renders the indeterminate bar. */
  value?: number | null;
  tone?: Tone;
  size?: keyof typeof heights;
  label: string;
  className?: string;
}

/**
 * Determinate bars move via translateX on a full-width fill (compositor only). 10 Hz updates are
 * bridged by a short linear transition, so motion reads as continuous rather than stepped.
 */
export function ProgressBar({ value, tone = "accent", size = "md", label, className }: ProgressBarProps) {
  const indeterminate = value == null;
  const v = indeterminate ? 0 : Math.min(100, Math.max(0, value));
  return (
    <Progress.Root
      value={indeterminate ? null : v}
      max={100}
      aria-label={label}
      className={cx("relative w-full overflow-hidden rounded-full bg-surface-3", heights[size], className)}
    >
      <Progress.Indicator
        className={cx(
          "h-full w-full rounded-full transition-transform duration-[120ms] ease-linear",
          fills[tone],
          indeterminate && "g-indeterminate",
        )}
        style={indeterminate ? undefined : { transform: `translateX(-${100 - v}%)` }}
      />
    </Progress.Root>
  );
}
