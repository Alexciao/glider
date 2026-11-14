import { useState } from "react";
import { Download } from "lucide-react";
import { actions } from "../../state/actions";
import { plural } from "../../state/format";
import { useNow } from "../../state/hooks";
import { currentJob, historyJobs, queuedJobs, useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { Button, EmptyState, ReorderList, Surface } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageHeader, PageScroll } from "../common/Page";
import { ActiveJobCard } from "./ActiveJobCard";
import { HistoryRow } from "./HistoryRow";
import { Notice, QueueControls, queueNotice } from "./QueueBar";
import { QueueRow } from "./QueueRow";

/** The latest few finished jobs; the rest sit behind "Show earlier". */
const HISTORY_VISIBLE = 3;

function SectionHeader({ title, action }: { title: string; action?: React.ReactNode }) {
  return (
    <div className="mb-1 flex min-h-7 items-center justify-between px-1">
      <h2 className="text-sm font-semibold text-fg">{title}</h2>
      {action}
    </div>
  );
}

export function Downloads() {
  const queue = useSnap((s) => s.queue);
  const offline = useSnap((s) => s.connectivity === "offline");
  const chosenMode = useSnap((s) => s.settings.downloadMode);
  const updates = useSnap((s) => s.library.updatesAvailable);
  const err = useUi((s) => s.errors.downloads);
  const go = useUi((s) => s.go);
  const now = useNow(30_000);
  const jobs = queue.jobs;
  const current = currentJob(jobs);
  const queued = queuedJobs(jobs, current);
  const history = historyJobs(jobs);
  const [showAll, setShowAll] = useState(false);
  const shownHistory = showAll ? history : history.slice(0, HISTORY_VISIBLE);
  const busy = !!current;
  const empty = !current && history.length === 0;
  // A running job gets the explanation (throttled, paused by you); a waiting one already says why on its own card.
  const notice = current && current.status !== "waiting" ? queueNotice(queue, chosenMode, offline) : null;

  return (
    <PageScroll wide>
      <PageHeader title="Downloads" actions={busy ? <QueueControls /> : undefined} />
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
                  Update {plural(updates, "game")}
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
          {notice && <Notice {...notice} />}
          {current ? (
            <ActiveJobCard job={current} />
          ) : (
            <EmptyState
              className="py-10"
              icon={<Download size={20} />}
              title="All caught up"
              description="Nothing is downloading right now."
              action={
                updates > 0 ? (
                  <Button variant="primary" onClick={() => void actions.updateAll()}>
                    Update {plural(updates, "game")}
                  </Button>
                ) : undefined
              }
            />
          )}

          {queued.length > 0 && (
            <section className="mt-10">
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

          {history.length > 0 && (
            <section className="mt-10">
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
        </>
      )}
    </PageScroll>
  );
}
