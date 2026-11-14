import type { JobInfo } from "../../ipc/contract.gen";
import { jobVerb } from "../../state/derive";
import { formatBytes, formatEta, formatPercent, formatSpeed } from "../../state/format";
import { useJobProgress } from "../../state/progress";
import { ProgressBar } from "../../ui";

/**
 * Bar plus one caption row for the game's running job. The status line above says what is happening; this says how
 * far along it is, once. It re-renders at the progress rate (~10 Hz); nothing else on the page does.
 */
export function HeroProgress({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const percent = p?.percent ?? 0;
  const paused = job.status === "paused";
  const running = job.status === "running";
  const net = p?.network ?? null;
  const verify = p?.verify ?? null;

  let amount: string | null = null;
  if (net) amount = `${formatBytes(net.downloadedBytes)} of ${formatBytes(net.totalBytes)}`;
  else if (verify && p?.phase === "verifying") {
    amount = `${verify.filesChecked.toLocaleString()} of ${verify.filesTotal.toLocaleString()} files${verify.badFiles > 0 ? ` · ${verify.badFiles.toLocaleString()} to fix` : ""}`;
  } else if (p?.filesTotal) amount = `${(p.filesDone ?? 0).toLocaleString()} of ${p.filesTotal.toLocaleString()} files`;

  const speed = net ? net.bytesPerSecond : verify && p?.phase === "verifying" ? verify.readBytesPerSecond : null;
  const queued = job.status === "queued" || job.status === "waiting";
  if (queued) return null;

  return (
    <div className="w-full">
      <ProgressBar
        value={running && !p ? null : percent}
        tone={paused ? "warning" : "accent"}
        paused={paused}
        speed={net ? net.bytesPerSecond : null}
        label={`${jobVerb(job.kind, p?.phase ?? job.phase)} ${job.title}`}
      />
      <div className="mt-2 flex items-center justify-between gap-4 text-xs text-fg-muted tabular-nums">
        <span className="min-w-0 truncate">{[p ? formatPercent(percent) : null, amount].filter(Boolean).join(" · ") || "Starting…"}</span>
        {paused ? (
          <span className="shrink-0">Paused</span>
        ) : running && p ? (
          <span className="shrink-0">
            {speed != null && speed > 0 ? `${formatSpeed(speed)} · ` : ""}
            {formatEta(p.etaSeconds)} left
          </span>
        ) : null}
      </div>
    </div>
  );
}
