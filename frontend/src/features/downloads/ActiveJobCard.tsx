import { useCallback, useEffect } from "react";
import { Pause, Play, X } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { formatClock, formatEta, formatSpeed, formatSpeedCompact } from "../../state/format";
import { useWindowVisible } from "../../state/hooks";
import { historyHasNetwork, loadHistory, readChart, readHistory, subscribeHistory, useJobProgressThrottled, useProgress } from "../../state/progress";
import { goGame } from "../../state/router";
import { useSnap } from "../../state/store";
import { PHASE_STATE, describeLimiter, figuresOf, metaOf, stateOf } from "../../state/transfer";
import { usePending, useUi } from "../../state/ui";
import { Button, MiniSparkline, PhaseStepper, ProgressBar, StatGroups, StatusDot, Surface, ThroughputChart, cx, type ChartSeries } from "../../ui";
import { PathText } from "../common/PathText";
import { JobArt } from "./JobArt";
import { PAUSABLE_KINDS } from "./kindIcon";

export { KIND_ICON } from "./kindIcon";

/** Beside the chart the groups stack in two balanced columns. */
const STAT_COLUMNS = [["progress", "disk"], ["speed", "time"]] as const;

const NET: ChartSeries = { key: "network", label: "Network", color: "var(--g-chart-net)", area: true };
const DISK: ChartSeries = { key: "disk", label: "Disk write", color: "var(--g-chart-disk)", area: true };
const DISK_LINE: ChartSeries = { ...DISK, area: false };

/** Announces progress to assistive tech at 10% steps and phase changes, not at 10 Hz. */
function ProgressAnnouncer({ job }: { job: JobInfo }) {
  const bucket = useProgress((s) => Math.floor((s.latest[job.id]?.percent ?? job.progress?.percent ?? 0) / 10) * 10);
  const phase = useProgress((s) => s.latest[job.id]?.phase ?? job.progress?.phase);
  return (
    <span className="sr-only" role="status" aria-live="polite">
      {job.status === "running" ? `${phase ? PHASE_STATE[phase] : "Working on"} ${job.title}, ${bucket} percent.` : ""}
    </span>
  );
}

/** Speed history of one job over its lifetime, seeded from the backend and extended by live events. */
function JobChart({ job, withNetwork, active }: { job: JobInfo; withNetwork: boolean; active: boolean }) {
  useEffect(() => {
    void loadHistory(job.id);
  }, [job.id]);
  const subscribe = useCallback((n: () => void) => subscribeHistory(job.id, n), [job.id]);
  const read = useCallback(() => readChart(job.id, withNetwork), [job.id, withNetwork]);
  const markers = useCallback(() => {
    const p = useProgress.getState().latest[job.id];
    const s = withNetwork ? p?.network : p?.disk;
    return s ? { average: s.averageBytesPerSecond, peak: s.peakBytesPerSecond } : null;
  }, [job.id, withNetwork]);
  return (
    <ThroughputChart
      key={withNetwork ? "net" : "disk"}
      series={withNetwork ? [NET, DISK_LINE] : [DISK]}
      read={read}
      subscribe={subscribe}
      active={active}
      format={formatSpeedCompact}
      formatTime={formatClock}
      markers={markers}
      label={withNetwork ? "Network and disk speed over time" : "Disk speed over time"}
    />
  );
}

function PauseResume({ job, size = "md" }: { job: JobInfo; size?: "sm" | "md" }) {
  const busy = usePending(`job:${job.id}`);
  const paused = job.status === "paused";
  if (!(job.status === "running" || paused) || !PAUSABLE_KINDS.includes(job.kind)) return null;
  const Icon = paused ? Play : Pause;
  return (
    <Button
      size={size}
      variant={paused ? "primary" : "secondary"}
      loading={busy}
      icon={<Icon size={size === "sm" ? 12 : 13} fill="currentColor" strokeWidth={0} />}
      onClick={() => void (paused ? actions.resumeJob(job.id) : actions.pauseJob(job.id))}
    >
      {paused ? "Resume" : "Pause"}
    </Button>
  );
}

