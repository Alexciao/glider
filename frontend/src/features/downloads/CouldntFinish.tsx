import { memo, useState } from "react";
import { ChevronRight, Ellipsis, X } from "lucide-react";
import type { JobInfo, StorageState } from "../../ipc/contract.gen";
import { actions, runFix } from "../../state/actions";
import { failedMeta, failureActions, failureMessage, spaceNeedOf, type FailureAction } from "../../state/failures";
import { goGame } from "../../state/router";
import { FIX_LABELS, usePending } from "../../state/ui";
import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, cx } from "../../ui";
import { JobArt } from "./JobArt";

/**
 * One failed job on a few lines: art, the game with "Update failed · 25 min ago", why in plain words, and its fix on the
 * right. No box and no red label: the section's title says it. A failure for lack of space reads the drive's free space as
 * it is now, so "Free up space" gives way to "Try again" the moment there is room, and "Try again" is never taken away.
 */
const FailureRow = memo(function FailureRow({ job, storage, now }: { job: JobInfo; storage: Pick<StorageState, "drives" | "folders">; now: number }) {
  const [details, setDetails] = useState(false);
  const retrying = usePending(`job:${job.id}`);
  const space = spaceNeedOf(job, storage);
  const buttons = failureActions(job, space, FIX_LABELS);
  const detail = job.error?.detail?.trim();
  const run = (a: FailureAction) => {
    if (a.id === "retry") void actions.resumeJob(job.id);
    else runFix(a.fix, { retry: () => void actions.resumeJob(job.id), path: space?.drive ? `${space.drive}\\` : job.targetPath, appName: job.appName });
  };
  const button = (a: FailureAction, variant: "primary" | "secondary" | "ghost") => (
    <Button key={a.id === "retry" ? "retry" : a.fix} size="sm" variant={variant} loading={a.id === "retry" && retrying} onClick={() => run(a)}>
      {a.label}
    </Button>
  );
  // A lone fix is the row's one action; beside a retry it leads (primary) and the retry steps back (secondary), until the
  // space is back and the retry takes the lead.
  const tail = buttons.secondary ? [button(buttons.primary, "primary"), button(buttons.secondary, "secondary")] : [button(buttons.primary, space?.enough ? "primary" : "secondary")];
  return (
    <div className="group py-3 pl-3 pr-2">
      <div className="flex items-start gap-3">
        <JobArt job={job} size="xs" />
        <div className="min-w-0 flex-1">
          <p className="flex min-w-0 items-baseline gap-3 text-sm">
            <button type="button" title={job.title} className="max-w-[45%] shrink-0 truncate rounded-sm text-left font-medium text-fg hover:underline" onClick={() => job.appName !== "eos-overlay" && goGame(job.appName)}>
              {job.title}
            </button>
            <span className="min-w-0 truncate text-xs tabular-nums text-fg-muted">{failedMeta(job, now)}</span>
          </p>
          <p className="mt-1 text-xs leading-[1.5] text-fg-muted">{failureMessage(job, space)}</p>
          {detail && (
            <>
              <button
                type="button"
                aria-expanded={details}
                onClick={() => setDetails((o) => !o)}
                className="mt-1 inline-flex items-center gap-1 rounded-sm text-xs text-fg-subtle transition-colors duration-150 hover:text-fg"
              >
                <ChevronRight size={12} aria-hidden className={cx("transition-transform duration-200 ease-out", details && "rotate-90")} />
                Details
              </button>
              {details && <pre className="selectable mt-1.5 max-h-32 overflow-auto whitespace-pre-wrap break-all rounded-md bg-surface-2 p-2.5 font-mono text-2xs leading-[1.5] text-fg-muted">{detail}</pre>}
            </>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {tail}
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <button
                type="button"
                aria-label={`More for ${job.title}`}
                className="grid size-7 place-items-center rounded-md text-fg-subtle outline-none transition-colors duration-150 hover:bg-surface-hover hover:text-fg focus-visible:bg-surface-hover data-[state=open]:bg-surface-hover data-[state=open]:text-fg"
              >
                <Ellipsis size={16} aria-hidden />
              </button>
            </DropdownMenuTrigger>
            <DropdownMenuContent className="min-w-40">
              <DropdownMenuItem icon={<X size={14} />} onSelect={() => void actions.dismissJob(job.id)}>
                Dismiss
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>
    </div>
  );
});

export function CouldntFinish({ jobs, storage, now }: { jobs: readonly JobInfo[]; storage: Pick<StorageState, "drives" | "folders">; now: number }) {
  if (jobs.length === 0) return null;
  return (
    <section aria-label="Couldn't finish">
      <div className="mb-1 flex min-h-7 items-center px-1">
        <h2 className="text-sm font-semibold text-fg">Couldn't finish</h2>
      </div>
      <ul>
        {jobs.map((j) => (
          <li key={j.id} className="rounded-lg transition-colors hover:bg-surface-hover">
            <FailureRow job={j} storage={storage} now={now} />
          </li>
        ))}
      </ul>
    </section>
  );
}
