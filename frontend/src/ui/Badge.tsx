import type { ReactNode } from "react";
import { cx } from "./cx";

export type BadgeTone = "neutral" | "accent" | "success" | "warning" | "danger";

const tones: Record<BadgeTone, string> = {
  neutral: "bg-surface-3 text-fg-muted",
  accent: "bg-accent-soft text-accent-text",
  success: "bg-success-soft text-success-text",
  warning: "bg-warning-soft text-warning-text",
  danger: "bg-danger-soft text-danger-text",
};
const dots: Record<BadgeTone, string> = {
  neutral: "bg-fg-subtle",
  accent: "bg-accent",
  success: "bg-success",
  warning: "bg-warning",
  danger: "bg-danger",
};

export function Badge({
  tone = "neutral",
  dot,
  icon,
  children,
  className,
}: {
  tone?: BadgeTone;
  dot?: boolean;
  icon?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <span className={cx("inline-flex h-5 items-center gap-1.5 whitespace-nowrap rounded-full px-2 text-2xs font-medium", tones[tone], className)}>
      {dot && <span aria-hidden className={cx("size-1.5 rounded-full", dots[tone])} />}
      {icon}
      {children}
    </span>
  );
}

/** A status dot used inline in text lines. */
export function StatusDot({ tone = "neutral", pulse }: { tone?: BadgeTone; pulse?: boolean }) {
  return (
    <span aria-hidden className={cx("inline-block size-2 shrink-0 rounded-full", dots[tone], pulse && "animate-[g-breathe_1.8s_ease-in-out_infinite]")} />
  );
}