/** "2.41 GB of 5.12 GB": the numbers lead, the rest follows. */
function Headline({ text }: { text: string }) {
  const at = text.indexOf(" of ");
  if (at < 0) return <>{text}</>;
  return (
    <>
      <span className="text-fg">{text.slice(0, at)}</span>
      {text.slice(at)}
    </>
  );
}

export interface ActiveJobCardProps {
  job: JobInfo;
  /** `compact` is the Home row: art, title, state, one bar and a single line of figures. */
  variant?: "full" | "compact";
  className?: string;
}

/**
 * The running job. One title (the game), one state, a stepper of the planned phases, one progress bar with a single
 * headline line, then the figures that belong to the phase it is in (only the ones with a value), the speed history
 * while bytes are moving, and, when it is not the network, what limits speed. All of it comes from `figuresOf`, so a
 * verify can never show a download speed.
 */
export function ActiveJobCard({ job, variant = "full", className }: ActiveJobCardProps) {
  const visible = useWindowVisible();
  // The figures are read, not watched: 2 Hz, and not at all while the window is hidden. The chart and bar feed on their own.
  const p = useJobProgressThrottled(job.id, job.progress, visible);
  const openDialog = useUi((s) => s.openDialog);
  const speedLimit = useSnap((s) => s.settings.speedLimitBytesPerSecond);
  const state = stateOf(job, p);
  const meta = metaOf(job, p);
  const fig = figuresOf(job, p);
  const running = job.status === "running";
  const paused = job.status === "paused";
  const waiting = job.status === "waiting";
  const percent = Math.floor(Math.min(100, Math.max(0, p?.percent ?? 0)));
  const phase = p?.phase ?? job.phase;
  const withNetwork = fig.chart === "network+disk" || (fig.chart === "disk" && historyHasNetwork(job.id));
  const showChart = fig.chart !== null && p !== null;
  const net = p?.network ?? null;
  const live = running ? (net?.bytesPerSecond ?? p?.disk?.bytesPerSecond ?? p?.verify?.readBytesPerSecond ?? null) : null;
  const limiter = running ? describeLimiter(p, fig.throughput, { speedLimitBytesPerSecond: speedLimit, path: job.targetPath }) : null;
  const left = running && p && p.etaSeconds != null ? `${formatEta(p.etaSeconds)} left` : null;
  const tone = state.tone === "accent" ? "accent" : state.tone;
  // A job that has not started (waiting, queued) has no steps, percent or bar to show: just why it is not running.
  const started = p !== null;
  const reason = (job.blockedReason ?? "Another task is using the game files").replace(/[.\s]+$/, "");
  const blocked = /continue/i.test(reason) ? `${reason}.` : `${reason}. Glider continues automatically.`;

  if (variant === "compact") {
    return (
      <Surface tone="raised" className={cx("p-4", className)}>
        <ProgressAnnouncer job={job} />
        <div className="flex items-center gap-3.5">
          <JobArt job={job} size="md" />
          <div className="min-w-0 flex-1">
            <div className="flex items-center justify-between gap-3">
              <h2 className="min-w-0 truncate text-base font-semibold tracking-tight text-fg">
                <button type="button" className="rounded-sm text-left hover:underline" onClick={() => job.appName !== "eos-overlay" && goGame(job.appName)}>
                  {job.title}
                </button>
              </h2>
              <PauseResume job={job} size="sm" />
            </div>
            <p className="mt-1 flex min-w-0 items-center gap-2 text-xs text-fg-muted">
              <StatusDot tone={tone} />
              <span className="text-fg">{state.label}</span>
              {meta && <span className="truncate">{meta}</span>}
            </p>
          </div>
        </div>
        <ProgressBar
          className="mt-3.5"
          size="md"
          value={waiting && !p ? 0 : percent}
          tone={paused || waiting ? "warning" : "accent"}
          paused={paused}
          speed={live}
          label={`${state.label} ${job.title}`}
        />
        <p className="mt-2 truncate text-xs tabular-nums text-fg-muted">
          {running && live != null && <span className="text-fg">{formatSpeed(live)} · </span>}
          {fig.headline && <>{fig.headline} · </>}
          {percent}%{left && ` · ${left}`}
        </p>
      </Surface>
    );
  }

  return (
    <Surface tone="raised" className={cx("@container overflow-hidden", className)}>
      <ProgressAnnouncer job={job} />

      <header className="flex items-start gap-4 p-5 pb-0">
        <JobArt job={job} size="lg" />
        <div className="min-w-0 flex-1 pt-0.5">
          <h2 className="truncate text-md font-semibold tracking-tight text-fg" title={job.title}>
            {job.title}
          </h2>
          <p className="mt-1.5 flex min-w-0 items-center gap-2 text-sm text-fg-muted">
            <StatusDot tone={tone} />
            <span className="shrink-0 font-medium text-fg">{state.label}</span>
            {meta && <span className="truncate">{meta}</span>}
          </p>
          {job.kind === "move" && job.targetPath && (
            <p className="mt-1 flex min-w-0 gap-1 text-sm text-fg-muted">
              to <PathText path={job.targetPath} className="text-fg" />
            </p>
          )}
          {waiting && <p className="mt-1.5 text-sm text-fg-muted">{blocked}</p>}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          <PauseResume job={job} />
          {job.cancellable && (
            <Button variant="ghost" icon={<X size={14} />} onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
              Cancel
            </Button>
          )}
        </div>
      </header>

      {started && (
        <div className="px-5 pt-5">
          <PhaseStepper steps={job.phases.map((id) => ({ id, label: PHASE_STATE[id] }))} current={phase} paused={paused} />
        </div>
      )}

      {started && (
      <div className="px-5 pt-5">
        <div className="flex items-end justify-between gap-4">
          <p className="text-2xl font-semibold leading-none tracking-tighter tabular-nums text-fg" aria-hidden>
            {percent}
            <span className="ml-0.5 text-lg font-medium text-fg-subtle">%</span>
          </p>
          <p className="pb-0.5 text-sm tabular-nums text-fg-muted">
            {fig.headline && p && <Headline text={fig.headline} />}
            {p && !fig.headline && `${formatClock(p.elapsedSeconds)} elapsed`}
            {left && <>{fig.headline || p ? " · " : ""}{left}</>}
          </p>
        </div>
        <ProgressBar
          className="mt-3"
          size="lg"
          value={percent}
          tone={paused || waiting ? "warning" : "accent"}
          paused={paused}
          speed={live}
          label={`${state.label} ${job.title}`}
        />
      </div>
      )}

      {p && fig.groups.length > 0 && (
        <div className={cx("grid gap-x-12 gap-y-6 px-5 pt-6", showChart && "@[900px]:grid-cols-[minmax(0,1.25fr)_minmax(0,1fr)]")}>
          {showChart && <JobChart job={job} withNetwork={withNetwork} active={visible} />}
          {showChart ? (
            <StatGroups groups={fig.groups} columns={STAT_COLUMNS} className="content-start" />
          ) : (
            <StatGroups groups={fig.groups} className="content-start" />
          )}
        </div>
      )}

      {limiter && <p className={cx("px-5 pt-5 text-xs", limiter.tone === "warning" ? "text-warning-text" : "text-fg-muted")}>{limiter.text}</p>}
      <div className="h-5" />
    </Surface>
  );
}

/** A compact speed trend of one job, for rows. Draws nothing until it has two samples. */
export function JobSparkline({ jobId, active = true, label }: { jobId: string; active?: boolean; label?: string }) {
  const subscribe = useCallback((n: () => void) => subscribeHistory(jobId, n), [jobId]);
  const read = useCallback(() => readHistory(jobId, historyHasNetwork(jobId) ? "network" : "disk"), [jobId]);
  useEffect(() => {
    void loadHistory(jobId);
  }, [jobId]);
  return <MiniSparkline read={read} subscribe={subscribe} active={active} label={label} />;
}
