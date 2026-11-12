import { useCallback } from "react";
import { Activity, ArrowDownToLine, Download, FolderInput, Pause, Play, ShieldCheck, Trash2, Wrench, X } from "lucide-react";
import type { JobInfo, JobKind } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_VERB, PHASE_LABEL } from "../../state/derive";
import { formatBytes, formatClock, formatEta, formatSpeed, prettyVersion } from "../../state/format";
import { HISTORY_CAPACITY, readHistory, subscribeHistory, useJobProgress, useProgress } from "../../state/progress";
import { useSnap } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Badge, Button, ProgressBar, Sparkline, Surface, cx } from "../../ui";
import { PathText } from "../common/PathText";

export const KIND_ICON: Record<JobKind, React.ReactNode> = {
  install: <Download size={18} />,
  update: <ArrowDownToLine size={18} />,
  repair: <Wrench size={18} />,
  verify: <ShieldCheck size={18} />,
  move: <FolderInput size={18} />,
  uninstall: <Trash2 size={18} />,
  import: <FolderInput size={18} />,
};

function Metric({ label, children, dim }: { label: string; children: React.ReactNode; dim?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-fg-muted">{label}</dt>
      <dd className={cx("mt-1 text-md font-semibold tabular-nums transition-colors duration-200", dim ? "text-fg-subtle" : "text-fg")}>{children}</dd>
    </div>
  );
}

/** Announces progress to assistive tech at 10% steps and phase changes, not at 10 Hz. */
function ProgressAnnouncer({ job }: { job: JobInfo }) {
  const bucket = useProgress((s) => Math.floor((s.latest[job.id]?.percent ?? job.progress?.percent ?? 0) / 10) * 10);
  const phase = useProgress((s) => s.latest[job.id]?.phase ?? job.progress?.phase);
  return (
    <span className="sr-only" role="status" aria-live="polite">
      {job.status === "running" ? `${JOB_VERB[job.kind]} Fortnite. ${phase ? PHASE_LABEL[phase] + ", " : ""}${bucket} percent.` : ""}
    </span>
  );
}

