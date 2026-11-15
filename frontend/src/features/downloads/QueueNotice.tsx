import { Play } from "lucide-react";
import type { JobInfo, QueueState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { goToSetting } from "../../state/settingsFocus";
import { usePending } from "../../state/ui";
import { Button } from "../../ui";

export interface NoticeContent {
  /** The lead words, in the warning colour. */
  lead: string;
  /** What follows, muted. */
  rest?: string;
  /** Resume the whole queue: only the "paused" notice has it. */
  resume?: boolean;
  /** A quiet link to the setting that holds the queue ("While a game is running"): only the game hold has it. */
  change?: boolean;
}

/**
 * Why the queue is not running, when nothing on the page says so already: paused as a whole (from the tray or the
 * palette), or held by Core (no connection, or a game running with "While a game is running" on Pause). A waiting card
 * already says its own reason, so the page only speaks while the current job isn't waiting. The download mode, the speed
 * limit and the throttle for a running game are not here: the card's speed status says them, only while they matter.
 * Null when there is nothing to add.
 */
export function queueNotice(queue: QueueState, offline: boolean, current: JobInfo | undefined): NoticeContent | null {
  if (!current) return null;
  if (queue.paused) return { lead: "Downloads are paused.", resume: true };
  if (!queue.holdReason || current.status === "waiting") return null;
  if (offline) return { lead: "You're offline.", rest: "Downloads continue when you're back." };
  return { lead: `${queue.holdReason}.`, rest: "Glider continues when it closes.", change: true };
}

/** One quiet line above the list; no box. */
export function Notice({ lead, rest, resume, change }: NoticeContent) {
  const busy = usePending("queue");
  return (
    <div role="status" className="flex min-h-8 items-center justify-between gap-4 text-sm text-fg-muted">
      <p>
        <span className="font-medium text-warning-text">{lead}</span>
        {rest && <> {rest}</>}
        {change && (
          <>
            {" "}
            <button type="button" className="rounded-sm text-accent-text underline underline-offset-2" onClick={() => goToSetting("whilePlaying")}>
              Change
            </button>
          </>
        )}
      </p>
      {resume && (
        <Button size="sm" variant="secondary" loading={busy} icon={<Play size={12} fill="currentColor" strokeWidth={0} />} onClick={() => void actions.resumeQueue()}>
          Resume all
        </Button>
      )}
    </div>
  );
}
