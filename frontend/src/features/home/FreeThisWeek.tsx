import { Check } from "lucide-react";
import type { FreeGame } from "../../ipc/contract.gen";
import { freeWindowLabel, useFreeGames } from "../../state/freeGames";
import { openStore, productTarget } from "../../state/storeTarget";
import { Skeleton, cx } from "../../ui";
import { HomeSection } from "./HomeParts";

const SHOWN = 3;

function FreeTile({ game }: { game: FreeGame }) {
  return (
    <li className="min-w-0">
      <button
        type="button"
        onClick={() => openStore(productTarget(game.slug))}
        className="group/free block w-full rounded-xl text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
      >
        <span className="block rounded-xl transition-[transform,box-shadow] duration-150 ease-out group-hover/free:-translate-y-[3px] group-hover/free:shadow-[0_14px_28px_-12px_oklch(0_0_0/0.5)]">
          <span className="relative block aspect-video overflow-hidden rounded-xl bg-surface-3 shadow-[inset_0_0_0_1px_var(--g-border-strong)]">
            {game.imageUrl && <img src={game.imageUrl} alt="" loading="lazy" decoding="async" draggable={false} className="absolute inset-0 size-full object-cover" />}
          </span>
        </span>
        <span className="mt-3 block truncate text-base font-medium text-fg">{game.title}</span>
        <span className={cx("mt-0.5 flex items-center gap-1.5 text-xs", "text-fg-muted")}>
          {game.owned && <Check size={12} strokeWidth={2.5} aria-hidden />}
          {freeWindowLabel(game)}
        </span>
      </button>
    </li>
  );
}

/**
 * This week's free games: free now first, then what is coming. Each opens its page in the Store. Offline or failed, the
 * section simply isn't there (the title bar already says offline); a skeleton holds its place while loading.
 */
export function FreeThisWeek() {
  const { status, items, offline } = useFreeGames();
  if (items.length === 0 && (offline || status === "error" || status === "ready")) return null;
  return (
    <HomeSection title="Free this week">
      {items.length > 0 ? (
        <ul className="grid grid-cols-3 gap-5">
          {items.slice(0, SHOWN).map((g) => (
            <FreeTile key={g.id} game={g} />
          ))}
        </ul>
      ) : (
        <ul aria-label="Loading free games" role="status" className="grid grid-cols-3 gap-5">
          {[0, 1, 2].map((i) => (
            <li key={i}>
              <Skeleton className="aspect-video rounded-xl" />
              <Skeleton className="mt-3 h-4 w-[58%] rounded-sm" />
            </li>
          ))}
        </ul>
      )}
    </HomeSection>
  );
}
