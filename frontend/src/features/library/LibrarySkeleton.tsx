import { Skeleton, useScrollViewport } from "../../ui";
import type { LibraryView } from "../../state/libraryPrefs";
import { GRID_GAP, LIST_ROW, ROW_GAP, TILE_TEXT, gridLayout, insetFor } from "./gridLayout";

/** The library's loading state: the same geometry as the real grid or list (same tile size, same gutters), so nothing moves when games arrive. */
export function LibrarySkeleton({ view }: { view: LibraryView }) {
  const [vp, attach] = useScrollViewport();
  const layout = gridLayout(vp.width);
  const pad = insetFor(vp.width);

  if (view === "list") {
    const rows = Math.ceil(vp.height / LIST_ROW);
    return (
      <div ref={attach} role="status" aria-label="Loading your library" className="h-full overflow-hidden [scrollbar-gutter:stable]" style={{ paddingInline: pad, paddingTop: 40 }}>
        {Array.from({ length: rows }, (_, i) => (
          <div key={i} className="flex items-center gap-4" style={{ height: LIST_ROW }}>
            <Skeleton className="h-[52px] w-[39px] shrink-0 rounded-md" />
            <div className="flex-1">
              <Skeleton className="h-3.5 w-[34%] rounded-sm" />
              <Skeleton className="mt-2 h-3 w-[20%] rounded-sm" />
            </div>
          </div>
        ))}
      </div>
    );
  }

  const rows = Math.ceil(vp.height / layout.rowH) + (vp.height > 0 ? 0 : 1);
  return (
    <div ref={attach} role="status" aria-label="Loading your library" className="h-full overflow-hidden [scrollbar-gutter:stable]" style={{ paddingTop: 8 }}>
      <div
        className="grid"
        style={{
          gridTemplateColumns: `repeat(${layout.cols}, ${layout.tileW}px)`,
          columnGap: GRID_GAP,
          rowGap: ROW_GAP,
          marginInline: layout.pad,
        }}
      >
        {Array.from({ length: rows * layout.cols }, (_, i) => (
          <div key={i} style={{ height: layout.coverH + TILE_TEXT }}>
            <Skeleton className="rounded-lg" style={{ height: layout.coverH, animationDelay: `${(i % layout.cols) * 40}ms` }} />
            <Skeleton className="mt-3 h-3.5 w-[72%] rounded-sm" />
            <Skeleton className="mt-1.5 h-3 w-[44%] rounded-sm" />
          </div>
        ))}
      </div>
    </div>
  );
}
