import type { ReactNode } from "react";
import { Pause, Play, X } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { useGame } from "../../state/library";
import { usePending, useUi } from "../../state/ui";
import { IconButton, cx } from "../../ui";
import { hueOf } from "../../ui/Avatar";
import { PAUSABLE_KINDS } from "./kindIcon";

/**
 * The card's picture band (docs/design-download-card.md §7): the game's wide art with the title and two icon controls on the top
 * row and the headline and the percent on the bottom row, the bar along its bottom edge. The band is a dark island in both themes
 * (like the game hero), and keeps AA over art Glider doesn't control with three scoped shades instead of a full scrim.
 */

const INK = "oklch(0.15 0.006 265";

/**
 * Readability shades scoped to the text they carry: three soft ellipses anchored at the corners the text sits in (title top-left,
 * headline bottom-left, percent bottom-right). The two on the left overlap into one smooth column and the rest of the art is
 * untouched. Sized so every line keeps AA on flat white art (`e2e/card-contrast.spec.ts`); the title is capped at 46 % of the band
 * so it never leaves its shade.
 */
const SHADES = [
  `radial-gradient(ellipse 80% 112px at 0% 0%, ${INK} / 0.8) 0%, ${INK} / 0.66) 60%, ${INK} / 0) 100%)`,
  `radial-gradient(ellipse 80% 120px at 0% 100%, ${INK} / 0.82) 0%, ${INK} / 0.66) 60%, ${INK} / 0) 100%)`,
  `radial-gradient(ellipse 26% 84px at 100% 100%, ${INK} / 0.7) 0%, ${INK} / 0.58) 55%, ${INK} / 0) 100%)`,
].join(", ");

/** The band's picture: wide art; else the tall cover blurred into a wash; else a calm gradient on the game's hue. */
function BandArt({ job }: { job: Pick<JobInfo, "appName" | "title"> }) {
  const game = useGame(job.appName);
  const wide = game?.art.wide ?? null;
  const tall = game?.art.tall ?? game?.art.tallThumb ?? null;
  const placeholder = game?.art.placeholder ?? undefined;
  if (wide) return <div aria-hidden className="absolute inset-0 bg-cover bg-center" style={{ backgroundImage: `url("${wide}")`, backgroundColor: placeholder }} />;
  if (tall) {
    // Scaled past the edges so the blur doesn't vignette; static, painted once.
    return (
      <div aria-hidden className="absolute inset-0 overflow-hidden" style={{ backgroundColor: placeholder }}>
        <div className="absolute -inset-10 scale-110 bg-cover bg-center opacity-90 blur-2xl" style={{ backgroundImage: `url("${tall}")` }} />
      </div>
    );
  }
  const h = hueOf(job.title);
  return <div aria-hidden className="absolute inset-0" style={{ background: `linear-gradient(115deg, oklch(0.46 0.08 ${h}) 0%, oklch(0.3 0.05 ${h}) 55%, oklch(0.22 0.03 ${h}) 100%)` }} />;
}

export function Band({ job, titleId, lead, percent, controls, children }: { job: Pick<JobInfo, "appName" | "title">; titleId: string; lead: ReactNode; percent?: ReactNode; controls?: ReactNode; children?: ReactNode }) {
  return (
    <div data-theme="dark" data-band className="relative h-[148px] overflow-hidden">
      <BandArt job={job} />
      <div aria-hidden className="absolute inset-0" style={{ background: SHADES }} />
      <div className="absolute inset-x-5 top-4 flex items-start justify-between gap-4">
        <h2 id={titleId} className="max-w-[46%] truncate text-md font-semibold leading-6 tracking-tight text-white" title={job.title}>
          {job.title}
        </h2>
        {controls}
      </div>
      <div className="absolute inset-x-5 bottom-[18px] flex items-baseline justify-between gap-6">
        <div className="min-w-0">{lead}</div>
        {percent}
      </div>
      {children}
    </div>
  );
}

/** Icon controls on the art: one dark glass square each, white glyph (≥ 3:1 on any art). Pause, Resume and Cancel share it, so the band never carries a bright primary. */
const GLASS_FILL = { backgroundColor: `${INK} / 0.58)`, color: "white" };
const GLASS = "border border-white/15 backdrop-blur-md hover:brightness-125";

export function BandControls({ job, started }: { job: JobInfo; started: boolean }) {
  const busy = usePending(`job:${job.id}`);
  const openDialog = useUi((s) => s.openDialog);
  const paused = job.status === "paused";
  // Held work can be paused once it has begun ("don't continue on your own"); a job that never started has nothing to pause.
  const canPause = PAUSABLE_KINDS.includes(job.kind) && (paused || job.status === "running" || ((job.status === "waiting" || job.status === "queued") && started));
  return (
    <div className="-mr-1.5 -mt-1 flex shrink-0 items-center gap-1.5">
      {canPause &&
        (paused ? (
          <IconButton label="Resume" disabled={busy} className={GLASS} style={GLASS_FILL} onClick={() => void actions.resumeJob(job.id)}>
            <Play size={14} fill="currentColor" strokeWidth={0} />
          </IconButton>
        ) : (
          <IconButton label="Pause" disabled={busy} className={GLASS} style={GLASS_FILL} onClick={() => void actions.pauseJob(job.id)}>
            <Pause size={14} fill="currentColor" strokeWidth={0} />
          </IconButton>
        ))}
      {job.cancellable && (
        <IconButton label="Cancel download" className={GLASS} style={GLASS_FILL} onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
          <X size={15} />
        </IconButton>
      )}
    </div>
  );
}

/** "1 h 12 min left": numbers carry the weight, units and "left" stay quiet. A phrase with no digits ("Under a minute") is set whole. */
export function TimeLead({ text }: { text: string }) {
  const numeric = /\d/.test(text);
  if (!numeric) {
    return (
      <p className="text-xl font-semibold leading-none tracking-tight text-white">
        {text}
        <span className="font-medium text-white/70"> left</span>
      </p>
    );
  }
  return (
    <p className="whitespace-nowrap leading-none tabular-nums">
      {text
        .split(/(\d+)/)
        .filter(Boolean)
        .map((s, i) => (
          <span key={i} className={cx(/^\d+$/.test(s) ? "text-2xl font-semibold tracking-tighter text-white" : "text-lg font-medium text-white/70")}>
            {s}
          </span>
        ))}
      <span className="text-lg font-medium text-white/70"> left</span>
    </p>
  );
}

/** The state in words, when there is no time to give: "Paused", "Waiting for Fortnite to close", "Ready to play". */
export function StateLead({ text }: { text: string }) {
  return <p className="truncate text-xl font-semibold leading-none tracking-tight text-white">{text}</p>;
}
