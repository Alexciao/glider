import { Clock } from "lucide-react";
import { jobVerb } from "../../state/derive";
import { formatPercent } from "../../state/format";
import { useJobProgress } from "../../state/progress";
import { useSnap } from "../../state/store";
import { ProgressRing } from "../../ui";

/** What a game with a job says about it, for its meta line and its accessible name. Subscribes to the job (and nothing else). */
export function useJobState(jobId: string) {
  const job = useSnap((s) => s.queue.jobs.find((j) => j.id === jobId));
  const progress = useJobProgress(jobId, job?.progress);
  if (!job) return null;
  const percent = progress?.percent ?? 0;
  const waiting = job.status === "queued" || job.status === "waiting";
  const text = job.status === "paused" ? "Paused" : waiting ? "Queued" : `${jobVerb(job.kind, progress?.phase ?? job.phase)}`;
  return { job, percent, waiting, paused: job.status === "paused", text };
}

/** A scrim and progress ring over a cover while its game has a job. Live from the progress store (≈10 Hz, this tile only). */
export function CoverProgress({ jobId }: { jobId: string }) {
  const s = useJobState(jobId);
  if (!s) return null;
  const indeterminate = s.job.kind === "uninstall";
  return (
    <div aria-hidden={false} className="absolute inset-0 z-[1] grid place-items-center bg-[oklch(0.12_0.006_265/0.58)] [animation:g-fade-in_180ms_var(--ease-out)]">
      <ProgressRing
        onArt
        size={48}
        stroke={3}
        paused={s.paused}
        value={indeterminate ? null : s.waiting ? 0 : s.percent}
        label={`${s.text}, ${formatPercent(s.percent)}`}
      >
        {s.waiting ? <Clock size={16} className="text-white/85" /> : <span className="text-xs font-medium tabular-nums text-white">{Math.floor(s.percent)}</span>}
      </ProgressRing>
    </div>
  );
}
