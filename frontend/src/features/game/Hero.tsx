import { Star } from "lucide-react";
import type { AppSnapshot, GameDetail, GameSummary, ShortcutState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { Tooltip, cx } from "../../ui";
import type { GameAction } from "./actionModel";
import { GameActions } from "./GameActions";
import { GameStatus } from "./GameStatus";
import { HeroArt } from "./HeroArt";
import { HeroProgress } from "./HeroProgress";

/** Same height, radius language and glass fill as the split button's quiet tone, so the row reads as one set. */
function FavoriteButton({ game }: { game: GameSummary }) {
  const on = game.favorite;
  return (
    <Tooltip content={on ? "Remove from favorites" : "Add to favorites"}>
      <button
        type="button"
        aria-pressed={on}
        aria-label="Favorite"
        onClick={() => void actions.setFavorite(game.appName, !on)}
        className={cx(
          "grid size-12 shrink-0 place-items-center rounded-xl border border-border-strong bg-surface-3 backdrop-blur-md",
          "transition-[background-color,color,scale] duration-150 ease-out hover:bg-surface-active press:scale-[0.96]",
          on ? "text-warning-text" : "text-fg-muted hover:text-fg",
        )}
      >
        <Star size={18} className={cx("transition-[fill,scale] duration-150", on && "fill-current")} />
      </button>
    </Tooltip>
  );
}

/**
 * The hero card: key art, the title, one status line, the running job's bar, and the split button. Everything inside is
 * scoped to the dark token set, because AA text on any picture only works against a dark, art-independent scrim.
 * (`data-hero` is what the legibility spec measures.)
 */
export function Hero({
  snap,
  game,
  detail,
  action,
  moment,
  onShortcuts,
}: {
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  moment: boolean;
  onShortcuts?: (state: ShortcutState) => void;
}) {
  const logo = game.art.logo;
  const job = action.job;
  const showBar = !!job && (action.kind === "pause" || action.kind === "resume" || action.kind === "cancel") && job.status !== "queued" && job.status !== "waiting";
  return (
    <header data-hero data-theme="dark" className="relative isolate text-fg">
      <HeroArt appName={game.appName} art={game.art} />
      <div className="relative z-10 flex min-h-[clamp(288px,42vh,400px)] flex-col justify-end p-8 max-[1100px]:p-6">
        <div className="g-rise" style={{ ["--g-i" as string]: 0 }}>
          {logo ? (
            <>
              <h1 className="sr-only">{game.title}</h1>
              <img src={logo} alt="" draggable={false} className="mb-3 max-h-24 w-auto max-w-[min(420px,100%)] object-contain object-left [filter:drop-shadow(0_2px_16px_oklch(0_0_0/0.5))]" />
            </>
          ) : (
            <h1
              title={game.title}
              className="line-clamp-2 max-w-[24ch] text-balance text-3xl font-semibold tracking-tighter text-fg [overflow-wrap:anywhere] [text-shadow:0_2px_24px_oklch(0_0_0/0.4)] max-[1100px]:max-w-[28ch] max-[1100px]:text-2xl"
            >
              {game.title}
            </h1>
          )}
          {(!action.status.quiet || moment) && <GameStatus status={action.status} moment={moment} className="mt-2" />}
        </div>

        <div className="g-rise mt-6 flex items-center gap-2" style={{ ["--g-i" as string]: 1 }}>
          <GameActions snap={snap} game={game} detail={detail} action={action} moment={moment} onShortcuts={onShortcuts} />
          <FavoriteButton game={game} />
          {/* Progress sits beside the buttons, not under the title: the title never jumps when a job starts. */}
          {showBar && job && (
            <div className="ml-4 min-w-0 max-w-[440px] flex-1">
              <HeroProgress job={job} />
            </div>
          )}
        </div>
      </div>
    </header>
  );
}
