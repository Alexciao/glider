// Direction C, "Hero band": the game's wide key art leads, with the title and the time left over it and the whole-job bar
// along its bottom edge; under the art, one line for the current step and, while something limits the speed, a headroom
// meter (what the download gets against what the connection has reached) instead of a time chart. Scratch mockup.
import { Info } from "lucide-react";
import { ProgressBar, cx } from "../../src/ui";
import { CancelButton, PauseButton, StateLead, TimeLead, useCard, useWideArt } from "./parts";
import { speedText } from "./model";

/** Dark scrims in both themes: text over arbitrary art only keeps AA against a dark, art-independent layer (as the game hero). */
const SCRIM =
  "linear-gradient(90deg, oklch(0.16 0.006 265 / 0.94) 0%, oklch(0.16 0.006 265 / 0.78) 38%, oklch(0.16 0.006 265 / 0.25) 75%, oklch(0.16 0.006 265 / 0.1) 100%), linear-gradient(0deg, oklch(0.16 0.006 265 / 0.85) 0%, oklch(0.16 0.006 265 / 0) 60%)";

function Headroom({ v }) {
  const share = v.ceiling ? Math.max(0.02, Math.min(1, (v.speed ?? 0) / v.ceiling)) : 1;
  return (
    <div className="mt-4">
      <div className="flex items-baseline justify-between gap-4 text-sm">
        <p className="font-medium text-fg">{v.cause.short}</p>
        <button type="button" className="-mr-1.5 inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-xs font-medium text-fg-muted hover:bg-surface-hover hover:text-fg">
          <Info size={13} aria-hidden />
          What can I do?
        </button>
      </div>
      <div className="mt-2.5 h-1.5 overflow-hidden rounded-full bg-surface-3" aria-hidden>
        <div className="h-full rounded-full bg-fg-muted" style={{ width: `${share * 100}%` }} />
      </div>
      <div className="mt-1.5 flex justify-between text-xs tabular-nums text-fg-muted">
        <span>
          <span className="text-fg">{v.speedLabel}</span> now
        </span>
        <span>Your connection has reached {speedText(v.ceiling)}</span>
      </div>
    </div>
  );
}

export function CardC({ job }) {
  const { v, focused } = useCard(job);
  const art = useWideArt(job);
  const url = art.wide ?? art.tall;
  const limited = v.running && v.cause && v.ceiling && v.speed && v.ceiling > v.speed * 1.25;
  return (
    <section className="@container overflow-hidden rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
      <div data-theme="dark" className="relative h-40 overflow-hidden @max-[620px]:h-36">
        <div aria-hidden className="absolute inset-0 bg-cover bg-center" style={{ backgroundColor: art.placeholder ?? undefined, backgroundImage: url ? `url("${url}")` : undefined }} />
        <div aria-hidden className="absolute inset-0" style={{ background: SCRIM }} />
        <div className="absolute right-4 top-4 flex items-center gap-1">
          <PauseButton job={job} className={v.paused ? undefined : "border-white/15 bg-black/35 text-white backdrop-blur-md hover:bg-black/50"} />
          <CancelButton job={job} className="text-white/80 hover:bg-black/35 hover:text-white" />
        </div>
        <div className="absolute inset-x-5 bottom-5 flex items-end justify-between gap-6">
          <div className="min-w-0">
            <p className="truncate text-xs font-medium text-white/75">{v.meta}</p>
            <h2 className="mt-0.5 truncate text-lg font-semibold tracking-tight text-white">{job.title}</h2>
          </div>
          <div className="shrink-0 text-right">
            {v.lead.kind === "time" ? <TimeLead text={v.lead.text} tone="white" /> : <StateLead text={v.lead.text} tone="white" />}
          </div>
        </div>
        <div className="absolute inset-x-0 bottom-0">
        <ProgressBar
          className="rounded-none bg-white/15"
          size="sm"
          value={v.started ? v.percent : 0}
          tone={v.paused || v.waiting ? "neutral" : "accent"}
          paused={v.paused || v.waiting}
          speed={v.speed}
          still={!focused}
          label={`${v.stepName} ${job.title}`}
        />
        </div>
      </div>
      <div className="px-5 py-4">
        <div className="flex items-center justify-between gap-4 text-sm text-fg-muted">
          <p className={cx("min-w-0 truncate tabular-nums")}>
            {v.waiting ? v.blocked : v.paused ? [`${v.step ?? "Download"} paused`, v.bytes, v.etaAtPace && `about ${v.etaAtPace} left`].filter(Boolean).join(" · ") : [v.step, v.bytes, !limited && v.speedLabel].filter(Boolean).join(" · ")}
            {v.extra && <span className="text-fg"> · {v.extra}</span>}
          </p>
          {v.started && <span className="shrink-0 tabular-nums">{v.percent}%</span>}
        </div>
        {limited && <Headroom v={v} />}
      </div>
    </section>
  );
}
