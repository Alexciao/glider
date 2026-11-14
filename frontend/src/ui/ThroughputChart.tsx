import { useEffect, useId, useRef } from "react";
import { cx } from "./cx";

/**
 * A live, low-cost throughput chart: one smooth area series and any number of line series over a growing time axis.
 *
 * Cost model (docs: the active download card must be near-free while it runs):
 *  - React renders this once. Data arrives through `subscribe`; each notification (≈1 Hz) rewrites a handful of SVG
 *    `d` attributes and label strings straight on the DOM. No state, no per-frame JS.
 *  - Between samples the chart does not redraw at all: the newest segment is revealed by one Web Animations transform
 *    + clip-path animation that the compositor runs (a linear 1 s glide), so 1 Hz data reads as continuous motion.
 *  - While `active` is false (window hidden, tab in the background) notifications are ignored and nothing animates;
 *    the next activation repaints once.
 */

export interface ChartSeries {
  key: string;
  /** Legend and tooltip name. */
  label: string;
  /** Any CSS colour, normally a token: `var(--g-chart-net)`. */
  color: string;
  /** Fill the area under this series (use on the first series only; a second wash muddies both). */
  area?: boolean;
}

/** Column-oriented samples: `t` in seconds (ascending), one value column per series, same length. */
export interface ChartData {
  t: ArrayLike<number>;
  values: readonly ArrayLike<number>[];
}

export interface ThroughputChartProps {
  series: readonly ChartSeries[];
  read: () => ChartData;
  subscribe: (notify: () => void) => () => void;
  /** False pauses all drawing and animation (hidden window). Default true. */
  active?: boolean;
  /** Axis and tooltip values, e.g. bytes/s → "60 MB/s". */
  format: (v: number) => string;
  /** Marker values for the first series, from the source of truth (so they match the stat rows). */
  markers?: () => { average: number | null; peak: number | null } | null;
  /** Elapsed seconds → "2:41". */
  formatTime: (seconds: number) => string;
  /** Accessible description; a live summary is appended. */
  label: string;
  /** Shortest x-axis span in seconds: a young job draws left to right instead of stretching. Default 60. */
  minSpan?: number;
  className?: string;
  /** Height of the plot area as a Tailwind class. */
  plotClassName?: string;
  emptyText?: string;
}

const VW = 1000;
const VH = 100;
const PAD_TOP = 1;
const PAD_BOTTOM = 1;
const NICE = [1, 1.5, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 40, 50, 60, 80, 100, 150, 200, 300, 400, 500, 600, 800, 1000, 1024];

/** The smallest round axis maximum (1024-based, 1/1.5/2/3/4/5/6/8 × 10ⁿ) at or above `v`. */
export function niceCeil(v: number): number {
  if (!Number.isFinite(v) || v <= 0) return 1;
  const u = Math.max(0, Math.floor(Math.log(v) / Math.log(1024)));
  const unit = 1024 ** u;
  const x = v / unit;
  const pick = NICE.find((n) => n >= x) ?? 1024;
  return pick * unit;
}

/** Index of the sample closest to `t` (samples ascending). */
export function nearestIndex(ts: ArrayLike<number>, t: number): number {
  const n = ts.length;
  if (n === 0) return -1;
  let lo = 0;
  let hi = n - 1;
  while (hi - lo > 1) {
    const mid = (lo + hi) >> 1;
    if (ts[mid]! <= t) lo = mid;
    else hi = mid;
  }
  return Math.abs(ts[lo]! - t) <= Math.abs(ts[hi]! - t) ? lo : hi;
}

const f1 = (n: number): string => (Math.round(n * 10) / 10).toString();

/**
 * A smooth, monotone cubic path through the points (Fritsch–Carlson): it never overshoots a sample, so a speed curve
 * cannot dip below zero or invent a peak. Straight lines for fewer than 3 points.
 */
