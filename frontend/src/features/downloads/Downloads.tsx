import { useState } from "react";
import { Check, ChevronsUp, Download, Pause, Play, X } from "lucide-react";
import type { JobInfo, JobStatus } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_ACTION, JOB_PAST } from "../../state/derive";
import { formatBytes, formatDuration, formatRelativeTime, prettyVersion } from "../../state/format";
import { useNow } from "../../state/hooks";
import { goGame } from "../../state/router";
import { currentJob, historyJobs, queuedJobs, useSnap } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Badge, Button, EmptyState, IconButton, Surface, type BadgeTone } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageHeader, PageScroll } from "../common/Page";
import { ActiveJobCard, KIND_ICON } from "./ActiveJobCard";

const STATUS: Record<JobStatus, { label: string; tone: BadgeTone }> = {
  queued: { label: "Queued", tone: "neutral" },
  running: { label: "Running", tone: "accent" },
  paused: { label: "Paused", tone: "warning" },
  waiting: { label: "Waiting", tone: "warning" },
  completed: { label: "Completed", tone: "success" },
  failed: { label: "Failed", tone: "danger" },
  cancelled: { label: "Cancelled", tone: "neutral" },
};

function jobSubtitle(job: JobInfo, now: number): { lead: string | null; rest: string } {
  let lead: string | null = null;
  if (job.kind === "update" && job.fromVersion && job.toVersion) lead = `${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`;
  else if (job.toVersion) lead = `Version ${prettyVersion(job.toVersion)}`;
  const rest: string[] = [];
  if (job.finishedAt) {
    const secs = (Date.parse(job.finishedAt) - Date.parse(job.createdAt)) / 1000;
    if (job.status === "completed" && secs > 0) rest.push(`took ${formatDuration(secs)}`);
    rest.push(formatRelativeTime(job.finishedAt, now));
  } else rest.push(formatRelativeTime(job.createdAt, now));
  const total = job.progress?.network?.totalBytes ?? 0;
  if (job.status === "completed" && total > 0) rest.push(`${formatBytes(total)} downloaded`);
  if (job.status === "waiting" && job.blockedReason) rest.push(job.blockedReason);
  if (job.outcome && job.outcome.kind !== "success") rest.push(job.outcome.message);
  return { lead, rest: rest.join("  ·  ") };
}

function SubtitleLine({ lead, rest }: { lead: string | null; rest: string }) {
  return (
    <p className="mt-0.5 flex min-w-0 text-xs text-fg-muted tabular-nums">
      {lead && <span className="min-w-0 truncate" title={lead}>{lead}</span>}
      <span className="shrink-0 whitespace-pre">{lead ? "  ·  " : ""}{rest}</span>
    </p>
  );
}

function JobRow({ job, history, now, canMoveUp }: { job: JobInfo; history?: boolean; now: number; canMoveUp?: boolean }) {
  const st = STATUS[job.status];
  const busyJob = usePending(`job:${job.id}`);
  const busyDismiss = usePending(`dismiss:${job.id}`);
  const busy = busyJob || busyDismiss;
  const openDialog = useUi((s) => s.openDialog);
  return (
    <li className="px-4 py-3">
      <div className="flex items-center gap-3.5">
        <span className="grid size-9 shrink-0 place-items-center rounded-lg bg-surface-2 text-fg-muted">{KIND_ICON[job.kind]}</span>
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium text-fg">
            <button type="button" className="text-left hover:underline" onClick={() => job.appName !== "eos-overlay" && goGame(job.appName)}>
              {job.status === "completed" ? JOB_PAST[job.kind] : JOB_ACTION[job.kind]} {job.title}
            </button>
          </p>
          <SubtitleLine {...jobSubtitle(job, now)} />
        </div>
        <Badge tone={st.tone} dot={job.status === "failed"} icon={job.status === "completed" ? <Check size={11} strokeWidth={2.5} /> : undefined}>
          {st.label}
        </Badge>
        {history ? (
          <IconButton label="Remove from history" size="sm" disabled={busy} onClick={() => void actions.dismissJob(job.id)}>
            <X size={14} />
          </IconButton>
        ) : (
          <>
            {canMoveUp && (
              <IconButton label="Move to top of the queue" size="sm" disabled={busy} onClick={() => void actions.moveJobToTop(job.id)}>
                <ChevronsUp size={14} />
              </IconButton>
            )}
            <Button size="sm" variant="ghost" onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
              Cancel
            </Button>
          </>
        )}
      </div>
      {job.status === "failed" && job.error && (
        <ErrorView className="mt-3" error={job.error} retry={() => void actions.retryJob(job)} path={job.targetPath} />
      )}
    </li>
  );
}

