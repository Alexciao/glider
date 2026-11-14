import { memo, useState } from "react";
import { Download, Play, Star, Wrench } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { formatBytes, formatRelativeTime } from "../../state/format";
import { ArtThumb, Button, ProgressRing, cx } from "../../ui";
import { useJobState } from "./CoverProgress";
import { MoreButton } from "./GameMenu";
import { LIST_ROW, type ListColumns } from "./gridLayout";
import { describeState, quickAction, rowStatus, runQuick } from "./status";

/** Fixed column widths, so the header row in the page lines up with every row. */
export const LIST_COL = { status: "w-36", size: "w-24", played: "w-28" } as const;

function JobStatus({ jobId }: { jobId: string }) {
  const job = useJobState(jobId);
  if (!job) return null;
  return (
    <span className="flex items-center gap-2 text-xs text-fg-muted">
      <ProgressRing size={18} stroke={2.5} value={job.waiting ? 0 : job.percent} paused={job.paused} label={`${job.text}, ${Math.floor(job.percent)}%`} />
      <span className="truncate tabular-nums">
        {job.text}
        {!job.waiting && !job.paused ? ` ${Math.floor(job.percent)}%` : ""}
      </span>
    </span>
  );
}

const TONE_TEXT = { neutral: "text-fg-muted", accent: "text-accent-text", success: "text-success-text", warning: "text-warning-text", danger: "text-danger-text" } as const;

function StatusCell({ game }: { game: GameSummary }) {
  if (game.activeJobId) return <JobStatus jobId={game.activeJobId} />;
  // A game that is simply not installed says nothing: blank rows are the quiet majority.
  if (game.installStatus === "notInstalled") return null;
  const st = rowStatus(game);
  return <span className={cx("text-xs", TONE_TEXT[st.tone])}>{st.text}</span>;
}

export interface GameListRowProps {
  game: GameSummary;
  index: number;
  count: number;
  y: number;
  cols: ListColumns;
  tabbable: boolean;
  onOpen: (game: GameSummary) => void;
}

/** A list row: thumbnail, title and developer, then status, size, last played and playtime as the window allows. */
export const GameListRow = memo(function GameListRow({ game, index, count, y, cols, tabbable, onOpen }: GameListRowProps) {
  const quick = quickAction(game);
  const [hot, setHot] = useState(false);
  const size = game.installStatus === "installed" || game.installStatus === "broken" ? (game.installedSizeBytes ?? game.installSizeBytes) : game.installSizeBytes;
  const open = () => onOpen(game);
  return (
    <li
      data-index={index}
      aria-posinset={index + 1}
      aria-setsize={count}
      className="absolute inset-x-0 top-0 list-none"
      style={{ height: LIST_ROW, transform: `translateY(${y}px)` }}
    >
      <div
          onPointerEnter={() => setHot(true)}
          onPointerLeave={() => setHot(false)}
          className={cx("group/row relative mx-[-8px] h-[60px] rounded-lg transition-colors duration-100 hover:bg-surface-hover", game.hidden && "opacity-60")}
        >
          <button
            type="button"
            data-tile-index={index}
            tabIndex={tabbable ? 0 : -1}
            onClick={open}
            className="flex h-full w-full items-center gap-4 rounded-lg px-2 pr-28 text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-[-2px] focus-visible:outline-focus"
          >
            <ArtThumb src={game.art.tallThumb ?? game.art.tall} placeholder={game.art.placeholder} title={game.title} size="md" />
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-1.5 text-sm font-medium text-fg">
                <span className="truncate" title={game.title}>{game.title}</span>
                {game.favorite && <Star size={12} aria-label="Favorite" className="shrink-0 fill-current text-warning-text" />}
              </span>
              {(game.developer || game.dlcCount > 0) && (
                <span className="block truncate text-xs text-fg-muted">
                  {game.developer && <bdi>{game.developer}</bdi>}
                  {game.developer && game.dlcCount > 0 ? " · " : ""}
                  {game.dlcCount > 0 ? <bdi>{game.dlcCount} DLC</bdi> : null}
                </span>
              )}
            </span>
            {cols.status && (
              <span className={cx("shrink-0", LIST_COL.status)}>
                <StatusCell game={game} />
              </span>
            )}
            {cols.size && <span className={cx("shrink-0 text-xs tabular-nums text-fg-muted", LIST_COL.size)}>{size ? formatBytes(size) : ""}</span>}
            {cols.played && (
              <span className={cx("shrink-0 text-xs text-fg-muted", LIST_COL.played)}>{game.lastPlayedAt ? formatRelativeTime(game.lastPlayedAt) : ""}</span>
            )}
            <span className="sr-only">{describeState(game)}</span>
          </button>
          {hot && (
            <div className="pointer-events-none absolute inset-y-0 right-2 flex items-center gap-1.5 [animation:g-fade-in_150ms_var(--ease-out)]">
              {quick && (
                <Button
                  size="sm"
                  variant="secondary"
                  tabIndex={-1}
                  aria-label={`${quick.label} ${game.title}`}
                  icon={quick.kind === "play" ? <Play size={12} className="fill-current" /> : quick.kind === "install" ? <Download size={13} /> : <Wrench size={13} />}
                  onClick={() => void runQuick(quick.kind, game.appName)}
                  className="pointer-events-auto"
                >
                  {quick.label}
                </Button>
              )}
              <MoreButton game={game} tone="plain" className="pointer-events-auto" />
            </div>
          )}
        </div>
    </li>
  );
});
