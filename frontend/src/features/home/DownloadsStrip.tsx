import { useMemo } from "react";
import type { JobInfo } from "../../ipc/contract.gen";
import { formatPercent } from "../../state/format";
import { useGame } from "../../state/library";
import { useJobProgress } from "../../state/progress";
import { activeJobs, useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { GameCover, ProgressBar } from "../../ui";
import { HomeSection } from "./HomeParts";

const SHOWN = 2;

/** Title, the percentage (or "Paused" / "Queued"), and the bar. Speed, ETA and phases live on the Downloads page this row opens. */
function DownloadRow({ job }: { job: JobInfo }) {
  const go = useUi((s) => s.go);
  const game = useGame(job.appName);
  const progress = useJobProgress(job.id, job.progress);
  const percent = progress?.percent ?? 0;
  const paused = job.status === "paused";
  const waiting = job.status === "queued" || job.status === "waiting";
  const status = paused ? "Paused" : waiting ? "Queued" : formatPercent(percent);
  return (
    <li>
      <button
        type="button"
        onClick={() => go("downloads")}
        aria-label={`${job.title}: ${status}. Open downloads`}
        className="flex w-full items-center gap-3 rounded-md py-1.5 text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
      >
        {game ? <GameCover art={game.art} title={game.title} size="thumb" className="h-10 w-[30px] shrink-0 rounded-sm" /> : <span className="h-10 w-[30px] shrink-0 rounded-sm bg-surface-3" />}
        <span className="min-w-0 flex-1">
          <span className="flex items-baseline justify-between gap-4">
            <span className="truncate text-sm font-medium text-fg">{job.title}</span>
            <span className="shrink-0 text-xs tabular-nums text-fg-muted">{status}</span>
          </span>
          <span className="mt-2 block">
            <ProgressBar size="sm" label={`${job.title} progress`} value={waiting ? 0 : percent} paused={paused || waiting} speed={progress?.network?.bytesPerSecond} tone={paused ? "warning" : "accent"} />
          </span>
        </span>
      </button>
    </li>
  );
}

/** Downloads in progress, compact: the Downloads page owns the full card. */
export function DownloadsStrip() {
  const allJobs = useSnap((s) => s.queue.jobs);
  const jobs = useMemo(() => activeJobs(allJobs), [allJobs]);
  if (jobs.length === 0) return null;
  return (
    <HomeSection title="Downloads">
      <ul>
        {jobs.slice(0, SHOWN).map((j) => (
          <DownloadRow key={j.id} job={j} />
        ))}
      </ul>
    </HomeSection>
  );
}
