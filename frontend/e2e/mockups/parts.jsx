// Shared pieces of the mockup cards (scratch, see docs/design-download-card.md).
import { useCallback, useEffect, useMemo } from "react";
import { Pause, Play, X } from "lucide-react";
import { actions } from "../../src/state/actions";
import { useWindowVisible } from "../../src/state/hooks";
import { loadHistory, readChart, subscribeHistory, useJobProgressThrottled } from "../../src/state/progress";
import { useGame } from "../../src/state/library";
import { useApp, useSnap } from "../../src/state/store";
import { usePending, useUi } from "../../src/state/ui";
import { Button, IconButton, ThroughputChart, cx, niceCeil } from "../../src/ui";
import { formatClock, formatSpeed } from "../../src/state/format";
import { PAUSABLE_KINDS } from "../../src/features/downloads/kindIcon";
import { viewOf } from "./model";

export function useCard(job) {
  const visible = useWindowVisible();
  const focused = useApp((s) => s.window.focused);
  const p = useJobProgressThrottled(job.id, job.progress, visible);
  const speedLimit = useSnap((s) => s.settings.speedLimitBytesPerSecond);
  const chosenMode = useSnap((s) => s.settings.downloadMode);
  const effectiveMode = useSnap((s) => s.queue.effectiveMode);
  const modeReason = useSnap((s) => s.queue.modeReason);
  const q = new URLSearchParams(location.search);
  const holdSubject = /while (.+?) is running/i.exec(modeReason ?? "")?.[1] ?? q.get("game") ?? "Fortnite";
  const ctx = { speedLimitBytesPerSecond: speedLimit, path: job.targetPath, chosenMode, effectiveMode, modeReason, hold: q.get("hold"), holdSubject };
  const v = viewOf(job, p, ctx);
  return { p, ctx, v, visible, focused };
}

/** "1 h 12 min left": numbers carry the weight, units and "left" stay quiet. A phrase ("Under a minute") is set whole. */
export function TimeLead({ text, size = "lg", tone = "fg", className, suffix = " left" }) {
  const big = size === "lg" ? "text-2xl" : "text-xl";
  const small = size === "lg" ? "text-lg" : "text-md";
  const parts = text.split(/(\d+)/).filter(Boolean);
  const numeric = /\d/.test(text);
  const strong = tone === "white" ? "text-white" : "text-fg";
  const quiet = tone === "white" ? "text-white/70" : "text-fg-subtle";
  if (!numeric)
    return (
      <p className={cx("font-semibold leading-none tracking-tight", size === "lg" ? "text-xl" : "text-lg", strong, className)}>
        {text}
        {suffix && <span className={cx("font-medium", quiet)}>{suffix}</span>}
      </p>
    );
  return (
    <p className={cx("whitespace-nowrap leading-none tabular-nums", className)}>
      {parts.map((s, i) =>
        /^\d+$/.test(s) ? (
          <span key={i} className={cx(big, "font-semibold tracking-tighter", strong)}>
            {s}
          </span>
        ) : (
          <span key={i} className={cx(small, "font-medium", quiet)}>
            {s}
          </span>
        ),
      )}
      {suffix && <span className={cx(small, "font-medium", quiet)}>{suffix}</span>}
    </p>
  );
}

export function StateLead({ text, size = "lg", tone = "fg", className }) {
  return <p className={cx("font-semibold leading-none tracking-tight", size === "lg" ? "text-xl" : "text-lg", tone === "white" ? "text-white" : tone === "warning" ? "text-warning-text" : "text-fg", className)}>{text}</p>;
}

/** The game's tall cover at card size. Decorative: the title sits next to it. */
export function Cover({ job, className }) {
  const game = useGame(job.appName);
  const url = game?.art.tall ?? game?.art.tallThumb ?? null;
  return (
    <span
      aria-hidden
      className={cx("relative block shrink-0 overflow-hidden rounded-md border border-border-strong bg-surface-2 bg-cover bg-center [box-shadow:inset_0_1px_0_var(--g-highlight)]", className)}
      style={{ backgroundColor: game?.art.placeholder ?? undefined, backgroundImage: url ? `url("${url}")` : undefined }}
    />
  );
}

