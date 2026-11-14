import type { ReactNode } from "react";
import { Tooltip, cx } from "../../../ui";

/** A titled block of the game page. One quiet heading; the content brings its own structure. */
export function Block({ id, title, meta, action, children, className }: { id: string; title: string; meta?: ReactNode; action?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section aria-labelledby={id} className={className}>
      <div className="mb-2 flex min-h-8 items-center justify-between gap-3">
        <h2 id={id} className="flex items-baseline gap-2 text-sm font-semibold text-fg">
          {title}
          {meta && <span className="text-xs font-normal text-fg-muted tabular-nums">{meta}</span>}
        </h2>
        {action}
      </div>
      {children}
    </section>
  );
}

/** One fact of a facts row: a small label over its value. No boxes; the row's spacing does the grouping. */
export function Fact({ label, hint, children, grow, className }: { label: string; hint?: string; children: ReactNode; grow?: boolean; className?: string }) {
  return (
    <div className={cx("min-w-0", grow ? "min-w-[200px] flex-1" : "shrink-0", className)}>
      <dt className="text-xs text-fg-muted">
        {hint ? (
          <Tooltip content={hint} side="top" align="start">
            <span className="cursor-help">{label}</span>
          </Tooltip>
        ) : (
          label
        )}
      </dt>
      <dd className="mt-0.5 truncate text-sm font-medium text-fg">{children}</dd>
    </div>
  );
}

/** A quiet placeholder while a section's data is on its way. */
export function RowsSkeleton({ rows = 2 }: { rows?: number }) {
  return (
    <div className="flex flex-col gap-3" aria-hidden>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="g-skeleton h-4 rounded-md" style={{ width: `${88 - i * 18}%` }} />
      ))}
    </div>
  );
}
