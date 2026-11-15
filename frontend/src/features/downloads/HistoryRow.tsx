import { memo } from "react";
import { AlertTriangle, Check, Minus, X } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_PAST } from "../../state/derive";
import { formatRelativeTime } from "../../state/format";
import { goGame } from "../../state/router";
import { KIND_NOUN, tookText } from "../../state/transfer";
import { usePending } from "../../state/ui";
import { Button, IconButton, cx } from "../../ui";

/** Failures aren't history: they sit in "Couldn't finish" until they succeed or are dismissed. */
type Result = "done" | "needsRepair" | "cancelled";

export function resultOf(job: JobInfo): Result {
  if (job.status === "cancelled") return "cancelled";
  if (job.outcome?.kind === "needsRepair") return "needsRepair";
  return "done";
}

/** The outcome in a few words, for results that are not a plain success. */
const OUTCOME_WORDS: Partial<Record<Result, { label: string; className: string }>> = {
  needsRepair: { label: "Needs repair", className: "text-warning-text" },
  cancelled: { label: "Cancelled", className: "text-fg-muted" },
};

const ICON: Record<Result, { node: React.ReactNode; className: string }> = {
  // Green is the dot of a running game only; a finished job is an ordinary fact, so its check is quiet.
  done: { node: <Check size={14} strokeWidth={2.5} />, className: "text-fg-subtle" },
  needsRepair: { node: <AlertTriangle size={14} />, className: "text-warning-text" },
  cancelled: { node: <Minus size={14} strokeWidth={2.5} />, className: "text-fg-subtle" },
};

/**
 * What happened, how long it took, when: "Updated in 28 min · 13 h ago". An import says what it linked instead of "Imported".
 * Exported for tests.
 */
export function historyMeta(job: JobInfo, now: number): string {
  const res = resultOf(job);
  const when = formatRelativeTime(job.finishedAt ?? job.createdAt, now);
  if (job.kind === "import" && job.outcome) return `${job.outcome.message.replace(/[.\s]+$/, "")} · ${when}`;
  if (res === "needsRepair" && job.outcome) return `${job.outcome.message.replace(/[.\s]+$/, "")} · ${when}`;
  const took = tookText(job);
  if (res === "done") return `${JOB_PAST[job.kind]}${took ? ` in ${took}` : ""} · ${when}`;
  return `${KIND_NOUN[job.kind]} · ${when}`;
}

/** A finished job on one line. A plain success is just a quiet check; anything else says so in a few words. */
export const HistoryRow = memo(function HistoryRow({ job, now }: { job: JobInfo; now: number }) {
  const res = resultOf(job);
  const words = OUTCOME_WORDS[res];
  const busyDismiss = usePending(`dismiss:${job.id}`);
  const busyRepair = usePending(`verify:${job.appName}`);
  return (
    <div className="group px-3 py-2">
      <div className="flex min-h-8 items-center gap-3">
        <span aria-hidden className={cx("grid w-4 shrink-0 place-items-center", ICON[res].className)}>
          {ICON[res].node}
        </span>
        <p className="flex min-w-0 flex-1 items-baseline gap-3 text-sm">
          <button type="button" title={job.title} className="max-w-[45%] shrink-0 truncate rounded-sm text-left font-medium text-fg hover:underline" onClick={() => job.appName !== "eos-overlay" && goGame(job.appName)}>
            {job.title}
          </button>
          <span className="min-w-0 truncate text-xs tabular-nums text-fg-muted" title={historyMeta(job, now)}>
            {historyMeta(job, now)}
          </span>
        </p>
        {words && <span className={cx("shrink-0 text-xs font-medium", words.className)}>{words.label}</span>}
        {res === "needsRepair" && (
          <Button size="sm" variant="secondary" loading={busyRepair} onClick={() => void actions.verify(job.appName, "downloads")}>
            Repair
          </Button>
        )}
        <IconButton
          label={`Remove ${job.title} from history`}
          size="sm"
          disabled={busyDismiss}
          className="opacity-0 transition-opacity duration-150 focus-visible:opacity-100 group-focus-within:opacity-100 group-hover:opacity-100"
          onClick={() => void actions.dismissJob(job.id)}
        >
          <X size={14} />
        </IconButton>
      </div>
    </div>
  );
});
