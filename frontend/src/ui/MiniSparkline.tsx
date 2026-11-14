import { useEffect, useRef } from "react";
import { cx } from "./cx";

export interface MiniSparklineProps {
  /** Chronological values, newest last. Read imperatively on every notification. */
  read: () => ArrayLike<number>;
  subscribe: (notify: () => void) => () => void;
  /** Any CSS colour, normally a token. */
  color?: string;
  /** Draw nothing while false (hidden window). Default true. */
  active?: boolean;
  /** Newest N samples to show. Default 60. */
  window?: number;
  /** Accessible description. Omit to hide the sparkline from assistive tech (when text already carries the number). */
  label?: string;
  className?: string;
}

const W = 100;
const H = 24;

/**
 * A 24px trend line for rows and compact cards. One path written straight to the DOM per notification, no React
 * state, nothing drawn while hidden; the scale is the window's own peak so the shape (steady, dipping) is what reads.
 */
export function MiniSparkline({ read, subscribe, color = "var(--g-chart-net)", active = true, window: win = 60, label, className }: MiniSparklineProps) {
  const line = useRef<SVGPathElement>(null);
  const area = useRef<SVGPathElement>(null);
  useEffect(() => {
    if (!active) return;
    const draw = () => {
      const all = read();
      const n = Math.min(all.length, win);
      if (!line.current || !area.current) return;
      if (n < 2) {
        line.current.setAttribute("d", "");
        area.current.setAttribute("d", "");
        return;
      }
      let peak = 1;
      for (let i = all.length - n; i < all.length; i++) peak = Math.max(peak, all[i]!);
      // The newest `win` samples span the full width, so a short history is a short, readable line, not a sliver.
      const step = W / (n - 1);
      const x0 = 0;
      let d = "";
      for (let i = 0; i < n; i++) {
        const x = x0 + i * step;
        const y = H - 2 - (all[all.length - n + i]! / (peak * 1.1)) * (H - 4);
        d += `${i === 0 ? "M" : "L"}${x.toFixed(1)} ${y.toFixed(1)}`;
      }
      line.current.setAttribute("d", d);
      area.current.setAttribute("d", `${d}L${W} ${H}L${x0.toFixed(1)} ${H}Z`);
    };
    draw();
    return subscribe(draw);
  }, [read, subscribe, active, win]);
  return (
    <svg
      viewBox={`0 0 ${W} ${H}`}
      preserveAspectRatio="none"
      className={cx("h-6 w-20 shrink-0 overflow-visible", className)}
      role={label ? "img" : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    >
      <path ref={area} fill={color} fillOpacity="0.14" />
      <path ref={line} fill="none" stroke={color} strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
    </svg>
  );
}
