import { useState } from "react";
import type { GameDetail, GameSummary } from "../../../ipc/contract.gen";
import { cx } from "../../../ui";

/** Long descriptions fold to three lines with a quiet "Show more"; short ones never show the control. */
const FOLD_AT = 220;

/** The description and, under it, who made it and when, in one muted line. Nothing here if there is nothing to say. */
export function AboutSection({ game, detail }: { game: GameSummary; detail: GameDetail | null }) {
  const [open, setOpen] = useState(false);
  const description = detail?.description?.trim() || null;
  const long = (description?.length ?? 0) > FOLD_AT;
  const year = detail?.releaseDate ? new Date(detail.releaseDate).getFullYear() : null;
  const publisher = detail?.publisher && detail.publisher !== game.developer ? detail.publisher : null;
  const byline = [game.developer ? `By ${game.developer}` : null, publisher ? `Published by ${publisher}` : null, year && !Number.isNaN(year) ? String(year) : null].filter(Boolean).join(" · ");
  if (!description && !byline) return null;
  return (
    <section aria-label="About" className="max-w-[68ch]">
      {description && (
        <>
          <p className={cx("text-sm leading-[1.6] text-fg [text-wrap:pretty]", long && !open && "line-clamp-3")}>{description}</p>
          {long && (
            <button type="button" aria-expanded={open} onClick={() => setOpen((o) => !o)} className="mt-1 rounded-sm text-sm font-medium text-accent-text transition-opacity duration-150 hover:opacity-80">
              {open ? "Show less" : "Show more"}
            </button>
          )}
        </>
      )}
      {byline && <p className={cx("text-xs text-fg-muted", description && "mt-2")}>{byline}</p>}
    </section>
  );
}
