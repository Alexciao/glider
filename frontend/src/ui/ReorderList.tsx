import { useCallback, useEffect, useId, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { GripVertical } from "lucide-react";
import { cx } from "./cx";

/**
 * A vertical list the user can reorder by dragging a grip or from the keyboard.
 *
 * Pointer: the grip follows the pointer 1:1 (captured, so it keeps tracking outside the list), neighbours slide aside
 * by exactly one row height, and on release the row settles into its slot before the move is committed.
 * Keyboard: focus the grip, Space or Enter picks the row up, Arrow Up/Down moves it (announced), Space or Enter drops it,
 * Escape puts it back. The DOM order only changes when the caller's `items` change after `onMove`, so focus is never
 * lost mid-move; the grip is re-focused once the new order arrives.
 */

export interface ReorderCtx {
  index: number;
  count: number;
  dragging: boolean;
  /** Render this where the row wants its grip. */
  handle: ReactNode;
}

export interface ReorderListProps<T> {
  items: readonly T[];
  getId: (item: T) => string;
  /** Names the row for assistive tech ("Fortnite"). */
  getLabel: (item: T) => string;
  /** Called once per completed move with the row's new index in `items`. */
  onMove: (id: string, toIndex: number) => void;
  renderItem: (item: T, ctx: ReorderCtx) => ReactNode;
  className?: string;
  /** Props for each `<li>`. */
  itemClassName?: string;
  "aria-label": string;
}

interface Active {
  id: string;
  from: number;
  to: number;
  mode: "pointer" | "keyboard";
  /** Pointer offset in px (pointer mode). */
  dy: number;
  /** Row heights at pickup, in px. */
  heights: number[];
  /** Pointer: the row has been released and is settling. */
  settling?: boolean;
}

const SETTLE_MS = 160;

/** Offset of slot `to` relative to row `from`, from the heights of the rows in between. */
export function slotOffset(heights: readonly number[], from: number, to: number): number {
  let sum = 0;
  if (to > from) for (let i = from + 1; i <= to; i++) sum += heights[i] ?? 0;
  else for (let i = to; i < from; i++) sum -= heights[i] ?? 0;
  return sum;
}

export function ReorderList<T>({ items, getId, getLabel, onMove, renderItem, className, itemClassName, ...aria }: ReorderListProps<T>) {
  const [active, setActive] = useState<Active | null>(null);
  const [announce, setAnnounce] = useState("");
  const rows = useRef(new Map<string, HTMLLIElement>());
  const origin = useRef<{ y: number; mids: number[] } | null>(null);
  const refocus = useRef<string | null>(null);
  const settle = useRef(0);
  const hintId = useId();
  const n = items.length;

  const measure = useCallback((): number[] => items.map((it) => rows.current.get(getId(it))?.getBoundingClientRect().height ?? 0), [items, getId]);

  // After a keyboard drop the new order arrives with the next `items`; put focus back on the moved row's grip.
  useEffect(() => {
    const id = refocus.current;
    if (!id) return;
    refocus.current = null;
    rows.current.get(id)?.querySelector<HTMLElement>("[data-reorder-handle]")?.focus();
  }, [items]);
  useEffect(() => () => window.clearTimeout(settle.current), []);

  const say = (it: T, pos: number, verb: string) => setAnnounce(`${verb} ${getLabel(it)}, position ${pos} of ${n}.`);

  const finish = (a: Active, commit: boolean) => {
    setActive(null);
    origin.current = null;
    if (commit && a.to !== a.from) onMove(a.id, a.to);
  };

  const onPointerDown = (e: PointerEvent<HTMLButtonElement>, i: number) => {
    if (e.button !== 0 || active) return;
    const it = items[i]!;
    const heights = measure();
    const mids = items.map((x) => {
      const r = rows.current.get(getId(x))?.getBoundingClientRect();
      return r ? r.top + r.height / 2 : 0;
    });
    e.currentTarget.setPointerCapture(e.pointerId);
    origin.current = { y: e.clientY, mids };
    setActive({ id: getId(it), from: i, to: i, mode: "pointer", dy: 0, heights });
  };

  const onPointerMove = (e: PointerEvent<HTMLButtonElement>) => {
    const a = active;
    const o = origin.current;
    if (!a || a.mode !== "pointer" || a.settling || !o) return;
    const dy = e.clientY - o.y;
    const centre = o.mids[a.from]! + dy;
    let to = 0;
    for (let j = 0; j < o.mids.length; j++) if (j !== a.from && o.mids[j]! < centre) to++;
    setActive({ ...a, dy, to });
  };

  const release = (e: PointerEvent<HTMLButtonElement>, cancel: boolean) => {
    const a = active;
    if (!a || a.mode !== "pointer" || a.settling) return;
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    const to = cancel ? a.from : a.to;
    // Settle into the slot, then commit: the row glides home instead of snapping.
    setActive({ ...a, to, dy: slotOffset(a.heights, a.from, to), settling: true });
    settle.current = window.setTimeout(() => finish({ ...a, to }, !cancel), SETTLE_MS);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLButtonElement>, i: number) => {
    const it = items[i]!;
    const a = active;
    if (!a) {
      if (e.key === " " || e.key === "Enter") {
        e.preventDefault();
        setActive({ id: getId(it), from: i, to: i, mode: "keyboard", dy: 0, heights: measure() });
        say(it, i + 1, "Picked up");
      }
      return;
    }
    if (a.mode !== "keyboard" || a.id !== getId(it)) return;
    if (e.key === "ArrowUp" || e.key === "ArrowDown") {
      e.preventDefault();
      const to = Math.min(n - 1, Math.max(0, a.to + (e.key === "ArrowUp" ? -1 : 1)));
      if (to !== a.to) {
        setActive({ ...a, to });
        say(it, to + 1, "Moved");
      }
    } else if (e.key === " " || e.key === "Enter") {
      e.preventDefault();
      refocus.current = a.to !== a.from ? a.id : null;
      say(it, a.to + 1, "Dropped");
      finish(a, true);
    } else if (e.key === "Escape") {
      e.preventDefault();
      say(it, a.from + 1, "Cancelled, back at");
      finish(a, false);
    }
  };

  return (
    <>
      <ul className={className} aria-label={aria["aria-label"]}>
        {items.map((it, i) => {
          const id = getId(it);
          const dragging = active?.id === id;
          let shift = 0;
          if (active && !dragging) {
            const h = active.heights[active.from] ?? 0;
            if (active.from < active.to && i > active.from && i <= active.to) shift = -h;
            else if (active.from > active.to && i >= active.to && i < active.from) shift = h;
          }
          const own = dragging && active ? (active.mode === "pointer" && !active.settling ? active.dy : slotOffset(active.heights, active.from, active.to)) : 0;
          const y = dragging ? own : shift;
          const handle =
            n > 1 ? (
              <button
                type="button"
                data-reorder-handle
                aria-label={`Reorder ${getLabel(it)}, position ${i + 1} of ${n}`}
                aria-describedby={hintId}
                aria-pressed={dragging && active?.mode === "keyboard" ? true : undefined}
                onPointerDown={(e) => onPointerDown(e, i)}
                onPointerMove={onPointerMove}
                onPointerUp={(e) => release(e, false)}
                onPointerCancel={(e) => release(e, true)}
                onKeyDown={(e) => onKeyDown(e, i)}
                onBlur={() => active?.mode === "keyboard" && active.id === id && finish(active, false)}
                className={cx(
                  "grid h-8 w-6 shrink-0 touch-none place-items-center rounded-md text-fg-subtle transition-colors duration-150 hover:bg-surface-hover hover:text-fg",
                  "cursor-grab active:cursor-grabbing aria-pressed:bg-surface-3 aria-pressed:text-fg",
                  dragging && "cursor-grabbing text-fg",
                )}
              >
                <GripVertical size={14} aria-hidden />
              </button>
            ) : null;
          return (
            <li
              key={id}
              ref={(el) => {
                if (el) rows.current.set(id, el);
                else rows.current.delete(id);
              }}
              data-dragging={dragging || undefined}
              className={cx(
                "relative",
                (dragging || y !== 0 || active?.settling) && "will-change-transform",
                dragging ? "z-10 bg-bg-elevated shadow-lg" : "z-0",
                // Neighbours slide with a transition; the dragged row tracks the pointer without one.
                !(dragging && active?.mode === "pointer" && !active.settling) && "transition-[transform,box-shadow] duration-200 ease-out",
                itemClassName,
              )}
              style={{ transform: y ? `translateY(${y}px)` : undefined }}
            >
              {renderItem(it, { index: i, count: n, dragging, handle })}
            </li>
          );
        })}
      </ul>
      <span id={hintId} className="sr-only">
        Press Space to pick up, Arrow Up or Down to move, Space to drop, Escape to cancel.
      </span>
      <span className="sr-only" role="status" aria-live="assertive">
        {announce}
      </span>
    </>
  );
}
