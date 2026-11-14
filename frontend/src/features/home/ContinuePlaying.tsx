import { Play } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { JOB_VERB } from "../../state/derive";
import { formatRelativeTime } from "../../state/format";
import { goGame } from "../../state/router";
import { Badge, Button, GameCover, navigateWithCover } from "../../ui";
import { quickAction, runQuick } from "../library/status";

/** One short line: when you last played (or what the game is doing right now). */
function meta(g: GameSummary): string {
  if (g.runState !== "idle") return "Playing now";
  if (g.activeJobKind) return JOB_VERB[g.activeJobKind];
  return g.lastPlayedAt ? formatRelativeTime(g.lastPlayedAt) : "Not played yet";
}

/** A large card: key art, then the title with Play beside it. No frame: the art is the card, the text sits on the page. */
export function ContinueCard({ game }: { game: GameSummary }) {
  const quick = quickAction(game);
  const running = game.runState !== "idle";
  return (
    <li className="relative min-w-0">
      <button
        type="button"
        aria-label={game.title}
        onClick={(e) => navigateWithCover(() => goGame(game.appName), e.currentTarget.querySelector<HTMLElement>("[data-cover]"))}
        className="group/card block w-full rounded-xl text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
      >
        <span className="block rounded-xl transition-[transform,box-shadow] duration-150 ease-out group-hover/card:-translate-y-[3px] group-hover/card:shadow-[0_14px_28px_-12px_oklch(0_0_0/0.5)]">
          <GameCover art={game.art} title={game.title} shape="wide" priority className="rounded-xl shadow-[inset_0_0_0_1px_var(--g-border-strong)]" />
        </span>
        <span className="mt-3 block pr-20">
          <span className="block truncate text-base font-medium text-fg">{game.title}</span>
          <span className="mt-0.5 block truncate text-xs text-fg-muted">{meta(game)}</span>
        </span>
      </button>
      {(quick || running) && (
        <div className="absolute bottom-0 right-0">
          {running ? (
            <Badge tone="success" dot>
              Playing
            </Badge>
          ) : (
            quick && (
              <Button
                size="sm"
                variant={quick.kind === "play" ? "primary" : "secondary"}
                aria-label={`${quick.label} ${game.title}`}
                icon={quick.kind === "play" ? <Play size={12} className="fill-current" /> : undefined}
                onClick={() => void runQuick(quick.kind, game.appName)}
              >
                {quick.label}
              </Button>
            )
          )}
        </div>
      )}
    </li>
  );
}
