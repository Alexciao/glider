import type { ReactNode } from "react";
import { cx } from "./cx";

/** An empty screen is an invitation: what this is, and the one thing to do next. */
export function EmptyState({
  icon,
  title,
  description,
  action,
  className,
}: {
  icon: ReactNode;
  title: string;
  description?: ReactNode;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cx("flex flex-col items-center px-6 py-12 text-center", className)}>
      <div className="mb-4 grid size-11 place-items-center rounded-xl border border-border-strong bg-surface-2 text-fg-muted [box-shadow:inset_0_1px_0_var(--g-highlight)]">
        {icon}
      </div>
      <h2 className="text-base font-semibold text-fg">{title}</h2>
      {description && <p className="mt-1 max-w-[34ch] text-sm leading-[1.45] text-fg-muted">{description}</p>}
      {action && <div className="mt-5">{action}</div>}
    </div>
  );
}