function List({ title, action, children }: { title: string; action?: React.ReactNode; children: React.ReactNode }) {
  return (
    <section className="mt-8">
      <div className="mb-2 flex items-center justify-between px-1">
        <h2 className="text-sm font-semibold text-fg">{title}</h2>
        {action}
      </div>
      <Surface>
        <ul className="divide-y divide-border">{children}</ul>
      </Surface>
    </section>
  );
}

/** More than this many finished jobs collapse behind "Show older". */
const HISTORY_VISIBLE = 12;

export function Downloads() {
  const queue = useSnap((s) => s.queue);
  const jobs = queue.jobs;
  const updates = useSnap((s) => s.library.updatesAvailable);
  const err = useUi((s) => s.errors.downloads);
  const go = useUi((s) => s.go);
  const now = useNow(30_000);
  const current = currentJob(jobs);
  const queued = queuedJobs(jobs, current);
  const history = historyJobs(jobs);
  const [showAll, setShowAll] = useState(false);
  const shownHistory = showAll ? history : history.slice(0, HISTORY_VISIBLE);

  const empty = !current && queued.length === 0 && history.length === 0;

  return (
    <PageScroll>
      <PageHeader
        title="Downloads"
        description={queue.modeReason ?? "Installs, updates and repairs, in the order they run."}
        actions={
          current ? (
            <Button size="sm" variant="secondary" icon={queue.paused ? <Play size={13} fill="currentColor" strokeWidth={0} /> : <Pause size={13} fill="currentColor" strokeWidth={0} />} onClick={() => void (queue.paused ? actions.resumeQueue() : actions.pauseQueue())}>
              {queue.paused ? "Resume all" : "Pause all"}
            </Button>
          ) : undefined
        }
      />
      {err && <ErrorView className="mb-4" error={err.error} retry={err.retry} onDismiss={() => useUi.getState().setError("downloads", undefined)} />}

      {empty ? (
        <Surface>
          <EmptyState
            icon={<Download size={20} />}
            title="Nothing downloading"
            description="When Glider installs, updates or repairs a game, live speed and progress show up here."
            action={
              updates > 0 ? (
                <Button variant="primary" onClick={() => void actions.updateAll()}>
                  Update {updates} {updates === 1 ? "game" : "games"}
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => go("library")}>
                  Browse your library
                </Button>
              )
            }
          />
        </Surface>
      ) : (
        <>
          {current && <ActiveJobCard job={current} />}
          {queued.length > 0 && (
            <List title="Up next">
              {queued.map((j, i) => (
                <JobRow key={j.id} job={j} now={now} canMoveUp={j.status === "queued" && i > 0} />
              ))}
            </List>
          )}
          {history.length > 0 && (
            <List
              title="History"
              action={
                <Button size="sm" variant="ghost" onClick={() => history.forEach((j) => void actions.dismissJob(j.id))}>
                  Clear
                </Button>
              }
            >
              {shownHistory.map((j) => (
                <JobRow key={j.id} job={j} history now={now} />
              ))}
              {history.length > shownHistory.length && (
                <li className="px-4 py-2.5">
                  <Button size="sm" variant="ghost" onClick={() => setShowAll(true)}>
                    Show {history.length - shownHistory.length} older
                  </Button>
                </li>
              )}
            </List>
          )}
        </>
      )}
    </PageScroll>
  );
}