export function ActiveJobCard({ job }: { job: JobInfo }) {
  const title = useSnap((s) => s.game.title);
  const p = useJobProgress(job.id, job.progress);
  const busy = usePending(`job:${job.id}`);
  const openDialog = useUi((s) => s.openDialog);

  const paused = job.status === "paused";
  const waiting = job.status === "waiting";
  const queued = job.status === "queued";
  const running = job.status === "running";
  const percent = p?.percent ?? 0;
  const downloading = p?.phase === "downloading";
  const hasBytes = job.kind === "install" || job.kind === "update" || job.kind === "repair";

  const read = useCallback(() => readHistory(job.id), [job.id]);
  const subscribe = useCallback((n: () => void) => subscribeHistory(job.id, n), [job.id]);

  const subtitle =
    job.kind === "update" && job.fromVersion && job.toVersion
      ? `${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`
      : job.kind === "install" && job.toVersion
        ? `Version ${prettyVersion(job.toVersion)}`
        : job.kind === "move" && job.targetPath
          ? null
          : job.kind === "verify" || job.kind === "repair"
            ? "Checking every file against Epic's manifest"
            : null;

  return (
    <Surface tone="raised" className="overflow-hidden">
      <ProgressAnnouncer job={job} />
      <div className="flex items-start gap-4 p-5 pb-4">
        <div className="grid size-11 shrink-0 place-items-center rounded-xl border border-border-strong bg-surface-2 text-fg">{KIND_ICON[job.kind]}</div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <h2 className="text-md font-semibold tracking-tight text-fg">
              {JOB_VERB[job.kind]} {title}
            </h2>
            {paused && <Badge tone="warning" dot>Paused</Badge>}
            {waiting && <Badge tone="warning" dot>Waiting</Badge>}
            {queued && <Badge tone="neutral">Queued</Badge>}
            {running && p && <Badge tone="accent">{PHASE_LABEL[p.phase]}</Badge>}
          </div>
          <div className="mt-1 flex flex-col gap-0.5 text-sm text-fg-muted">
            {subtitle && <p>{subtitle}</p>}
            {job.kind === "move" && job.targetPath && (
              <p className="flex min-w-0 gap-1">
                to <PathText path={job.targetPath} className="text-fg" />
              </p>
            )}
            {waiting && <p>{(job.blockedReason ?? "Another task is using the game files").replace(/[.\s]+$/, "")}, so Glider will wait and continue automatically.</p>}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {(running || paused) && job.kind !== "uninstall" && (
            <Button
              variant={paused ? "primary" : "secondary"}
              loading={busy}
              icon={paused ? <Play size={14} fill="currentColor" strokeWidth={0} /> : <Pause size={14} fill="currentColor" strokeWidth={0} />}
              onClick={() => void (paused ? actions.resumeJob(job.id) : actions.pauseJob(job.id))}
            >
              {paused ? "Resume" : "Pause"}
            </Button>
          )}
          {job.kind !== "uninstall" && (
            <Button variant="ghost" icon={<X size={14} />} onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
              Cancel
            </Button>
          )}
        </div>
      </div>

      <div className="px-5">
        <div className="flex items-end justify-between gap-4">
          <p className="text-3xl font-semibold leading-none tracking-tighter tabular-nums text-fg max-[1100px]:text-2xl" aria-hidden>
            {Math.floor(Math.min(100, Math.max(0, percent)))}
            <span className="ml-0.5 text-xl font-medium text-fg-subtle">%</span>
          </p>
          {hasBytes && p && (
            <p className="pb-1 text-sm text-fg-muted tabular-nums">
              {downloading ? (
                <>
                  <span className="text-fg">{formatBytes(p.downloadedBytes)}</span> of {formatBytes(p.totalDownloadBytes)}
                </>
              ) : (
                <>
                  <span className="text-fg">{formatBytes(p.writtenBytes)}</span> of {formatBytes(p.totalWriteBytes)}
                </>
              )}
            </p>
          )}
        </div>
        <ProgressBar className="mt-3.5" size="lg" value={queued || (waiting && !p) ? 0 : percent} tone={paused || waiting ? "warning" : "accent"} label={`${JOB_VERB[job.kind]} progress`} />
      </div>

      <dl className="grid grid-cols-4 gap-4 px-5 pb-5 pt-5 max-[1100px]:grid-cols-2">
        {downloading ? (
          <>
            <Metric label="Download speed" dim={!running}>{running && p ? formatSpeed(p.downloadBytesPerSecond) : "—"}</Metric>
            <Metric label="Disk write" dim={!running}>{running && p ? formatSpeed(p.diskBytesPerSecond) : "—"}</Metric>
          </>
        ) : (
          <>
            <Metric label={p?.phase === "verifying" ? "Disk read" : "Disk speed"} dim={!running}>{running && p ? formatSpeed(p.diskBytesPerSecond) : "—"}</Metric>
            <Metric label="Phase">{p ? PHASE_LABEL[p.phase] : "—"}</Metric>
          </>
        )}
        <Metric label="Time left" dim={!running}>{running && p ? formatEta(p.etaSeconds) : "—"}</Metric>
        <Metric label="Elapsed">{p ? formatClock(p.elapsedSeconds) : "0:00"}</Metric>
      </dl>

      {hasBytes && (running || paused || percent > 0) && (
        <div className="border-t border-border bg-surface-1 px-5 pb-4 pt-3.5">
          <Sparkline
            read={read}
            subscribe={subscribe}
            rate={10}
            minWindow={100}
            maxWindow={HISTORY_CAPACITY}
            format={formatSpeed}
            title={
              <>
                <Activity size={12} /> {downloading ? "Network speed" : "Disk speed"}
              </>
            }
            label="Throughput history"
          />
        </div>
      )}
    </Surface>
  );
}
