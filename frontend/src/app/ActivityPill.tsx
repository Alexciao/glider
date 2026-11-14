import { AnimatePresence, m } from "motion/react";
import { Clock, Pause } from "lucide-react";
import type { JobInfo } from "../ipc/contract.gen";
import { JOB_VERB, PHASE_LABEL } from "../state/derive";
import { formatPercent, formatSpeed } from "../state/format";
import { useJobProgress } from "../state/progress";
import { currentJob, useSnap } from "../state/store";
import { useUi } from "../state/ui";
import { Spinner, cx, spring } from "../ui";

function PillBody({ job }: { job: JobInfo }) {
  const progress = useJobProgress(job.id, job.progress);
  const percent = progress?.percent ?? 0;
  const running = job.status === "running";

  let icon = <Spinner size={13} className="text-accent" />;
  let text: string = `${JOB_VERB[job.kind]} ${job.title}`;
  let detail: string | null = null;
  if (job.status === "paused") {
    icon = <Pause size={13} className="text-warning-text" />;
    text = "Paused";
    detail = formatPercent(percent);
  } else if (job.status === "waiting") {
    icon = <Clock size={13} className="text-warning-text" />;
    text = "Waiting";
    detail = job.blockedReason ?? "Waiting";
  } else if (job.status === "queued") {
    icon = <Clock size={13} className="text-fg-subtle" />;
    text = "Queued";
  } else if (progress) {
    detail = progress.network ? `${formatPercent(percent)}  ·  ${formatSpeed(progress.network.bytesPerSecond)}` : `${PHASE_LABEL[progress.phase]}  ·  ${formatPercent(percent)}`;
  }

  return (
    <>
      {icon}
      <span className="font-medium text-fg">{text}</span>
      {detail && <span className="whitespace-pre text-fg-muted tabular-nums">{detail}</span>}
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
  const go = useUi((s) => s.go);
  const label = job ? `${JOB_VERB[job.kind]} ${job.title}, ${job.status}. Open downloads` : "";
  return (
    <AnimatePresence initial={false}>
      {job && (
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
          <PillBody job={job} />
        </m.button>
      )}
    </AnimatePresence>
  );
}