export function useWideArt(job) {
  const game = useGame(job.appName);
  return { wide: game?.art.wide ?? null, tall: game?.art.tall ?? null, placeholder: game?.art.placeholder ?? null };
}

/** `glass`: over key art. A ghost button on its own dark fill (inline, so no Button variant's fill competes with it). */
export function PauseButton({ job, size = "md", className, glass = false }) {
  const busy = usePending(`job:${job.id}`);
  const paused = job.status === "paused";
  if (!(job.status === "running" || paused) || !PAUSABLE_KINDS.includes(job.kind)) return null;
  const Icon = paused ? Play : Pause;
  return (
    <Button
      size={size}
      variant={paused ? "primary" : glass ? "ghost" : "secondary"}
      loading={busy}
      className={cx(glass && !paused && "border border-white/20 text-white backdrop-blur-md hover:text-white", className)}
      style={glass && !paused ? { backgroundColor: "oklch(0.15 0.006 265 / 0.72)" } : undefined}
      icon={<Icon size={size === "sm" ? 12 : 14} fill="currentColor" strokeWidth={0} />}
      onClick={() => void (paused ? actions.resumeJob(job.id) : actions.pauseJob(job.id))}
    >
      {paused ? "Resume" : "Pause"}
    </Button>
  );
}

export function CancelButton({ job, className, variant = "ghost" }) {
  const openDialog = useUi((s) => s.openDialog);
  if (!job.cancellable) return null;
  return (
    <IconButton label="Cancel download" variant={variant} className={className} onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
      <X size={15} />
    </IconButton>
  );
}

const NET = { key: "network", label: "Download", color: "var(--g-chart-net)", area: true };
const DISK = { key: "disk", label: "Disk write", color: "var(--g-chart-disk)", area: true };

/**
 * The download's speed over its lifetime, drawn against the connection's ceiling (dashed), so a drive-bound line sits low
 * instead of filling the plot like every auto-scaled chart does. NEW (UI): `ceiling` prop on ThroughputChart; here an overlay.
 */
export function SpeedChart({ job, active, ceiling, kind = "network", className, plotClassName = "h-16", ceilingLabel }) {
  useEffect(() => {
    void loadHistory(job.id);
  }, [job.id]);
  const withNetwork = kind === "network";
  const subscribe = useCallback((n) => subscribeHistory(job.id, n), [job.id]);
  const read = useCallback(() => {
    // One series: the download while downloading, the drive's writes otherwise.
    const d = readChart(job.id, withNetwork);
    return { t: d.t, values: [d.values[0]] };
  }, [job.id, withNetwork]);
  const y = useMemo(() => {
    if (!ceiling) return null;
    const d = read();
    let peak = ceiling;
    for (const col of d.values) for (let i = 0; i < d.t.length; i++) if (col[i] > peak) peak = col[i];
    const yMax = niceCeil(peak * 1.3);
    return 99 - (ceiling / yMax) * 98;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ceiling, read, job.status]);
  return (
    <div className={cx("relative", className)}>
      <ThroughputChart
        series={[withNetwork ? NET : DISK]}
        read={read}
        subscribe={subscribe}
        active={active}
        format={formatSpeed}
        formatTime={formatClock}
        markers={() => null}
        legend={false}
        peakLabel={false}
        averageLabel="never"
        minSpan={20}
        plotClassName={plotClassName}
        label="Download speed over time"
      />
      {y != null && (
        <div aria-hidden className="pointer-events-none absolute inset-x-0 top-0" style={{ height: "100%" }}>
          <div className="absolute inset-x-0 border-t border-dashed border-fg-subtle/70" style={{ top: `${y}%` }} />
          <span className="absolute right-0 -translate-y-[calc(100%+3px)] rounded-sm text-2xs tabular-nums text-fg-muted" style={{ top: `${y}%` }}>
            {ceilingLabel}
          </span>
        </div>
      )}
    </div>
  );
}
