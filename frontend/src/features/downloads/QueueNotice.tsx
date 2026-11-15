import { Play } from "lucide-react";
import type { JobInfo, QueueState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { usePending } from "../../state/ui";
import { Button } from "../../ui";

export interface NoticeContent {
  /** The lead words, in the warning colour. */
  lead: string;
  /** What follows, muted. */
  rest?: string;
  /** Resume the whole queue: only the "paused" notice has it. */
  resume?: boolean;
}

/**
 * The queue paused as a whole (from the tray or the palette), which no card says: its jobs are all paused, and "Resume all" is
 * the one control that lifts it. Holds Core puts on the queue (no connection, a game running, the Epic launcher) are said once,
 * in place, on the card itself (`cardOf`), so the page stays silent about them. The download mode, the speed limit and the throttle
 * for a running game are the card's disclosure. Null when there is nothing to add.
 */
export function queueNotice(queue: QueueState, current: JobInfo | undefined): NoticeContent | null {
  if (!current) return null;
  return queue.paused ? { lead: "Downloads are paused.", resume: true } : null;
}

/** One quiet line above the list; no box. */
export function Notice({ lead, rest, resume }: NoticeContent) {
  const busy = usePending("queue");
  return (
    <div role="status" className="flex min-h-8 items-center justify-between gap-4 text-sm text-fg-muted">
      <p>
        <span className="font-medium text-warning-text">{lead}</span>
        {rest && <> {rest}</>}
      </p>
      {resume && (
        <Button size="sm" variant="secondary" loading={busy} icon={<Play size={12} fill="currentColor" strokeWidth={0} />} onClick={() => void actions.resumeQueue()}>
          Resume all
        </Button>
      )}
    </div>
  );
}
