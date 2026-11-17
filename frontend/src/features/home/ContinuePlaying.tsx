import { useState } from "react";
import { Play, Wrench } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { JOB_VERB } from "../../state/derive";
import { formatRelativeTime } from "../../state/format";
import { goGame } from "../../state/router";
import { Button, GameCover, StatusDot } from "../../ui";
import { SharedCover } from "../library/SharedCover";
import { quickAction, runQuick } from "../library/status";

/** One short line: when you last played (or what the game is doing right now). */
function meta(g: GameSummary): string {
  if (g.runState !== "idle") return "Playing now";
  if (g.activeJobKind) return JOB_VERB[g.activeJobKind];
  return g.lastPlayedAt ? formatRelativeTime(g.lastPlayedAt) : "Not played yet";
}

/**
 * A large card: key art, then the title and when you last played. No frame: the art is the card, the text sits on the page.
 * Play appears on the art on hover or focus (as on library tiles), so at rest the page has no competing primary buttons.
 */
export function ContinueCard({ game }: { game: GameSummary }) {
  const quick = quickAction(game);
  const running = game.runState !== "idle";
  // Armed while hovered or focused: only then does the cover carry the shared-element id (it morphs into the game page hero).
  const [armed, setArmed] = useState(false);
  return (
    <li className="group/card relative min-w-0" onPointerEnter={() => setArmed(true)} onPointerLeave={() => setArmed(false)} onFocus={() => setArmed(true)} onBlur={() => setArmed(false)}>
      <div className="transition-transform duration-150 ease-out group-hover/card:-translate-y-[3px]">
        <button
          type="button"
          aria-label={game.title}
          onClick={() => goGame(game.appName)}
          className="block w-full rounded-xl text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
        >
          <span className="block rounded-xl transition-shadow duration-150 ease-out group-hover/card:shadow-[0_14px_28px_-12px_oklch(0_0_0/0.5)]">
            <span className="relative block">
              <GameCover art={game.art} title={game.title} shape="wide" priority className="rounded-xl shadow-[inset_0_0_0_1px_var(--g-border-strong)]" />
              {armed && <SharedCover id={game.appName} radius={16} shape="wide" />}
            </span>
          </span>
          <span className="mt-3 block">
            <span className="block truncate text-base font-medium text-fg">{game.title}</span>
            <span className="mt-0.5 block truncate text-xs text-fg-muted">{meta(game)}</span>
          </span>
        </button>
        {/* A sibling of the card button (never nested), laid over the bottom-left of the art. */}
        <div className="pointer-events-none absolute inset-x-0 top-0 aspect-video">
          {running ? (
            <span className="absolute bottom-3 left-3 inline-flex h-6 items-center gap-1.5 rounded-full bg-black/55 px-2.5 text-xs font-medium text-white/90 ring-1 ring-inset ring-white/15 backdrop-blur-sm">
              <StatusDot tone="success" pulse />
              Playing
            </span>
          ) : (
            quick && (
              <Button
                size="sm"
                variant={quick.kind === "play" ? "primary" : "secondary"}
                aria-label={`${quick.label} ${game.title}`}
                icon={quick.kind === "play" ? <Play size={12} className="fill-current" /> : <Wrench size={14} />}
                onClick={() => void runQuick(quick.kind, game.appName)}
                className="pointer-events-auto absolute bottom-3 left-3 opacity-0 shadow-md transition-opacity duration-150 ease-out focus-visible:opacity-100 group-focus-within/card:opacity-100 group-hover/card:opacity-100"
              >
                {quick.label}
              </Button>
            )
          )}
        </div>
      </div>
    </li>
  );
}
