import type { ReactNode } from "react";
import { cx } from "../../ui";

/** Scrolling page body: one centred column, generous gutters that tighten on narrow windows. `full` is the Library/Home width (a 1240 px column, in line with the library grid's edges). */
export function PageScroll({ children, className, wide, full }: { children: ReactNode; className?: string; wide?: boolean; full?: boolean }) {
  return (
    <div className="h-full overflow-y-auto [scrollbar-gutter:stable]">
      <div className={cx("mx-auto w-full px-10 pb-12 pt-8 max-[1100px]:px-7", full ? "max-w-[1240px]" : wide ? "max-w-[1040px]" : "max-w-[900px]", className)}>{children}</div>
    </div>
  );
}

export function PageHeader({ title, description, actions }: { title: string; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-7 flex items-end justify-between gap-6">
      <div className="min-w-0">
        <h1 className="text-xl font-semibold tracking-tight text-fg">{title}</h1>
        {description && <p className="mt-1 text-sm text-fg-muted">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  );
}
