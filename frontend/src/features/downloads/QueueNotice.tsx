import { Play } from "lucide-react";
import type { QueueState } from "../../ipc/contract.gen";
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
 * Why the queue is not running, when nothing on the page says so already: paused as a whole (from the tray or the
 * palette), or offline. The download mode, the speed limit and the throttle for a running game are not here: the card's
 * speed status says them, only while they matter. Null when there is nothing to add.
 */
export function queueNotice(queue: QueueState, offline: boolean, hasActive: boolean): NoticeContent | null {
  if (hasActive && queue.paused) return { lead: "Downloads are paused.", resume: true };
  if (offline) return { lead: "You're offline.", rest: "Downloads continue on their own when your connection is back." };
  return null;
}

/** One quiet line above the list; no box. */
export function Notice({ lead, rest, resume }: NoticeContent) {
  const busy = usePending("queue");
  return (
    <div role="status" className="flex min-h-8 items-center justify-between gap-4 px-1 text-sm text-fg-muted">
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
