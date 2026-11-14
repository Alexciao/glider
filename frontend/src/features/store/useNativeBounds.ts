import { useCallback, useEffect, useRef, type RefObject } from "react";
import type { StoreBounds } from "../../ipc/contract.gen";

/** CSS px, whole numbers: sub-pixel jitter must not flood the host with setBounds calls. */
export function roundBounds(r: { left: number; top: number; width: number; height: number }): StoreBounds {
  return { x: Math.round(r.left), y: Math.round(r.top), width: Math.max(0, Math.round(r.width)), height: Math.max(0, Math.round(r.height)) };
}

export const sameBounds = (a: StoreBounds | null, b: StoreBounds | null): boolean =>
  a !== null && b !== null && a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;

/**
 * Reports the element's rectangle (CSS px relative to the page, which is also the WebView2 client area) whenever it changes:
 * ResizeObserver + window resize, coalesced to one call per animation frame and de-duplicated. `measure()` reads it right now
 * (for the first `store.show`). The native Store view is placed from these numbers, so they must follow layout exactly.
 */
export function useNativeBounds(
  ref: RefObject<HTMLElement | null>,
  onBounds: (b: StoreBounds) => void,
): { measure: () => StoreBounds | null } {
  const last = useRef<StoreBounds | null>(null);
  const cb = useRef(onBounds);
  useEffect(() => {
    cb.current = onBounds;
  });

  const measure = useCallback((): StoreBounds | null => {
    const el = ref.current;
    return el ? roundBounds(el.getBoundingClientRect()) : null;
  }, [ref]);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    let frame = 0;
    const flush = () => {
      frame = 0;
      const b = measure();
      if (!b || sameBounds(b, last.current)) return;
      last.current = b;
      cb.current(b);
    };
    const schedule = () => {
      if (!frame) frame = requestAnimationFrame(flush);
    };
    const ro = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(schedule);
    ro?.observe(el);
    window.addEventListener("resize", schedule);
    schedule();
    return () => {
      ro?.disconnect();
      window.removeEventListener("resize", schedule);
      if (frame) cancelAnimationFrame(frame);
    };
  }, [ref, measure]);

  return { measure };
}
