import { splitPath } from "../../state/format";
import { cx } from "../../ui";

/** A path whose leading folders ellipsize first, so the meaningful tail ("Epic Games\\Fortnite") always shows. */
export function PathText({ path, className, tailSegments = 2 }: { path: string; className?: string; tailSegments?: number }) {
  const { head, tail } = splitPath(path, tailSegments);
  return (
    <span className={cx("flex min-w-0 items-baseline", className)} title={path}>
      {head && <span className="min-w-0 truncate text-fg-muted">{head}</span>}
      <span className="shrink-0 whitespace-nowrap">{tail}</span>
    </span>
  );
}
