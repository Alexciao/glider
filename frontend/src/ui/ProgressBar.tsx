import { useState } from "react";
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
  /** Current speed in bytes/s: the faster the transfer, the quicker the sheen sweeps. Omit for a steady pace. */
  speed?: number | null;
  /** Calmer look: desaturated fill, no sheen, no glow. */
  paused?: boolean;
  /** No sheen, whatever the state: nobody is looking (the window is not focused). The fill and glow stay. */
  still?: boolean;
  /** What assistive tech reads as the value ("33 percent, 1 hour 37 minutes left"); omit for the plain percent. */
  valueLabel?: string;
}

/** Sheen sweep time: 3.6 s when crawling down to 1.2 s at several hundred MB/s, in 0.4 s steps so it never twitches. */
export function sheenSeconds(bytesPerSecond: number | null | undefined): number {
  if (bytesPerSecond == null || !Number.isFinite(bytesPerSecond) || bytesPerSecond <= 0) return 2.4;
  const mb = bytesPerSecond / (1024 * 1024);
  const d = 3.6 - 1.15 * Math.log10(1 + mb);
  return Math.round(Math.min(3.6, Math.max(1.2, d)) / 0.4) * 0.4;
}

/**
 * Determinate bars move via translateX on a full-width fill (compositor only). 10 Hz updates are
 * bridged by a short linear transition, so motion reads as continuous rather than stepped.
 */
export function ProgressBar({ value, tone = "accent", size = "md", label, className, speed, paused, still, valueLabel }: ProgressBarProps) {
  const indeterminate = value == null;
  const v = indeterminate ? 0 : Math.min(100, Math.max(0, value));
  const live = !indeterminate && !paused && !still && v > 0 && v < 100;
  // One-shot settle when the bar reaches 100 while mounted (not for bars that already start full).
  const [prev, setPrev] = useState(v);
  const [settle, setSettle] = useState(false);
  if (prev !== v) {
    setPrev(v);
    setSettle(prev < 100 && v >= 100);
  }
  return (
    <Progress.Root
      value={indeterminate ? null : v}
      max={100}
      getValueLabel={valueLabel ? () => valueLabel : undefined}
      aria-label={label}
      data-paused={paused || undefined}
      className={cx("relative w-full overflow-hidden rounded-full bg-surface-3", heights[size], settle && "g-bar-settle", className)}
    >
      <Progress.Indicator
        className={cx(
          // The fill glides linearly between 10 Hz updates; a change of tone (accent ⇄ graphite on pause) eases instead of snapping.
          "relative h-full w-full overflow-hidden rounded-full transition-[transform,opacity,filter,background-color] duration-[120ms,120ms,120ms,200ms] ease-linear",
          fills[tone],
          indeterminate && "g-indeterminate",
          paused && tone !== "neutral" && "opacity-70 saturate-[0.45]",
        )}
        style={indeterminate ? undefined : { transform: `translateX(-${100 - v}%)` }}
      >
        {!indeterminate && !paused && v > 0 && <span aria-hidden className="g-bar-glow" />}
        {live && (
          <span
            aria-hidden
            className="g-bar-sheen"
            style={{ ["--v" as string]: v, ["--g-sheen-s" as string]: `${sheenSeconds(speed)}s` }}
          />
        )}
      </Progress.Indicator>
    </Progress.Root>
  );
}
