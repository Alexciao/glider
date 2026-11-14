import { memo, useState, type MouseEvent, type ReactNode } from "react";
import { ArrowDownToLine, Check, Download, Play, TriangleAlert, Wrench } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { Button, GameCover, cx } from "../../ui";
import { CoverProgress } from "./CoverProgress";
import { MoreButton } from "./GameMenu";
import { describeState, quickAction, runQuick } from "./status";

const glass = "bg-[oklch(0.14_0.006_265/0.72)] ring-1 ring-inset ring-white/10";

interface Badge {
  label: string;
  node: ReactNode;
}

/**
 * The tile's single status mark, top-left of the cover: the most important thing that is true of this game. A game with a
 * job shows its progress ring instead (no badge); a game that is simply not installed shows nothing.
 */
function badgeFor(g: GameSummary): Badge | null {
  if (g.activeJobId) return null;
  if (g.runState !== "idle") return { label: "Playing", node: <span className={cx("grid size-5 place-items-center rounded-full", glass)}><span className="size-2 animate-[g-breathe_1.8s_ease-in-out_infinite] rounded-full bg-[oklch(0.82_0.15_155)]" /></span> };
  if (g.installStatus === "broken") return { label: "Needs repair", node: <span className={cx("grid size-5 place-items-center rounded-full text-[oklch(0.8_0.15_25)]", glass)}><TriangleAlert size={11} strokeWidth={2.5} /></span> };
  if (g.installStatus === "installed" && g.updateAvailable) return { label: "Update available", node: <span className="grid size-5 place-items-center rounded-full bg-accent text-accent-fg ring-1 ring-inset ring-white/20"><ArrowDownToLine size={11} strokeWidth={2.5} /></span> };
  if (g.installStatus === "installed") return { label: "Installed", node: <span className={cx("grid size-5 place-items-center rounded-full text-[oklch(0.82_0.15_155)]", glass)}><Check size={12} strokeWidth={2.75} /></span> };
  if (g.thirdParty !== "none" && g.thirdPartyName) return { label: g.thirdPartyName, node: <span className={cx("inline-flex h-5 items-center rounded-full px-2 text-2xs font-medium text-white/90", glass)}>{g.thirdPartyName}</span> };
  return null;
}

export interface GameTileProps {
  game: GameSummary;
  index: number;
  count: number;
  x: number;
  y: number;
  width: number;
  coverH: number;
  /** The one tile that is a tab stop (roving tabindex). */
  tabbable: boolean;
  priority?: boolean;
  onOpen: (game: GameSummary, cover: HTMLElement | null) => void;
}

/**
 * A grid tile: 3:4 cover and the title. At most one badge; on hover it lifts a few pixels and offers one quick action, and a
 * "…" button (or right-click) opens the full menu. The tile is one button (the page link); its extras are siblings, not nested.
 */
export const GameTile = memo(function GameTile({ game, index, count, x, y, width, coverH, tabbable, priority, onOpen }: GameTileProps) {
  const quick = quickAction(game);
  const badge = badgeFor(game);
  // The hover extras (quick action, "…") exist only for the tile under the pointer: 30 tiles never pay for 60 buttons.
  const [hot, setHot] = useState(false);
  const open = (e: MouseEvent<HTMLButtonElement>) => onOpen(game, e.currentTarget.querySelector<HTMLElement>("[data-cover]"));
  return (
    <li
      data-index={index}
      aria-posinset={index + 1}
      aria-setsize={count}
      className="absolute left-0 top-0 list-none"
      style={{ width, height: coverH + 28, transform: `translate(${x}px, ${y}px)` }}
    >
      <div
        onPointerEnter={() => setHot(true)}
        onPointerLeave={() => setHot(false)}
        className={cx("group/tile relative transition-transform duration-150 ease-out hover:-translate-y-[3px]", game.hidden && "opacity-60")}
      >
          <button
            type="button"
            data-tile-index={index}
            tabIndex={tabbable ? 0 : -1}
            onClick={open}
            className="block w-full rounded-lg text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
          >
            <span className="relative block">
              <span
                aria-hidden
                className="pointer-events-none absolute inset-0 rounded-lg opacity-0 shadow-[0_14px_28px_-10px_oklch(0_0_0/0.55)] transition-opacity duration-150 group-hover/tile:opacity-100"
              />
              <GameCover art={game.art} title={game.title} priority={priority} className="rounded-lg" style={{ height: coverH }}>
                <span aria-hidden className="pointer-events-none absolute inset-0 rounded-[inherit] shadow-[inset_0_0_0_1px_var(--g-border-strong)]" />
                {game.activeJobId && <CoverProgress jobId={game.activeJobId} />}
              </GameCover>
              {badge && (
                <span aria-hidden title={badge.label} className="absolute left-2 top-2 z-[2]">
                  {badge.node}
                </span>
              )}
            </span>
            <span className="mt-2 flex h-5 items-center text-sm font-medium text-fg">
              <span className="truncate" title={game.title}>{game.title}</span>
              <span className="sr-only">. {describeState(game)}</span>
            </span>
          </button>

          {/* Siblings of the page button, placed over the cover. */}
          {hot && (
            <div className="pointer-events-none absolute inset-x-0 top-0 [animation:g-fade-in_150ms_var(--ease-out)]" style={{ height: coverH }}>
              {quick && <span aria-hidden className="absolute inset-x-0 bottom-0 h-1/3 rounded-b-lg bg-gradient-to-t from-black/55 to-transparent" />}
              <MoreButton game={game} className="pointer-events-auto absolute right-2 top-2 z-[3]" />
              {quick && (
                <Button
                  size="sm"
                  variant="primary"
                  tabIndex={-1}
                  aria-label={`${quick.label} ${game.title}`}
                  icon={quick.kind === "play" ? <Play size={12} className="fill-current" /> : quick.kind === "install" ? <Download size={13} /> : <Wrench size={13} />}
                  onClick={() => void runQuick(quick.kind, game.appName)}
                  className="pointer-events-auto absolute inset-x-2 bottom-2 z-[3] shadow-md"
                >
                  {quick.label}
                </Button>
              )}
            </div>
          )}
        </div>
    </li>
  );
});
