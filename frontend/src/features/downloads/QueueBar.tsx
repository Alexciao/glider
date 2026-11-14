import { Gauge, Pause, Play } from "lucide-react";
import type { DownloadMode, QueueState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { formatSpeedCompact } from "../../state/format";
import { useSnap } from "../../state/store";
import { MODE_LABEL } from "../../state/transfer";
import { usePending, useUi } from "../../state/ui";
import { Button, SegmentedControl, Tooltip } from "../../ui";

const MODES = (["background", "balanced", "full"] as const).map((value) => ({ value, label: MODE_LABEL[value] }));

export const MODE_HINT: Record<DownloadMode, string> = {
  background: "Stays out of your way: slower and light on the disk.",
  balanced: "Full network speed, gentle on the disk.",
  full: "Everything this PC has. Other apps may slow down.",
};

/**
 * Queue-wide controls for the page header: the download mode (the same setting as in Settings, via `settings.update`),
 * the speed limit when one is set, and Pause all / Resume all.
 */
export function QueueControls() {
  const chosen = useSnap((s) => s.settings.downloadMode);
  const limit = useSnap((s) => s.settings.speedLimitBytesPerSecond);
  const paused = useSnap((s) => s.queue.paused);
  const go = useUi((s) => s.go);
  const busy = usePending("queue");
  return (
    <>
      <Tooltip content={MODE_HINT[chosen]}>
        <div>
          <SegmentedControl<DownloadMode>
            aria-label="Download mode"
            size="sm"
            className="w-[264px]"
            value={chosen}
            options={MODES}
            onValueChange={(downloadMode) => void actions.updateSettings({ downloadMode })}
          />
        </div>
      </Tooltip>
      {limit ? (
        <Tooltip content="Change the limit in Settings">
          <Button size="sm" variant="ghost" icon={<Gauge size={14} />} onClick={() => go("settings", "downloads")}>
            Limit {formatSpeedCompact(limit)}
          </Button>
        </Tooltip>
      ) : null}
      <Button
        size="sm"
        variant="secondary"
        loading={busy}
        icon={paused ? <Play size={12} fill="currentColor" strokeWidth={0} /> : <Pause size={12} fill="currentColor" strokeWidth={0} />}
        onClick={() => void (paused ? actions.resumeQueue() : actions.pauseQueue())}
      >
        {paused ? "Resume all" : "Pause all"}
      </Button>
    </>
  );
}

export interface NoticeContent {
  /** The lead words, in the warning colour. */
  lead: string;
  /** What follows, muted. */
  rest?: string;
}

/**
 * Why the queue is not running as the user set it: offline, paused while a game runs, throttled to Background, or paused
 * by the user. Null when everything is as chosen (the mode control already says so).
 */
export function queueNotice(queue: QueueState, chosen: DownloadMode, offline: boolean): NoticeContent | null {
  const overridden = queue.effectiveMode !== chosen;
  if (queue.modeReason) return { lead: overridden ? `Running as ${MODE_LABEL[queue.effectiveMode]}.` : `${queue.modeReason}.`, rest: overridden ? `${queue.modeReason}.` : "Glider continues on its own when that changes." };
  if (queue.paused) return { lead: "Downloads are paused.", rest: "Nothing starts until you resume all." };
  if (offline) return { lead: "You're offline.", rest: "Downloads continue on their own when your connection is back." };
  if (overridden) return { lead: `Running as ${MODE_LABEL[queue.effectiveMode]}.`, rest: MODE_HINT[queue.effectiveMode] };
  return null;
}

/** One quiet line above the card; no box. */
export function Notice({ lead, rest }: NoticeContent) {
  return (
    <p role="status" className="mb-4 px-1 text-sm text-fg-muted">
      <span className="font-medium text-warning-text">{lead}</span>
      {rest && <> {rest}</>}
    </p>
  );
}