export function monotonePath(xs: ArrayLike<number>, ys: ArrayLike<number>): string {
  const n = xs.length;
  if (n === 0) return "";
  if (n === 1) return `M${f1(xs[0]!)} ${f1(ys[0]!)}`;
  const d = new Float64Array(n - 1);
  const m = new Float64Array(n);
  for (let i = 0; i < n - 1; i++) d[i] = (ys[i + 1]! - ys[i]!) / Math.max(1e-9, xs[i + 1]! - xs[i]!);
  m[0] = d[0]!;
  m[n - 1] = d[n - 2]!;
  for (let i = 1; i < n - 1; i++) m[i] = d[i - 1]! * d[i]! <= 0 ? 0 : (d[i - 1]! + d[i]!) / 2;
  for (let i = 0; i < n - 1; i++) {
    if (d[i] === 0) {
      m[i] = 0;
      m[i + 1] = 0;
      continue;
    }
    const a = m[i]! / d[i]!;
    const b = m[i + 1]! / d[i]!;
    const s = a * a + b * b;
    if (s > 9) {
      const t = 3 / Math.sqrt(s);
      m[i] = t * a * d[i]!;
      m[i + 1] = t * b * d[i]!;
    }
  }
  let out = `M${f1(xs[0]!)} ${f1(ys[0]!)}`;
  for (let i = 0; i < n - 1; i++) {
    const h = (xs[i + 1]! - xs[i]!) / 3;
    out += `C${f1(xs[i]! + h)} ${f1(ys[i]! + m[i]! * h)} ${f1(xs[i + 1]! - h)} ${f1(ys[i + 1]! - m[i + 1]! * h)} ${f1(xs[i + 1]!)} ${f1(ys[i + 1]!)}`;
  }
  return out;
}

const yOf = (v: number, yMax: number): number => VH - PAD_BOTTOM - Math.min(1, Math.max(0, v / yMax)) * (VH - PAD_TOP - PAD_BOTTOM);

interface Frame {
  t0: number;
  tLast: number;
  span: number;
  n: number;
}

