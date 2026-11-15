import type { ReactNode } from "react";
import { cx } from "../../ui";

/**
 * Scrolling page body. Every page starts at the same left edge (the 40 px gutter, 28 px on narrow windows): a page's
 * width only decides where its right side ends, and a narrower page is left-aligned, never centred, so the title
 * doesn't jump sideways as you move through the rail. Widths are the content width inside the gutters:
 * `full` 1160 (Home), `game` 1040 (the game hero), `wide` 960 (Downloads, Settings), default 820.
 */
export function PageScroll({
  children,
  className,
  wide,
  full,
  game,
}: {
  children: ReactNode;
  className?: string;
  wide?: boolean;
  full?: boolean;
  game?: boolean;
}) {
  return (
    <div className="h-full overflow-y-auto [scrollbar-gutter:stable]">
      <div className={cx("w-full px-10 max-[1100px]:px-7", className ?? "pb-12 pt-8")}>
        <div className={full ? "max-w-[1160px]" : game ? "max-w-[1040px]" : wide ? "max-w-[960px]" : "max-w-[820px]"}>{children}</div>
      </div>
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
