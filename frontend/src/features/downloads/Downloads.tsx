import { useEffect, useState } from "react";
import { Download } from "lucide-react";
import { actions, refreshSnapshot } from "../../state/actions";
import { failedJobsOf, hasSpaceFailure, pastJobsOf, updatesToOffer } from "../../state/failures";
import { plural } from "../../state/format";
import { useNow, useWindowVisible } from "../../state/hooks";
import { currentJob, queuedJobs, useSnap } from "../../state/store";
import { READY_KINDS } from "../../state/transfer";
import { suppressNoticesWhen } from "../../state/notices";
import { useUi } from "../../state/ui";
import type { JobInfo } from "../../ipc/contract.gen";
import { Button, EmptyState, ReorderList } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageHeader, PageScroll } from "../common/Page";
import { ActiveJobCard } from "./ActiveJobCard";
import { CouldntFinish } from "./CouldntFinish";
import { HistoryRow } from "./HistoryRow";
import { Notice, queueNotice } from "./QueueNotice";
import { QueueRow } from "./QueueRow";

/** The latest few finished jobs; the rest sit behind "Show earlier". */
const HISTORY_VISIBLE = 3;
/** How often free space is re-read while a failure for lack of space is on screen. Core caches drive info for 10 s. */
const SPACE_RECHECK_MS = 12_000;
const DRIVE_CACHE_MS = 10_000;

/** How long a finished install stays on the page as "Ready to play" (the spec has no timeout; this one only stops a stale card sitting there all day). */
export const READY_MS = 10 * 60_000;

/**
 * The job that just finished while this page was open, kept as the card ("Ready to play", Play) until the next job starts, the page is
 * left (the state is the page's own) or `READY_MS` pass. A job that finished while the page was closed goes straight to History.
 */
export function useReadyJob(jobs: readonly JobInfo[], current: JobInfo | undefined): JobInfo | undefined {
  const [readyId, setReadyId] = useState<string | null>(null);
  const [prevId, setPrevId] = useState(current?.id);
  if (prevId !== current?.id) {
    setPrevId(current?.id);
    const done = prevId ? jobs.find((j) => j.id === prevId) : undefined;
    // The next job starting takes the card's place; otherwise the one that ended keeps it.
    setReadyId(!current && done?.status === "completed" && READY_KINDS.includes(done.kind) ? done.id : null);
  }
  useEffect(() => {
    if (!readyId) return;
    const id = setTimeout(() => setReadyId(null), READY_MS);
    return () => clearTimeout(id);
  }, [readyId]);
  return current ? undefined : jobs.find((j) => j.id === readyId && j.status === "completed");
}

function SectionHeader({ title, action }: { title: string; action?: React.ReactNode }) {
  return (
    <div className="mb-1 flex min-h-7 items-center justify-between">
      <h2 className="text-sm font-semibold text-fg">{title}</h2>
      {action}
    </div>
  );
}

/**
 * While a "not enough space" failure is on screen, keep the drive's free space fresh: when the window gets focus again
 * (the user went to free some space) and every few seconds while it is visible, so the row can say "There's enough space now."
 */
function useFreshFreeSpace(watching: boolean): void {
  const visible = useWindowVisible();
  useEffect(() => {
    if (!watching || !visible) return;
    void refreshSnapshot();
    const id = setInterval(() => void refreshSnapshot(), SPACE_RECHECK_MS);
    let late: ReturnType<typeof setTimeout> | undefined;
    const onFocus = () => {
      void refreshSnapshot();
      // Core caches drive info for 10 s, so the first read can still show the old number: look once more right after it expires.
      clearTimeout(late);
      late = setTimeout(() => void refreshSnapshot(), DRIVE_CACHE_MS + 500);
    };
    window.addEventListener("focus", onFocus);
    return () => {
      clearInterval(id);
      clearTimeout(late);
      window.removeEventListener("focus", onFocus);
    };
  }, [watching, visible]);
}