export function ThroughputChart({
  series,
  read,
  subscribe,
  active = true,
  format,
  markers,
  formatTime,
  label,
  minSpan = 60,
  className,
  plotClassName = "h-24",
  emptyText = "Collecting speed data…",
}: ThroughputChartProps) {
  const uid = useId().replace(/:/g, "");
  const plot = useRef<HTMLDivElement>(null);
  const layer = useRef<HTMLDivElement>(null);
  const paths = useRef<(SVGPathElement | null)[]>([]);
  const areaPath = useRef<SVGPathElement>(null);
  const empty = useRef<HTMLSpanElement>(null);
  const avgLine = useRef<HTMLDivElement>(null);
  const avgLabel = useRef<HTMLSpanElement>(null);
  const peakDot = useRef<HTMLSpanElement>(null);
  const peakLabel = useRef<HTMLSpanElement>(null);
  const cross = useRef<HTMLDivElement>(null);
  const tip = useRef<HTMLDivElement>(null);
  const tipTime = useRef<HTMLSpanElement>(null);
  const tipVals = useRef<(HTMLSpanElement | null)[]>([]);
  const live = useRef<HTMLSpanElement>(null);

  // Props the draw loop needs, kept in a ref so the subscription never re-binds.
  const props = useRef({ series, read, format, markers, formatTime, label, minSpan });
  useEffect(() => {
    props.current = { series, read, format, markers, formatTime, label, minSpan };
  });

  useEffect(() => {
    if (!active) return;
    let frame: Frame | null = null;
    let data: ChartData | null = null;
    let yMax = 1;
    let hover = -1;
    let animation: Animation | null = null;
    const reduced = typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    const showHover = (idx: number) => {
      hover = idx;
      if (!data || !frame || idx < 0 || !cross.current || !tip.current) {
        if (cross.current) cross.current.style.opacity = "0";
        if (tip.current) tip.current.style.opacity = "0";
        return;
      }
      const t = data.t[idx]!;
      const x = ((t - frame.t0) / frame.span) * 100;
      cross.current.style.opacity = "1";
      cross.current.style.left = `${x}%`;
      tip.current.style.opacity = "1";
      tip.current.style.left = `${x}%`;
      tip.current.style.transform = x > 55 ? "translateX(calc(-100% - 10px))" : "translateX(10px)";
      if (tipTime.current) tipTime.current.textContent = props.current.formatTime(t - frame.t0);
      props.current.series.forEach((_, i) => {
        const el = tipVals.current[i];
        if (el) el.textContent = props.current.format(data!.values[i]?.[idx] ?? 0);
      });
      if (live.current) live.current.textContent = `${props.current.formatTime(t - frame.t0)}: ${props.current.series.map((s, i) => `${s.label} ${props.current.format(data!.values[i]?.[idx] ?? 0)}`).join(", ")}`;
    };

    const draw = (animate: boolean) => {
      const { series: ss, minSpan: ms } = props.current;
      const d = props.current.read();
      data = d;
      const n = d.t.length;
      const prev = frame;
      if (empty.current) empty.current.hidden = n >= 2;
      if (n < 2) {
        frame = null;
        areaPath.current?.setAttribute("d", "");
        paths.current.forEach((p) => p?.setAttribute("d", ""));
        for (const el of [avgLine.current, avgLabel.current, peakDot.current, peakLabel.current]) if (el) el.style.opacity = "0";
        return;
      }
      const t0 = d.t[0]!;
      const tLast = d.t[n - 1]!;
      const span = Math.max(ms, tLast - t0);
      frame = { t0, tLast, span, n };

      const mk = props.current.markers?.() ?? null;
      let peak = Math.max(mk?.peak ?? 0, mk?.average ?? 0);
      for (const col of d.values) for (let i = 0; i < n; i++) if (col[i]! > peak) peak = col[i]!;
      // Headroom for the peak label.
      yMax = niceCeil(peak * 1.3);

      const xs = new Float64Array(n);
      for (let i = 0; i < n; i++) xs[i] = ((d.t[i]! - t0) / span) * VW;
      const ys = new Float64Array(n);
      ss.forEach((s, si) => {
        const col = d.values[si];
        const el = paths.current[si];
        if (!col || !el) return;
        for (let i = 0; i < n; i++) ys[i] = yOf(col[i]!, yMax);
        const dPath = monotonePath(xs, ys);
        el.setAttribute("d", dPath);
        if (s.area && areaPath.current) areaPath.current.setAttribute("d", `${dPath}L${f1(xs[n - 1]!)} ${VH}L${f1(xs[0]!)} ${VH}Z`);
      });

      // Average and peak markers (first series).
      const first = d.values[0];
      const lineY = (v: number) => yOf(v, yMax);
      let avgY = -1;
      if (mk?.average && mk.average > 0 && avgLine.current && avgLabel.current) {
        avgY = lineY(mk.average);
        avgLine.current.style.opacity = "1";
        avgLine.current.style.top = `${avgY}%`;
        avgLabel.current.style.top = `${avgY}%`;
        avgLabel.current.textContent = `Average ${props.current.format(mk.average)}`;
      } else if (avgLine.current && avgLabel.current) {
        avgLine.current.style.opacity = "0";
        avgLabel.current.style.opacity = "0";
      }
      let peakY = -1;
      let peakX = 0;
      if (first && mk?.peak && mk.peak > 0 && peakDot.current && peakLabel.current) {
        let pi = 0;
        for (let i = 1; i < n; i++) if (first[i]! > first[pi]!) pi = i;
        peakX = xs[pi]! / 10;
        peakY = lineY(first[pi]!);
        peakDot.current.style.opacity = "1";
        peakDot.current.style.left = `${peakX}%`;
        peakDot.current.style.top = `${peakY}%`;
        peakLabel.current.style.opacity = "1";
        peakLabel.current.style.top = `${peakY}%`;
        peakLabel.current.style.left = `${peakX}%`;
        peakLabel.current.style.transform = `translate(${peakX > 78 ? "calc(-100% + 4px)" : peakX < 14 ? "-4px" : "-50%"}, calc(-100% - 8px))`;
        peakLabel.current.textContent = `Peak ${props.current.format(mk.peak)}`;
      } else if (peakDot.current && peakLabel.current) {
        peakDot.current.style.opacity = "0";
        peakLabel.current.style.opacity = "0";
      }
      // Two labels close together would collide; the dashed line still marks the average.
      if (avgLabel.current) {
        const clash = avgY >= 0 && peakY >= 0 && Math.abs(avgY - peakY) < 22 && peakX > 55;
        avgLabel.current.style.opacity = avgY >= 0 && !clash ? "1" : "0";
      }

      plot.current?.setAttribute("aria-label", `${props.current.label}. ${ss.map((s, i) => `${s.label} now ${props.current.format(d.values[i]?.[n - 1] ?? 0)}`).join(", ")}.`);

      // Glide: reveal only the newest segment, on the compositor.
      if (animation) {
        animation.cancel();
        animation = null;
      }
      if (animate && !reduced && prev && prev.t0 === t0 && n === prev.n + 1 && layer.current && typeof layer.current.animate === "function") {
        const k = span / prev.span;
        const oldX = (prev.tLast - t0) / span;
        const dt = Math.min(2, Math.max(0.25, tLast - prev.tLast));
        animation = layer.current.animate(
          [
            { transform: `scaleX(${k})`, clipPath: `inset(0 ${(1 - oldX) * 100}% 0 0)` },
            { transform: "scaleX(1)", clipPath: "inset(0 0% 0 0)" },
          ],
          { duration: dt * 1000, easing: "linear" },
        );
      }
      if (hover >= 0) showHover(Math.min(hover, n - 1));
    };
    draw(false);
    const off = subscribe(() => draw(true));

    // Hover and keyboard readout share one path.
    const el = plot.current;
    const at = (clientX: number) => {
      if (!el || !data || !frame) return -1;
      const r = el.getBoundingClientRect();
      const f = Math.min(1, Math.max(0, (clientX - r.left) / Math.max(1, r.width)));
      return nearestIndex(data.t, frame.t0 + f * frame.span);
    };
    const onMove = (e: PointerEvent) => showHover(at(e.clientX));
    const onLeave = () => {
      if (document.activeElement !== el) showHover(-1);
    };
    const onKey = (e: KeyboardEvent) => {
      if (!data) return;
      const last = data.t.length - 1;
      let next = hover < 0 ? last : hover;
      if (e.key === "ArrowLeft") next = Math.max(0, next - 1);
      else if (e.key === "ArrowRight") next = Math.min(last, next + 1);
      else if (e.key === "Home") next = 0;
      else if (e.key === "End") next = last;
      else return;
      e.preventDefault();
      showHover(next);
    };
    const onFocus = () => data && showHover(data.t.length - 1);
    const onBlur = () => showHover(-1);
    el?.addEventListener("pointermove", onMove);
    el?.addEventListener("pointerleave", onLeave);
    el?.addEventListener("keydown", onKey);
    el?.addEventListener("focus", onFocus);
    el?.addEventListener("blur", onBlur);
    return () => {
      off();
      animation?.cancel();
      el?.removeEventListener("pointermove", onMove);
      el?.removeEventListener("pointerleave", onLeave);
      el?.removeEventListener("keydown", onKey);
      el?.removeEventListener("focus", onFocus);
      el?.removeEventListener("blur", onBlur);
    };
  }, [active, subscribe, series.length]);

  return (
    <div className={cx("flex min-w-0 flex-col", className)}>
      <ul className="mb-3 flex items-center gap-4 text-xs text-fg-muted" aria-label="Series">
        {series.map((s) => (
          <li key={s.key} className="inline-flex items-center gap-1.5">
            <span aria-hidden className="h-0.5 w-3.5 rounded-full" style={{ background: s.color }} />
            {s.label}
          </li>
        ))}
      </ul>
      <div className={cx("flex", plotClassName)}>
      <div
        ref={plot}
        tabIndex={0}
        role="group"
        aria-roledescription="chart"
        aria-label={label}
        className={cx(
          "relative flex-1 cursor-crosshair select-none rounded-sm outline-none focus-visible:ring-2 focus-visible:ring-focus focus-visible:ring-offset-2 focus-visible:ring-offset-bg",
        )}
      >
        {/* One baseline. The average line and the peak label carry the scale; no other gridlines or axis text. */}
        <div aria-hidden className="pointer-events-none absolute inset-x-0 bottom-0 border-t border-border-strong" />

        <div ref={layer} className="pointer-events-none absolute inset-0 origin-left will-change-transform" aria-hidden>
          <svg viewBox={`0 0 ${VW} ${VH}`} preserveAspectRatio="none" className="size-full overflow-visible">
            <defs>
              <linearGradient id={`${uid}-a`} x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor={series[0]?.color} stopOpacity="0.26" />
                <stop offset="100%" stopColor={series[0]?.color} stopOpacity="0.02" />
              </linearGradient>
            </defs>
            <path ref={areaPath} fill={`url(#${uid}-a)`} />
            {[...series].reverse().map((s) => {
              const i = series.indexOf(s);
              return (
                <path
                  key={s.key}
                  ref={(el) => {
                    paths.current[i] = el;
                  }}
                  fill="none"
                  stroke={s.color}
                  strokeWidth="2"
                  strokeLinejoin="round"
                  strokeLinecap="round"
                  vectorEffect="non-scaling-stroke"
                />
              );
            })}
          </svg>
        </div>


        <div ref={avgLine} aria-hidden className="pointer-events-none absolute inset-x-0 border-t border-dashed border-fg-subtle opacity-0 transition-opacity duration-200" />
        <span
          ref={avgLabel}
          aria-hidden
          className="pointer-events-none absolute right-0 -translate-y-full rounded-sm bg-bg-elevated/80 px-1 text-2xs tabular-nums text-fg-muted opacity-0 transition-opacity duration-200"
        />
        <span
          ref={peakDot}
          aria-hidden
          className="pointer-events-none absolute size-2 -translate-x-1/2 -translate-y-1/2 rounded-full opacity-0 ring-2 ring-[var(--g-bg-elevated)]"
          style={{ background: series[0]?.color }}
        />
        <span ref={peakLabel} aria-hidden className="pointer-events-none absolute whitespace-nowrap rounded-sm bg-bg-elevated/80 px-1 text-2xs tabular-nums text-fg-muted opacity-0 transition-opacity duration-200" />

        <div ref={cross} aria-hidden className="pointer-events-none absolute inset-y-0 w-px -translate-x-1/2 bg-fg-subtle opacity-0" />
        <div
          ref={tip}
          aria-hidden
          className="pointer-events-none absolute top-1 z-10 min-w-36 rounded-md border border-border-strong bg-popover px-2.5 py-2 text-xs opacity-0 shadow-md"
        >
          <span ref={tipTime} className="mb-1 block tabular-nums text-fg-muted" />
          {series.map((s, i) => (
            <span key={s.key} className="flex items-center justify-between gap-4">
              <span className="inline-flex items-center gap-1.5 text-fg-muted">
                <span className="h-0.5 w-3 rounded-full" style={{ background: s.color }} />
                {s.label}
              </span>
              <span
                ref={(el) => {
                  tipVals.current[i] = el;
                }}
                className="font-medium tabular-nums text-fg"
              />
            </span>
          ))}
        </div>

        <span ref={empty} className="pointer-events-none absolute inset-0 grid place-items-center text-xs text-fg-subtle">
          {emptyText}
        </span>
        <span ref={live} className="sr-only" aria-live="polite" />
      </div>
      </div>
    </div>
  );
}
