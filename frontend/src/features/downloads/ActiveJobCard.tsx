import { useCallback, useEffect } from "react";
import { Pause, Play, X } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { formatClock, formatSpeed } from "../../state/format";
import { useWindowVisible } from "../../state/hooks";
import { historyHasNetwork, loadHistory, readChart, readHistory, subscribeHistory, useJobProgressThrottled, useProgress } from "../../state/progress";
import { goGame } from "../../state/router";
import { useApp, useSnap } from "../../state/store";
import { PHASE_STATE, figuresOf, headlineOf, metaOf, speedStatusOf, stateOf, timeOf, type Readout } from "../../state/transfer";
import { usePending, useUi } from "../../state/ui";
import { Button, MiniSparkline, PhaseStepper, ProgressBar, StatusDot, Surface, ThroughputChart, Tooltip, cx, type ChartSeries } from "../../ui";
import { PathText } from "../common/PathText";
import { JobArt } from "./JobArt";
import { SpeedStatus } from "./SpeedStatus";
import { PAUSABLE_KINDS } from "./kindIcon";

export { KIND_ICON } from "./kindIcon";

const NET: ChartSeries = { key: "network", label: "Download", color: "var(--g-chart-net)", area: true };
const DISK: ChartSeries = { key: "disk", label: "Disk write", color: "var(--g-chart-disk)", area: true };
const DISK_LINE: ChartSeries = { ...DISK, area: false };
/** The finished download while the files are installed: context, not a live line. */
const NET_QUIET: ChartSeries = { ...NET, area: false, opacity: 0.4 };

const SERIES = {
  /** Downloading, repairing: the download is the area, the drive's writes a line over it. */
  download: [NET, DISK_LINE],
  /** Installing after a download: the drive's writes lead and the finished download stays faintly behind. */
  install: [NET_QUIET, DISK],
  diskOnly: [DISK],
} as const;

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
function JobChart({ job, kind, withNetwork, active, paused }: { job: JobInfo; kind: "download" | "install" | "diskOnly"; withNetwork: boolean; active: boolean; paused: boolean }) {
  useEffect(() => {
    void loadHistory(job.id);
  }, [job.id]);
  const subscribe = useCallback((n: () => void) => subscribeHistory(job.id, n), [job.id]);
  const read = useCallback(() => readChart(job.id, withNetwork), [job.id, withNetwork]);
  // The average line and peak dot belong to the line the readouts lead with: the download while downloading, else the drive.
  const onNetwork = kind === "download";
  const markers = useCallback(() => {
    const p = useProgress.getState().latest[job.id];
    const s = onNetwork ? p?.network : p?.disk;
    return s ? { average: s.averageBytesPerSecond, peak: s.peakBytesPerSecond } : null;
  }, [job.id, onNetwork]);
  return (
    <ThroughputChart
      key={`${kind}-${withNetwork}`}
      className={cx("transition-opacity duration-200", paused && "opacity-60")}
      series={SERIES[kind]}
      markerSeries={kind === "install" ? 1 : 0}
      read={read}
      subscribe={subscribe}
      active={active}
      format={formatSpeed}
      formatTime={formatClock}
      markers={markers}
      legend={false}
      peakLabel={false}
      averageLabel={paused ? "never" : "whenDifferent"}
      minSpan={20}
      plotClassName="h-20 @max-[720px]:h-16"
      label={withNetwork ? "Download and disk speed over time" : "Disk speed over time"}
    />
  );
}

