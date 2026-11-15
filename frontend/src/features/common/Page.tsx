import type { ReactNode } from "react";
import { cx } from "../../ui";

/**
 * Scrolling page body, in the shared page frame (`common/frame`): one centred column of at most 1440 px between the 40 px
 * gutters (28 px on narrow windows). Every page's title therefore has the same left edge at any window size, so it doesn't
 * jump sideways as you move through the rail. A page's width only decides where its right side ends, and a narrower page is
 * left-aligned inside the frame, never centred: `full` is the whole frame (Home, the game page's hero), `wide` 960
 * (Downloads, Settings), default 820.
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
      <div className={cx("mx-auto w-full max-w-[1520px] px-10 max-[1100px]:px-7", className ?? "pb-12 pt-8")}>
        {/* `@container`: Home's grids gain a column when the frame is wide enough for it, whatever the window. */}
        <div className={cx("@container", full || game ? "" : wide ? "max-w-[960px]" : "max-w-[820px]")}>{children}</div>
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
