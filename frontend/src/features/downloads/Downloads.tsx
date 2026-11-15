import { useEffect, useState } from "react";
import { Download } from "lucide-react";
import { actions, refreshSnapshot } from "../../state/actions";
import { failedJobsOf, hasSpaceFailure, pastJobsOf, updatesToOffer } from "../../state/failures";
import { plural } from "../../state/format";
import { useNow, useWindowVisible } from "../../state/hooks";
import { currentJob, queuedJobs, useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
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

function SectionHeader({ title, action }: { title: string; action?: React.ReactNode }) {
  return (
    <div className="mb-1 flex min-h-7 items-center justify-between px-1">
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
    const onFocus = () => void refreshSnapshot();
    window.addEventListener("focus", onFocus);
    return () => {
      clearInterval(id);
      window.removeEventListener("focus", onFocus);
    };
  }, [watching, visible]);
}

export function Downloads() {
  const queue = useSnap((s) => s.queue);
  const storage = useSnap((s) => s.storage);
  const offline = useSnap((s) => s.connectivity === "offline");
  const updates = useSnap((s) => s.library.updatesAvailable);
  const err = useUi((s) => s.errors.downloads);
  const go = useUi((s) => s.go);
  const now = useNow(30_000);
  const jobs = queue.jobs;
  const current = currentJob(jobs);
  const queued = queuedJobs(jobs, current);
  const failed = failedJobsOf(jobs);
  const history = pastJobsOf(jobs);
  const [showAll, setShowAll] = useState(false);
  const shownHistory = showAll ? history : history.slice(0, HISTORY_VISIBLE);
  useFreshFreeSpace(hasSpaceFailure(failed));
  // Updates that are not already waiting in "Couldn't finish": those have their own "Try again".
  const toUpdate = updatesToOffer(updates, failed);
  // A waiting job already says why on its own card; a paused queue and a lost connection are the page's to say.
  const notice = queueNotice(queue, offline && !!current && current.status !== "waiting", !!current);
  // Nothing to show at the top: an invitation, unless something failed (then the failures lead).
  const idle = !current && failed.length === 0;

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
        {current && <ActiveJobCard job={current} />}

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
              itemClassName="rounded-lg transition-colors hover:bg-surface-hover"
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
          <div className="flex min-h-8 items-center justify-between gap-4 px-1 text-sm text-fg-muted">
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
                <li key={j.id} className="rounded-lg transition-colors hover:bg-surface-hover">
                  <HistoryRow job={j} now={now} />
                </li>
              ))}
            </ul>
            {history.length > shownHistory.length && (
              <Button size="sm" variant="ghost" className="ml-1 mt-1" onClick={() => setShowAll(true)}>
                Show {history.length - shownHistory.length} earlier
              </Button>
            )}
          </section>
        )}
      </div>
    </PageScroll>
  );
}
