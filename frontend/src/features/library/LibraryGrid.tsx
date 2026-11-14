import { memo, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import type { GameSummary } from "../../ipc/contract.gen";
import type { LibraryView } from "../../state/libraryPrefs";
import { goGame } from "../../state/router";
import { computeRange, cx, useScrollViewport } from "../../ui";
import { LibraryContextMenu } from "./GameMenu";
import { GameListRow, LIST_COL } from "./GameListRow";
import { GameTile } from "./GameTile";
import { GRID_GAP, LIST_ROW, gridLayout, listColumns, padFor } from "./gridLayout";

/** Scroll offsets survive leaving the Library for a game's page and coming back. */
const savedScroll: Record<LibraryView, number> = { grid: 0, list: 0 };

const GRID_TOP = 8;
const LIST_HEADER = 32;
const BOTTOM = 40;

function ListHeader({ cols }: { cols: ReturnType<typeof listColumns> }) {
  return (
    <div aria-hidden className="sticky top-0 z-10 -mx-2 flex h-8 items-center gap-4 bg-bg-elevated px-2 text-xs text-fg-subtle">
      <span className="w-[39px] shrink-0" />
      <span className="min-w-0 flex-1">Name</span>
      {cols.status && <span className={cx("shrink-0", LIST_COL.status)}>Status</span>}
      {cols.size && <span className={cx("shrink-0", LIST_COL.size)}>Size</span>}
      {cols.played && <span className={cx("shrink-0", LIST_COL.played)}>Last played</span>}
      <span className="w-[88px] shrink-0" />
    </div>
  );
}

export interface LibraryGridProps {
  games: readonly GameSummary[];
  view: LibraryView;
  /** Changes when the result set changes meaning (search, filter, sort): the list scrolls back to the top. */
  resetKey: string;
  /** Reports whether the content is scrolled, so the toolbar can draw its divider. */
  onScrolled?: (scrolled: boolean) => void;
}

/**
 * The virtualized library: a window of absolutely positioned tiles (grid) or rows (list) over one tall spacer. Only the
 * visible rows plus two of overscan exist in the DOM, so 600 games cost the same as 30. Arrow keys, Home/End and
 * Page Up/Down move focus through the tiles (roving tabindex: one tab stop for the whole library).
 */
export const LibraryGrid = memo(function LibraryGrid({ games, view, resetKey, onScrolled }: LibraryGridProps) {
  const list = useRef<HTMLUListElement>(null);
  const [vp, attachViewport] = useScrollViewport();
  // The page owns the element (it scrolls it imperatively); the viewport hook is attached through the same ref.
  const scroller = useRef<HTMLDivElement | null>(null);
  const setScroller = useCallback(
    (el: HTMLDivElement | null) => {
      scroller.current = el;
      return attachViewport(el);
    },
    [attachViewport],
  );
  const [active, setActive] = useState(0);
  const pendingFocus = useRef<number | null>(null);

  const isGrid = view === "grid";
  const layout = useMemo(() => gridLayout(vp.width), [vp.width]);
  const cols = isGrid ? layout.cols : 1;
  const rowH = isGrid ? layout.rowH : LIST_ROW;
  const rowCount = Math.ceil(games.length / cols);
  const head = isGrid ? GRID_TOP : LIST_HEADER;
  const win = computeRange(Math.max(0, vp.top - head), vp.height, rowH, rowCount, 1);
  const total = rowCount * rowH + head + BOTTOM;
  const listCols = useMemo(() => listColumns(vp.width), [vp.width]);

  // Restore the scroll position on return; start at the top when the result set changes.
  const firstRun = useRef(true);
  useLayoutEffect(() => {
    const el = scroller.current;
    if (!el) return;
    if (firstRun.current) {
      firstRun.current = false;
      el.scrollTop = savedScroll[view];
    } else {
      el.scrollTop = 0;
    }
    // Only the result identity matters here, not the view or window size.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [resetKey]);
  useEffect(() => {
    const el = scroller.current;
    return () => {
      if (el) savedScroll[view] = el.scrollTop;
    };
  }, [view]);

  const scrolled = vp.top > 0;
  useEffect(() => onScrolled?.(scrolled), [scrolled, onScrolled]);

  // The roving tab stop must always be a rendered tile.
  const lastIndex = Math.max(0, games.length - 1);
  const firstRendered = win.first * cols;
  const lastRendered = Math.min(lastIndex, (win.last + 1) * cols - 1);
  const current = Math.min(active, lastIndex);
  const tabStop = current >= firstRendered && current <= lastRendered ? current : Math.min(lastIndex, win.visibleFirst * cols);

  const open = useCallback((game: GameSummary) => goGame(game.appName), []);

  const focusTile = useCallback(
    (i: number) => {
      const el = scroller.current;
      if (el) {
        const row = Math.floor(i / cols);
        const top = row * rowH + head;
        if (top < el.scrollTop + head) el.scrollTop = Math.max(0, top - head - 8);
        else if (top + rowH > el.scrollTop + el.clientHeight) el.scrollTop = top + rowH - el.clientHeight + 8;
      }
      pendingFocus.current = i;
      setActive(i);
    },
    [cols, rowH, head],
  );

  // After the keyboard moved the tab stop, focus that tile as soon as it is rendered (it may need a scroll first).
  useEffect(() => {
    const i = pendingFocus.current;
    if (i === null) return;
    const el = list.current?.querySelector<HTMLElement>(`[data-tile-index="${i}"]`);
    if (el) {
      pendingFocus.current = null;
      el.focus({ preventScroll: true });
    }
  });

  const onKeyDown = (e: KeyboardEvent<HTMLUListElement>) => {
    const target = e.target as HTMLElement;
    if (e.altKey || e.ctrlKey || e.metaKey || target.dataset.tileIndex === undefined) return;
    const i = Number(target.dataset.tileIndex);
    const screenRows = Math.max(1, win.visibleLast - win.visibleFirst);
    let next: number;
    switch (e.key) {
      case "ArrowRight": next = isGrid ? i + 1 : i; break;
      case "ArrowLeft": next = isGrid ? i - 1 : i; break;
      case "ArrowDown": next = i + cols; break;
      case "ArrowUp": next = i - cols; break;
      case "PageDown": next = i + cols * screenRows; break;
      case "PageUp": next = i - cols * screenRows; break;
      case "Home": next = 0; break;
      case "End": next = lastIndex; break;
      default: return;
    }
    e.preventDefault();
    // Down from a short last row lands on the last tile; Up from the first row stays put.
    if (next > lastIndex) next = Math.floor(i / cols) < rowCount - 1 ? lastIndex : i;
    next = Math.max(0, next);
    if (next !== i) focusTile(next);
  };

  const rows: React.ReactNode[] = [];
  for (let r = win.first; r <= win.last; r++) {
    for (let c = 0; c < cols; c++) {
      const i = r * cols + c;
      const game = games[i];
      if (!game) break;
      rows.push(
        isGrid ? (
          <GameTile
            key={game.appName}
            game={game}
            index={i}
            count={games.length}
            x={layout.pad + c * (layout.tileW + GRID_GAP)}
            y={GRID_TOP + r * rowH}
            width={layout.tileW}
            coverH={layout.coverH}
            tabbable={i === tabStop}
            priority={r <= win.visibleFirst + 1}
            onOpen={open}
          />
        ) : (
          <GameListRow key={game.appName} game={game} index={i} count={games.length} y={r * rowH} cols={listCols} tabbable={i === tabStop} onOpen={open} />
        ),
      );
    }
  }

  return (
    <LibraryContextMenu games={games}>
    <div ref={setScroller} className="h-full overflow-y-auto [scrollbar-gutter:stable]">
      {!isGrid && (
        <div style={{ paddingInline: padFor(vp.width) }}>
          <ListHeader cols={listCols} />
        </div>
      )}
      <ul
        ref={list}
        aria-label="Games"
        onKeyDown={onKeyDown}
        onFocus={(e) => {
          const idx = (e.target as HTMLElement).dataset.tileIndex;
          if (idx !== undefined) setActive(Number(idx));
        }}
        className="relative"
        style={{ height: total - (isGrid ? 0 : LIST_HEADER), marginInline: isGrid ? 0 : padFor(vp.width) }}
      >
        {rows}
      </ul>
    </div>
    </LibraryContextMenu>
  );
});
