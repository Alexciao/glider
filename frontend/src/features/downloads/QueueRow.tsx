import { memo, type ReactNode } from "react";
import { ChevronsUp, X } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { formatBytes } from "../../state/format";
import { useWindowVisible } from "../../state/hooks";
import { useGame } from "../../state/library";
import { goGame } from "../../state/router";
import { KIND_NOUN } from "../../state/transfer";
import { usePending, useUi } from "../../state/ui";
import { Button, IconButton, cx } from "../../ui";
import { JobSparkline } from "./JobSparkline";
import { JobArt } from "./JobArt";

/** The one short fact a waiting row carries: how big it is, how far a paused one got, or why it can't run. */
function queueMeta(job: JobInfo, downloadBytes: number | null, showReason: boolean): string {
  if (job.status === "waiting" && showReason) return job.blockedReason ?? "Another task is using the game files";
  const percent = Math.floor(job.progress?.percent ?? 0);
  if (job.status === "paused") return percent > 0 ? `${KIND_NOUN[job.kind]} paused at ${percent}%` : `${KIND_NOUN[job.kind]} paused`;
  return downloadBytes ? `${KIND_NOUN[job.kind]} · ${formatBytes(downloadBytes)}` : KIND_NOUN[job.kind];
}

export interface QueueRowProps {
  job: JobInfo;
  /** The drag grip from `ReorderList`; omit for a static row (Home). */
  handle?: ReactNode;
  /** Offer "Move to top" (false for the next job in line). */
  canMoveUp?: boolean;
  /** The reason the running card already shows: not repeated on every row waiting for the same thing. */
  sharedReason?: string | null;
}

/**
 * One waiting, paused or blocked job on one line: grip, art, game and its one fact, a trend if it has run, and its own
 * controls. The state is the fact ("Paused at 33%", the blocking reason), not a badge beside it.
 */
export const QueueRow = memo(function QueueRow({ job, handle, canMoveUp, sharedReason }: QueueRowProps) {
  const game = useGame(job.appName);
  const busy = usePending(`job:${job.id}`);
  const openDialog = useUi((s) => s.openDialog);
  const visible = useWindowVisible();
  const paused = job.status === "paused";
  const showReason = job.status === "waiting" && job.blockedReason !== sharedReason;
  const meta = queueMeta(job, game?.downloadSizeBytes ?? null, showReason);
  const hasTrend = paused && (job.progress?.percent ?? 0) > 0;
  return (
    <div className="flex h-14 items-center gap-3 px-3">
      <span className="w-6 shrink-0">{handle}</span>
      <JobArt job={job} size="xs" />
      <p className="flex min-w-0 flex-1 items-baseline gap-3 text-sm">
        <button type="button" title={job.title} className="min-w-0 shrink-0 truncate rounded-sm text-left font-medium text-fg hover:underline max-w-[45%]" onClick={() => job.appName !== "eos-overlay" && goGame(job.appName)}>
          {job.title}
        </button>
        <span className={cx("min-w-0 truncate text-xs tabular-nums", showReason ? "text-warning-text" : "text-fg-muted")} title={meta}>
          {meta}
        </span>
      </p>
      {hasTrend && <JobSparkline jobId={job.id} active={visible} />}
      <div className="flex shrink-0 items-center gap-1">
        {paused && (
          <Button size="sm" variant="secondary" loading={busy} onClick={() => void actions.resumeJob(job.id)}>
            Resume
          </Button>
        )}
        {canMoveUp && (
          <IconButton label={`Move ${job.title} to the top`} size="sm" disabled={busy} onClick={() => void actions.moveJobToTop(job.id)}>
            <ChevronsUp size={14} />
          </IconButton>
        )}
        {job.cancellable && (
          <IconButton label={`Cancel ${job.title}`} size="sm" onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
            <X size={14} />
          </IconButton>
        )}
      </div>
    </div>
  );
});
