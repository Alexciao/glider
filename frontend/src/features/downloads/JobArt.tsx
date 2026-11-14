import { Download } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { useGame } from "../../state/library";
import { cx } from "../../ui";
import { KIND_ICON } from "./kindIcon";

const sizes = { xs: "h-9 w-7 rounded-sm", sm: "h-11 w-8 rounded-sm", md: "h-14 w-10 rounded-md", lg: "h-16 w-12 rounded-md" } as const;

/** The game's tall key art as a small tile; a kind icon on a quiet tile when there is no art (overlay jobs, no-art mode). */
export function JobArt({ job, size = "md", className }: { job: Pick<JobInfo, "appName" | "kind">; size?: keyof typeof sizes; className?: string }) {
  const game = useGame(job.appName);
  const url = game?.art.tallThumb ?? game?.art.tall ?? null;
  return (
    <span
      aria-hidden
      className={cx(
        "relative grid shrink-0 place-items-center overflow-hidden border border-border-strong bg-surface-2 bg-cover bg-center text-fg-muted [box-shadow:inset_0_1px_0_var(--g-highlight)]",
        sizes[size],
        className,
      )}
      style={{ backgroundColor: game?.art.placeholder ?? undefined, backgroundImage: url ? `url("${url}")` : undefined }}
    >
      {!url && (KIND_ICON[job.kind] ?? <Download size={16} />)}
    </span>
  );
}
