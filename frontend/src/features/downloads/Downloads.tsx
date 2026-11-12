import { useState } from "react";
import { Check, Download, X } from "lucide-react";
import type { JobInfo, JobStatus } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_ACTION, JOB_PAST } from "../../state/derive";
import { formatBytes, formatDuration, formatRelativeTime, prettyVersion } from "../../state/format";
import { useNow } from "../../state/hooks";
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
  if (job.progress && job.status === "completed" && job.progress.totalDownloadBytes > 0) rest.push(`${formatBytes(job.progress.totalDownloadBytes)} downloaded`);
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

function JobRow({ job, history, now }: { job: JobInfo; history?: boolean; now: number }) {
  const title = useSnap((s) => s.game.title);
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
            {job.status === "completed" ? JOB_PAST[job.kind] : JOB_ACTION[job.kind]} {title}
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
          <Button size="sm" variant="ghost" onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
            Cancel
          </Button>
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
  const jobs = useSnap((s) => s.jobs);
  const snapGame = useSnap((s) => s.game);
  const auth = useSnap((s) => s.auth.status);
  const err = useUi((s) => s.errors.downloads);
  const openDialog = useUi((s) => s.openDialog);
  const now = useNow(30_000);
  const current = currentJob(jobs);
  const queued = queuedJobs(jobs, current);
  const history = historyJobs(jobs);
  const [showAll, setShowAll] = useState(false);
  const shownHistory = showAll ? history : history.slice(0, HISTORY_VISIBLE);

  const empty = !current && queued.length === 0 && history.length === 0;

  return (
    <PageScroll>
      <PageHeader title="Downloads" description="Installs, updates and repairs, in the order they run." />
      {err && <ErrorView className="mb-4" error={err.error} retry={err.retry} onDismiss={() => useUi.getState().setError("downloads", undefined)} />}

      {empty ? (
        <Surface>
          <EmptyState
            icon={<Download size={20} />}
            title="Nothing downloading"
            description="When Glider installs, updates or repairs Fortnite, live speed and progress show up here."
            action={
              snapGame.installStatus === "notInstalled" && auth === "signedIn" ? (
                <Button variant="primary" onClick={() => openDialog({ kind: "install" })}>
                  Install Fortnite
                </Button>
              ) : snapGame.updateAvailable ? (
                <Button variant="primary" onClick={() => void actions.update()}>
                  Update Fortnite
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => void actions.checkForUpdates()}>
                  Check for updates
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
              {queued.map((j) => (
                <JobRow key={j.id} job={j} now={now} />
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