export function Downloads() {
  const queue = useSnap((s) => s.queue);
  const storage = useSnap((s) => s.storage);
  const updates = useSnap((s) => s.library.updatesAvailable);
  const err = useUi((s) => s.errors.downloads);
  const go = useUi((s) => s.go);
  const now = useNow(30_000);
  const jobs = queue.jobs;
  const current = currentJob(jobs);
  const queued = queuedJobs(jobs, current);
  const failed = failedJobsOf(jobs);
  const ready = useReadyJob(jobs, current);
  // The card still shows a job that just finished: it joins History once it leaves.
  const history = pastJobsOf(jobs).filter((j) => j.id !== ready?.id);
  const [showAll, setShowAll] = useState(false);
  const shownHistory = showAll ? history : history.slice(0, HISTORY_VISIBLE);
  useFreshFreeSpace(hasSpaceFailure(failed));
  // This page lists every failure in place ("Couldn't finish"): a toast per failed job would say it twice.
  useEffect(() => suppressNoticesWhen((n) => n.level === "error" && !!n.jobId), []);
  // Updates that are not already waiting in "Couldn't finish": those have their own "Try again".
  const toUpdate = updatesToOffer(updates, failed);
  // A hold (no connection, a game running, the Epic launcher) is said once, on the card; a queue the player paused is the page's to say.
  const notice = queueNotice(queue, current);
  // Nothing to show at the top: an invitation, unless something failed (then the failures lead).
  const idle = !current && !ready && failed.length === 0;

  return (
    <PageScroll>
      <PageHeader title="Downloads" />
      {err && <ErrorView className="mb-4" error={err.error} retry={err.retry} onDismiss={() => useUi.getState().setError("downloads", undefined)} />}

      {notice && (
        <div className="mb-4">
          <Notice {...notice} />
        </div>
      )}
      <div className="flex flex-col gap-10">
        {(current ?? ready) && <ActiveJobCard job={(current ?? ready)!} />}

        {idle && (
          <EmptyState
            className={history.length > 0 ? "py-6" : "py-16"}
            icon={<Download size={20} />}
            title="Nothing downloading"
            action={
              toUpdate > 0 ? (
                <Button variant="primary" onClick={() => void actions.updateAll()}>
                  Update {plural(toUpdate, "game")}
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => go("library")}>
                  Browse your library
                </Button>
              )
            }
          />
        )}

        {queued.length > 0 && (
          <section>
            <SectionHeader title="Up next" />
            <ReorderList
              aria-label="Up next"
              itemClassName="-mx-3 rounded-lg transition-colors hover:bg-surface-hover"
              items={queued}
              getId={(j) => j.id}
              getLabel={(j) => j.title}
              onMove={(id, to) => void actions.reorderJob(id, to + (current ? 1 : 0))}
              renderItem={(job, { index, handle }) => <QueueRow job={job} handle={handle} canMoveUp={index > 0} sharedReason={current?.status === "waiting" ? current.blockedReason : null} />}
            />
          </section>
        )}

        <CouldntFinish jobs={failed} storage={storage} now={now} />

        {!current && failed.length > 0 && toUpdate > 0 && (
          <div className="flex min-h-8 items-center justify-between gap-4 text-sm text-fg-muted">
            <p>Other games have updates.</p>
            <Button size="sm" variant="secondary" onClick={() => void actions.updateAll()}>
              Update {plural(toUpdate, "game")}
            </Button>
          </div>
        )}

        {history.length > 0 && (
          <section>
            <SectionHeader
              title="History"
              action={
                <Button size="sm" variant="ghost" onClick={() => void actions.clearFinished()}>
                  Clear all
                </Button>
              }
            />
            <ul>
              {shownHistory.map((j) => (
                <li key={j.id} className="-mx-3 rounded-lg transition-colors hover:bg-surface-hover">
                  <HistoryRow job={j} now={now} />
                </li>
              ))}
            </ul>
            {history.length > shownHistory.length && (
              <Button size="sm" variant="ghost" className="-ml-3 mt-1" onClick={() => setShowAll(true)}>
                Show {history.length - shownHistory.length} earlier
              </Button>
            )}
          </section>
        )}
      </div>
    </PageScroll>
  );
}
