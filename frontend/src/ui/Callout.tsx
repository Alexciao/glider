import type { ReactNode } from "react";
import { CircleAlert, CircleCheck, Info, TriangleAlert, WifiOff } from "lucide-react";
import { cx } from "./cx";

export type CalloutTone = "info" | "success" | "warning" | "danger" | "offline";

const tones: Record<CalloutTone, { box: string; icon: string; Icon: typeof Info }> = {
  info: { box: "border-border bg-surface-1", icon: "text-accent-text", Icon: Info },
  success: { box: "border-success/25 bg-success-soft", icon: "text-success-text", Icon: CircleCheck },
  warning: { box: "border-warning/25 bg-warning-soft", icon: "text-warning-text", Icon: TriangleAlert },
  danger: { box: "border-danger/25 bg-danger-soft", icon: "text-danger-text", Icon: CircleAlert },
  offline: { box: "border-border-strong bg-surface-2", icon: "text-fg-muted", Icon: WifiOff },
};

export interface CalloutProps {
  tone?: CalloutTone;
  title: ReactNode;
  children?: ReactNode;
  /** Right-aligned actions. */
  action?: ReactNode;
  className?: string;
  /** Announce changes to screen readers (default for warning/danger/offline). */
  live?: boolean;
}

/** A calm in-flow message: icon, one bold line, an optional sentence, an optional action. Same radius, padding and type as NoticeCard and ErrorCallout. For errors with a fix use ErrorCallout. */
export function Callout({ tone = "info", title, children, action, className, live }: CalloutProps) {
  const t = tones[tone];
  const announce = live ?? (tone === "warning" || tone === "danger" || tone === "offline");
  return (
    <div role={announce ? "status" : undefined} className={cx("flex items-start gap-3 rounded-lg border p-3.5", t.box, className)}>
      <t.Icon aria-hidden size={16} className={cx("mt-0.5 shrink-0", t.icon)} />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-fg">{title}</p>
        {children && <div className="mt-0.5 text-xs leading-[1.5] text-fg-muted">{children}</div>}
      </div>
      {action && <div className="flex shrink-0 items-center gap-1.5">{action}</div>}
    </div>
  );
}
