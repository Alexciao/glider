import { useEffect, useId, useRef, type ReactNode } from "react";
import { cx } from "./cx";

export interface SparklineProps {
  /** Chronological samples, newest last. Read imperatively on every notification. */
  read: () => ArrayLike<number>;
  /** Called when new data exists; returns an unsubscribe. The chart redraws outside React. */
  subscribe: (notify: () => void) => () => void;
  /** Samples per second, for the "Last N seconds" caption. */
  rate: number;
  /** The window grows with the data between these bounds, so a young chart still fills its width. */
  minWindow: number;
  maxWindow: number;
  /** Formats the y-axis ceiling label. */
  format?: (v: number) => string;
  /** Left caption above the chart. */
  title?: ReactNode;
  /** Accessible description of the chart. */
  label: string;
  className?: string;
  /** Height of the plot area, as a Tailwind class. */
  plotClassName?: string;
}

const W = 600;
const H = 100;

/**
 * Throughput history. The path is written straight to the DOM from a rAF-coalesced subscription,
 * so a 10 Hz feed never re-renders React. Horizontal stretch uses non-scaling strokes.
 */
export function Sparkline({ read, subscribe, rate, minWindow, maxWindow, format, title, label, className, plotClassName = "h-24" }: SparklineProps) {
  const uid = useId().replace(/:/g, "");
  const line = useRef<SVGPathElement>(null);
  const area = useRef<SVGPathElement>(null);
  const ceiling = useRef<HTMLSpanElement>(null);
  const span = useRef<HTMLSpanElement>(null);
  const yMax = useRef(1);

  useEffect(() => {
    let raf = 0;
    const draw = () => {
      raf = 0;
      const data = read();
      const n = data.length;
      if (!line.current || !area.current) return;
      const cap = Math.min(maxWindow, Math.max(minWindow, n));
      if (span.current) span.current.textContent = `Last ${Math.round(cap / rate)} seconds`;
      if (n < 2) {
        line.current.setAttribute("d", "");
        area.current.setAttribute("d", "");
        return;
      }
      // 5-point moving average keeps the raw jitter readable without hiding real dips.
      let peak = 0;
      const sm = new Float64Array(n);
      for (let i = 0; i < n; i++) {
        let sum = 0;
        let c = 0;
        for (let j = Math.max(0, i - 2); j <= Math.min(n - 1, i + 2); j++) {
          sum += data[j] ?? 0;
          c++;
        }
        sm[i] = sum / c;
        if (sm[i]! > peak) peak = sm[i]!;
      }
      // Ceiling rises instantly and relaxes slowly: no axis flicker.
      yMax.current = Math.max(peak * 1.15, yMax.current * 0.985, 1);
      const step = W / Math.max(cap - 1, 1);
      const x0 = W - (n - 1) * step;
      let d = "";
      for (let i = 0; i < n; i++) {
        const x = x0 + i * step;
        const y = H - (sm[i]! / yMax.current) * (H - 4) - 2;
        d += `${i === 0 ? "M" : "L"}${x.toFixed(1)} ${y.toFixed(1)}`;
      }
      line.current.setAttribute("d", d);
      area.current.setAttribute("d", `${d}L${W} ${H}L${x0.toFixed(1)} ${H}Z`);
      if (ceiling.current && format) ceiling.current.textContent = format(yMax.current);
    };
    const schedule = () => {
      if (!raf) raf = requestAnimationFrame(draw);
    };
    draw();
    const off = subscribe(schedule);
    return () => {
      off();
      if (raf) cancelAnimationFrame(raf);
    };
  }, [read, subscribe, rate, minWindow, maxWindow, format]);

  return (
    <div className={className}>
      <div className="mb-2.5 flex items-center justify-between text-xs text-fg-muted">
        <span className="inline-flex items-center gap-1.5">{title}</span>
        <span ref={span} className="tabular-nums" aria-hidden />
      </div>
      <div className={cx("relative", plotClassName)} role="img" aria-label={label}>
        <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" className="absolute inset-0 size-full overflow-visible" aria-hidden>
          <defs>
            <linearGradient id={`${uid}-fill`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="var(--g-accent)" stopOpacity="0.28" />
              <stop offset="100%" stopColor="var(--g-accent)" stopOpacity="0" />
            </linearGradient>
          </defs>
          {[25, 50, 75].map((p) => (
            <line key={p} x1="0" x2={W} y1={p} y2={p} stroke="var(--g-border)" strokeWidth="1" strokeDasharray="2 4" vectorEffect="non-scaling-stroke" />
          ))}
          <path ref={area} fill={`url(#${uid}-fill)`} />
          <path ref={line} fill="none" stroke="var(--g-accent)" strokeWidth="1.5" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
        </svg>
        {format && <span ref={ceiling} className="pointer-events-none absolute left-0 top-0 text-2xs tabular-nums text-fg-subtle" aria-hidden />}
      </div>
    </div>
  );
}
