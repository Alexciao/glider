import { useCallback, useEffect } from "react";
import { Activity, ArrowDownToLine, Download, FolderInput, HardDrive, Layers, Pause, Play, ShieldCheck, Trash2, Wrench, X } from "lucide-react";
import type { JobInfo, JobKind, JobPhase } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_VERB, PHASE_LABEL } from "../../state/derive";
import { formatBytes, formatClock, formatEta, formatSpeed, prettyVersion } from "../../state/format";
import { HISTORY_CAPACITY, loadHistory, readHistory, subscribeHistory, useJobProgress, useProgress } from "../../state/progress";
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
  modify: <Layers size={18} />,
  overlay: <HardDrive size={18} />,
};

const PAUSABLE: readonly JobKind[] = ["install", "update", "repair", "verify", "modify", "overlay"];

function Metric({ label, children, dim }: { label: string; children: React.ReactNode; dim?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-fg-muted">{label}</dt>
      <dd className={cx("mt-1 text-md font-semibold tabular-nums transition-colors duration-200", dim ? "text-fg-subtle" : "text-fg")}>{children}</dd>
    </div>
  );
}

/** The job's planned phases as a row of steps; the current one is highlighted, finished ones are checked. */
function PhaseSteps({ phases, current }: { phases: readonly JobPhase[]; current: JobPhase | null }) {
  if (phases.length < 2) return null;
  const at = current ? phases.indexOf(current) : -1;
  return (
    <ol aria-label="Steps" className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-xs">
      {phases.map((p, i) => (
        <li key={p} aria-current={i === at ? "step" : undefined} className="flex items-center gap-1.5">
          <span className={cx("transition-colors duration-200", i === at ? "font-medium text-fg" : i < at ? "text-fg-muted" : "text-fg-subtle")}>{PHASE_LABEL[p]}</span>
          {i < phases.length - 1 && <span aria-hidden className="text-fg-subtle">›</span>}
        </li>
      ))}
    </ol>
  );
}

/** Announces progress to assistive tech at 10% steps and phase changes, not at 10 Hz. */
function ProgressAnnouncer({ job }: { job: JobInfo }) {
  const bucket = useProgress((s) => Math.floor((s.latest[job.id]?.percent ?? job.progress?.percent ?? 0) / 10) * 10);
  const phase = useProgress((s) => s.latest[job.id]?.phase ?? job.progress?.phase);
  return (
    <span className="sr-only" role="status" aria-live="polite">
      {job.status === "running" ? `${JOB_VERB[job.kind]} ${job.title}. ${phase ? PHASE_LABEL[phase] + ", " : ""}${bucket} percent.` : ""}
    </span>
  );
}

