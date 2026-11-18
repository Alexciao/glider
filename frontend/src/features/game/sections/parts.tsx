import { Info } from "lucide-react";
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
export function Fact({ label, detail, children, grow, className }: { label: string; detail?: { label: string; content: ReactNode }; children: ReactNode; grow?: boolean; className?: string }) {
  return (
    <div className={cx("min-w-0", grow ? "min-w-[200px] flex-1" : "shrink-0", className)}>
      <dt className="flex items-center gap-1 text-xs text-fg-muted">
        {label}
        {detail && (
          <Tooltip content={detail.content} side="top" align="start">
            <button type="button" aria-label={detail.label} className="grid size-4 shrink-0 cursor-help place-items-center rounded-sm text-fg-muted transition-colors duration-150 hover:text-fg">
              <Info size={12} aria-hidden />
            </button>
          </Tooltip>
        )}
      </dt>
      <dd className="mt-0.5 truncate text-sm font-medium text-fg">{children}</dd>
    </div>
  );
}

/** Label-left, value-right rows for a Fact's detail tooltip. */
export function DetailRows({ rows }: { rows: { label: string; value: string }[] }) {
  return (
    <span className="grid grid-cols-[auto_auto] gap-x-4 gap-y-0.5 tabular-nums">
      {rows.map((r) => (
        <span key={r.label} className="contents">
          <span className="text-fg-muted">{r.label}</span>
          <span className="text-right">{r.value}</span>
        </span>
      ))}
    </span>
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
