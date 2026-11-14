import { AnimatePresence, m } from "motion/react";
import { Clock, Pause } from "lucide-react";
import type { JobInfo } from "../ipc/contract.gen";
import { jobVerb } from "../state/derive";
import { formatPercent, formatSpeed } from "../state/format";
import { useRoute } from "../state/router";
import { PHASE_STATE } from "../state/transfer";
import { useJobProgress } from "../state/progress";
import { activeJobs, currentJob, useSnap } from "../state/store";
import { useUi } from "../state/ui";
import { Spinner, cx, spring } from "../ui";

function PillBody({ job, more }: { job: JobInfo; more: number }) {
  const progress = useJobProgress(job.id, job.progress);
  const percent = progress?.percent ?? 0;
  const running = job.status === "running";

  let icon = <Spinner size={14} className="text-accent" />;
  let text: string = `${jobVerb(job.kind, progress?.phase ?? job.phase)} ${job.title}`;
  let detail: string | null = null;
  if (job.status === "paused") {
    icon = <Pause size={14} className="text-warning-text" />;
    text = "Paused";
    detail = formatPercent(percent);
  } else if (job.status === "waiting") {
    icon = <Clock size={14} className="text-warning-text" />;
    text = "Waiting";
    detail = job.blockedReason;
  } else if (job.status === "queued") {
    icon = <Clock size={14} className="text-fg-subtle" />;
    text = "Queued";
  } else if (progress) {
    // The verb already names the phase ("Verifying Fortnite"); the phase is only added when it says something new ("Finalizing").
    const phase = PHASE_STATE[progress.phase];
    detail = progress.network ? `${formatPercent(percent)}  ·  ${formatSpeed(progress.network.bytesPerSecond)}` : text.startsWith(phase) ? formatPercent(percent) : `${phase}  ·  ${formatPercent(percent)}`;
  }

  return (
    <>
      {icon}
      <span className="font-medium text-fg">{text}</span>
      {detail && <span className="whitespace-pre text-fg-muted tabular-nums">{detail}</span>}
      {more > 0 && <span className="rounded-full bg-surface-3 px-1.5 text-2xs font-medium leading-4 text-fg-muted tabular-nums">+{more}</span>}
      <span aria-hidden className="absolute inset-x-3 bottom-0 h-px overflow-hidden rounded-full">
        <span
          className={cx("block h-full origin-left bg-accent transition-transform duration-[120ms] ease-linear", !running && "opacity-40")}
          style={{ transform: `scaleX(${Math.min(100, Math.max(0, percent)) / 100})` }}
        />
      </span>
    </>
  );
}

/** Slim, persistent status for the active job; lives in the title bar so it never costs layout. */
export function ActivityPill() {
  const job = useSnap((s) => currentJob(s.queue.jobs));
  const more = useSnap((s) => Math.max(0, activeJobs(s.queue.jobs).length - 1));
  const go = useUi((s) => s.go);
  const route = useRoute();
  // Home's Downloads strip, the Downloads card and the game's own hero already show this job: the pill would say it twice.
  const shown = job && !(route.page === "downloads" || route.page === "home" || (route.page === "game" && route.appName === job.appName));
  const label = job ? `${jobVerb(job.kind, job.phase)} ${job.title}, ${job.status}${more > 0 ? `, and ${more} more` : ""}. Open downloads` : "";
  return (
    <AnimatePresence initial={false}>
      {job && shown && (
        <m.button
          key="pill"
          type="button"
          aria-label={label}
          onClick={() => go("downloads")}
          initial={{ opacity: 0, scale: 0.94, y: -4 }}
          animate={{ opacity: 1, scale: 1, y: 0 }}
          exit={{ opacity: 0, scale: 0.96, y: -2, transition: { duration: 0.12 } }}
          transition={spring.snappy}
          className="app-no-drag pointer-events-auto relative flex h-7 items-center gap-2 overflow-hidden rounded-full border border-border-strong bg-surface-2 px-3.5 text-xs transition-colors duration-150 hover:bg-surface-3 active:scale-[0.98]"
        >
          <PillBody job={job} more={more} />
        </m.button>
      )}
    </AnimatePresence>
  );
}
