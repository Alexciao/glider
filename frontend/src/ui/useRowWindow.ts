import { useCallback, useState } from "react";

/**
 * Fixed-height row windowing for a scroll container. Both library views have uniform row heights, so the visible
 * range is plain arithmetic on `scrollTop` (no measuring, no layout shift): native scrolling stays on the compositor
 * and React re-renders only when the range crosses a row boundary. `useScrollViewport` reports the container's size and
 * scroll position (one read per frame); `computeRange` turns them into the rows to render.
 */
export interface RowWindow {
  /** First and last row to render (inclusive, overscan included); `last < first` when there are no rows. */
  first: number;
  last: number;
  /** Rows that are actually on screen right now (no overscan). */
  visibleFirst: number;
  visibleLast: number;
}

export function computeRange(scrollTop: number, viewport: number, rowHeight: number, rowCount: number, overscan: number) {
  if (rowCount <= 0 || rowHeight <= 0) return { first: 0, last: -1, visibleFirst: 0, visibleLast: -1 };
  const visibleFirst = Math.min(rowCount - 1, Math.max(0, Math.floor(scrollTop / rowHeight)));
  const visibleLast = Math.min(rowCount - 1, Math.max(visibleFirst, Math.ceil((scrollTop + viewport) / rowHeight) - 1));
  return {
    first: Math.max(0, visibleFirst - overscan),
    last: Math.min(rowCount - 1, visibleLast + overscan),
    visibleFirst,
    visibleLast,
  };
}

const FALLBACK = { width: 1000, height: 720 };

export interface ScrollViewport {
  width: number;
  height: number;
  top: number;
}

/**
 * Size and scroll position of a scroll container: `<div ref={vp.ref}>`. The container is measured when it attaches (so the
 * first paint already has the right geometry), then on resize and, once per frame, on scroll. Falls back to a sensible size where nothing is laid out (jsdom).
 */
export function useScrollViewport(): [ScrollViewport, (el: HTMLElement | null) => (() => void) | void] {
  const [state, setState] = useState<ScrollViewport>({ ...FALLBACK, top: 0 });

  const ref = useCallback((el: HTMLElement | null) => {
    if (!el) return;
    let frame = 0;
    const read = (withSize: boolean) =>
      setState((s) => {
        const width = withSize ? el.clientWidth || FALLBACK.width : s.width;
        const height = withSize ? el.clientHeight || FALLBACK.height : s.height;
        const top = el.scrollTop;
        return s.width === width && s.height === height && s.top === top ? s : { width, height, top };
      });
    read(true);
    // Scroll events can fire several times per frame: read the position once per frame.
    const onScroll = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        read(false);
      });
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    const ro = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => read(true));
    ro?.observe(el);
    return () => {
      el.removeEventListener("scroll", onScroll);
      ro?.disconnect();
      if (frame) cancelAnimationFrame(frame);
    };
  }, []);

  return [state, ref];
}