export function ActiveJobCard({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const busy = usePending(`job:${job.id}`);
  const openDialog = useUi((s) => s.openDialog);

  const paused = job.status === "paused";
  const waiting = job.status === "waiting";
  const queued = job.status === "queued";
  const running = job.status === "running";
  const percent = p?.percent ?? 0;
  const net = p?.network ?? null;
  const disk = p?.disk ?? null;
  const verify = p?.verify ?? null;
  const channel = net ? "network" : "disk";
  const hasChart = (net ?? disk ?? verify) !== null && job.kind !== "uninstall";

  // The graph survives a UI reload: fetch the backend's history once, then the live events extend it.
  useEffect(() => {
    void loadHistory(job.id);
  }, [job.id]);
  const read = useCallback(() => readHistory(job.id, channel), [job.id, channel]);
  const subscribe = useCallback((n: () => void) => subscribeHistory(job.id, n), [job.id]);

  const subtitle =
    job.kind === "update" && job.fromVersion && job.toVersion
      ? `${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`
      : job.kind === "install" && job.toVersion
        ? `Version ${prettyVersion(job.toVersion)}`
        : null;

  return (
    <Surface tone="raised" className="overflow-hidden">
      <ProgressAnnouncer job={job} />
      <div className="flex items-start gap-4 p-5 pb-4">
        <div className="grid size-11 shrink-0 place-items-center rounded-xl border border-border-strong bg-surface-2 text-fg">{KIND_ICON[job.kind]}</div>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <h2 className="text-md font-semibold tracking-tight text-fg">
              {JOB_VERB[job.kind]} {job.title}
            </h2>
            {paused && <Badge tone="warning" dot>Paused</Badge>}
            {waiting && <Badge tone="warning" dot>Waiting</Badge>}
            {queued && <Badge tone="neutral">Queued</Badge>}
          </div>
          <div className="mt-1 flex flex-col gap-1 text-sm text-fg-muted">
            {subtitle && <p>{subtitle}</p>}
            {running && <PhaseSteps phases={job.phases} current={p?.phase ?? job.phase} />}
            {job.kind === "move" && job.targetPath && (
              <p className="flex min-w-0 gap-1">
                to <PathText path={job.targetPath} className="text-fg" />
              </p>
            )}
            {waiting && <p>{(job.blockedReason ?? "Another task is using the game files").replace(/[.\s]+$/, "")}, so Glider will wait and continue automatically.</p>}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {(running || paused) && PAUSABLE.includes(job.kind) && (
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
          {p && (
            <p className="pb-1 text-sm text-fg-muted tabular-nums">
              {net ? (
                <>
                  <span className="text-fg">{formatBytes(net.downloadedBytes)}</span> of {formatBytes(net.totalBytes)}
                </>
              ) : verify ? (
                <>
                  <span className="text-fg">{verify.filesChecked.toLocaleString()}</span> of {verify.filesTotal.toLocaleString()} files
                </>
              ) : disk ? (
                <>
                  <span className="text-fg">{formatBytes(disk.writtenBytes)}</span> of {formatBytes(disk.totalBytes)}
                </>
              ) : null}
            </p>
          )}
        </div>
        <ProgressBar
          className="mt-3.5"
          size="lg"
          value={queued || (waiting && !p) ? 0 : percent}
          tone={paused || waiting ? "warning" : "accent"}
          paused={paused}
          speed={net && running ? net.bytesPerSecond : null}
          label={`${JOB_VERB[job.kind]} ${job.title}`}
        />
      </div>

      <dl className="grid grid-cols-4 gap-4 px-5 pb-5 pt-5 max-[1100px]:grid-cols-2">
        {net ? (
          <>
            <Metric label="Download speed" dim={!running}>{running && p ? formatSpeed(net.bytesPerSecond) : "—"}</Metric>
            <Metric label="Disk write" dim={!running}>{running && disk ? formatSpeed(disk.bytesPerSecond) : "—"}</Metric>
          </>
        ) : verify ? (
          <>
            <Metric label="Disk read" dim={!running}>{running ? formatSpeed(verify.readBytesPerSecond) : "—"}</Metric>
            <Metric label="Damaged files">{verify.badFiles > 0 ? `${verify.badFiles} · ${verify.fixedFiles} fixed` : "None so far"}</Metric>
          </>
        ) : (
          <>
            <Metric label="Disk speed" dim={!running}>{running && disk ? formatSpeed(disk.bytesPerSecond) : "—"}</Metric>
            <Metric label="Step">{p ? PHASE_LABEL[p.phase] : "—"}</Metric>
          </>
        )}
        <Metric label="Time left" dim={!running}>{running && p ? formatEta(p.etaSeconds) : "—"}</Metric>
        <Metric label="Elapsed">{p ? formatClock(p.elapsedSeconds) : "0:00"}</Metric>
      </dl>

      {hasChart && (running || paused || percent > 0) && (
        <div className="border-t border-border bg-surface-1 px-5 pb-4 pt-3.5">
          <Sparkline
            read={read}
            subscribe={subscribe}
            rate={1}
            minWindow={60}
            maxWindow={HISTORY_CAPACITY}
            format={formatSpeed}
            title={
              <>
                <Activity size={12} /> {net ? "Network speed" : verify ? "Disk read speed" : "Disk speed"}
              </>
            }
            label="Throughput history"
          />
        </div>
      )}
    </Surface>
  );
}