/** One live speed, number big and unit quiet, with its name under it. The names are the chart's legend. */
function ReadoutItem({ r }: { r: Readout }) {
  const at = r.bytesPerSecond > 0 || r.average ? formatSpeed(r.bytesPerSecond) : "0 B/s";
  const split = at.indexOf(" ");
  return (
    <div className="flex flex-col-reverse">
      <dt className="mt-1 flex items-center gap-1.5 text-xs text-fg-muted">
        {r.series && <span aria-hidden className="h-0.5 w-3.5 rounded-full" style={{ background: r.series === "network" ? "var(--g-chart-net)" : "var(--g-chart-disk)", opacity: r.average ? 0.6 : 1 }} />}
        {r.label}
      </dt>
      <dd className={cx("text-lg font-semibold leading-7 tracking-tight tabular-nums", r.average ? "text-fg-subtle" : "text-fg")}>
        {at.slice(0, split)}
        <span className="ml-1 text-sm font-medium text-fg-subtle">{at.slice(split + 1)}</span>
      </dd>
    </div>
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
      icon={<Icon size={size === "sm" ? 12 : 14} fill="currentColor" strokeWidth={0} />}
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

/** Time left beside the big percentage (or elapsed, when there is no estimate). Elapsed time rides in the tooltip. */
function TimeLeft({ text, tip }: { text: string; tip: string | null }) {
  const label = <span className="whitespace-nowrap text-base tabular-nums text-fg-muted">{text}</span>;
  if (!tip) return label;
  return (
    <Tooltip content={tip}>
      <span className="cursor-default whitespace-nowrap text-base tabular-nums text-fg-muted">
        {text}
        <span className="sr-only">, {tip}</span>
      </span>
    </Tooltip>
  );
}

export interface ActiveJobCardProps {
  job: JobInfo;
  /** `compact` is the Home row: art, title, state, one bar and a single line of figures. */
  variant?: "full" | "compact";
  className?: string;
}

/**
 * The running job, top to bottom: the game and what it is doing, the percentage with the time left, the bar with the
 * bytes of this phase, the stepper of planned phases under the bar, then the live speeds (they are the chart's legend), the
 * speed history while bytes are moving and, at the right end of the speeds, what is holding the speed back. Every figure
 * comes from `figuresOf` / `speedStatusOf`, so a verify can never show a download speed, and no fact appears twice.
 */
export function ActiveJobCard({ job, variant = "full", className }: ActiveJobCardProps) {
  const visible = useWindowVisible();
  // The one thing that keeps moving on a long download is the bar's sheen, and only while someone could be looking at it.
  const focused = useApp((s) => s.window.focused);
  // The figures are read, not watched: 2 Hz, and not at all while the window is hidden. The chart and bar feed on their own.
  const p = useJobProgressThrottled(job.id, job.progress, visible);
  const openDialog = useUi((s) => s.openDialog);
  const speedLimit = useSnap((s) => s.settings.speedLimitBytesPerSecond);
  const chosenMode = useSnap((s) => s.settings.downloadMode);
  const effectiveMode = useSnap((s) => s.queue.effectiveMode);
  const modeReason = useSnap((s) => s.queue.modeReason);
  const state = stateOf(job, p);
  const meta = metaOf(job, p);
  const fig = figuresOf(job, p);
  const running = job.status === "running";
  const paused = job.status === "paused";
  const waiting = job.status === "waiting";
  const percent = Math.floor(Math.min(100, Math.max(0, p?.percent ?? 0)));
  const phase = p?.phase ?? job.phase;
  const withNetwork = fig.chart === "network+disk" || (fig.chart === "disk" && historyHasNetwork(job.id));
  const chartKind = fig.chart === "network+disk" ? "download" : withNetwork ? "install" : "diskOnly";
  const showChart = fig.chart !== null && p !== null;
  const net = p?.network ?? null;
  const live = running ? (net?.bytesPerSecond ?? p?.disk?.bytesPerSecond ?? p?.verify?.readBytesPerSecond ?? null) : null;
  const speedContext = { speedLimitBytesPerSecond: speedLimit, path: job.targetPath, chosenMode, effectiveMode, modeReason };
  const speedStatus = speedStatusOf(job, p, fig.throughput, speedContext);
  const time = timeOf(job, p);
  const headline = headlineOf(fig);
  const tone = state.tone === "accent" ? "accent" : state.tone;
  // A job that has not started (waiting, queued) has no steps, percent or bar to show: just why it is not running.
  const started = p !== null;
  // While the stepper is on screen its lit step already names what the job is doing: the state label would say it twice.
  const steps = started && job.phases.length > 1;
  const showState = !(running && steps);
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
          still={!focused}
          label={`${state.label} ${job.title}`}
        />
        <p className="mt-2 truncate text-xs tabular-nums text-fg-muted">
          {running && live != null && <span className="text-fg">{formatSpeed(live)} · </span>}
          {headline && <>{headline} · </>}
          {percent}%{time?.tip && ` · ${time.text}`}
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
          {(showState || meta) && (
            <p className="mt-1.5 flex min-w-0 items-center gap-2 text-sm text-fg-muted">
              {showState && (
                <>
                  <StatusDot tone={tone} />
                  <span className="shrink-0 font-medium text-fg">{state.label}</span>
                </>
              )}
              {meta && <span className="truncate">{meta}</span>}
            </p>
          )}
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
          <div className="flex items-baseline justify-between gap-4">
            <div className="flex min-w-0 items-baseline gap-3">
              <p className="text-2xl font-semibold leading-none tracking-tighter tabular-nums text-fg" aria-hidden>
                {percent}
                <span className="ml-0.5 text-lg font-medium text-fg-subtle">%</span>
              </p>
              {time && <TimeLeft text={time.text} tip={time.tip} />}
            </div>
            {headline && (
              <p className="shrink-0 text-sm tabular-nums text-fg-muted">
                <Headline text={headline} />
              </p>
            )}
          </div>
          <ProgressBar
            className="mt-3"
            size="lg"
            value={percent}
            tone={paused || waiting ? "warning" : "accent"}
            paused={paused}
            speed={live}
            still={!focused}
            label={`${state.label} ${job.title}`}
          />
          {steps && <PhaseStepper className="mt-4" steps={job.phases.map((id) => ({ id, label: PHASE_STATE[id] }))} current={phase} paused={paused} />}
        </div>
      )}

      {p && (fig.readouts.length > 0 || fig.damaged) && (
        <div className="mt-6 flex flex-wrap items-end justify-between gap-x-8 gap-y-2 px-5">
          <dl className="flex items-end gap-x-8" aria-label="Speed">
            {fig.readouts.map((r) => (
              <ReadoutItem key={r.key} r={r} />
            ))}
          </dl>
          {fig.damaged ? (
            <p className="pb-0.5 text-xs font-medium tabular-nums text-warning-text">{fig.damaged}</p>
          ) : speedStatus ? (
            <div className="pb-0.5">
              <SpeedStatus status={speedStatus} progress={p} context={speedContext} />
            </div>
          ) : null}
        </div>
      )}

      {showChart && (
        <div className="mt-3 px-5">
          <JobChart job={job} kind={chartKind} withNetwork={withNetwork} active={visible} paused={!running} />
        </div>
      )}

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
