import type { ReactNode } from "react";
import { cx } from "../../ui";

/** A titled block on Home: a heading and its content. Nothing else: no counts, no links (the rail already goes everywhere). */
export function HomeSection({ title, action, children, className }: { title: string; action?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section aria-label={title} className={cx("mb-10", className)}>
      <div className="mb-3 flex h-7 items-center justify-between gap-4">
        <h2 className="text-md font-semibold tracking-tight text-fg">{title}</h2>
        {action}
      </div>
      {children}
    </section>
  );
}
