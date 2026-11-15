// Direction A, "Tuned": today's card with its structure kept (header, figures, bar, live speed, chart always on screen) and
// what's broken fixed: time left leads, no stepper, one speed with its cause beside it, the chart drawn against the connection.
// Scratch mockup; see docs/design-download-card.md.
import { Info } from "lucide-react";
import { JobArt } from "../../src/features/downloads/JobArt";
import { ProgressBar, cx } from "../../src/ui";
import { CancelButton, PauseButton, SpeedChart, StateLead, TimeLead, useCard } from "./parts";
import { speedText } from "./model";

export function CardA({ job }) {
  const { v, visible, focused } = useCard(job);
  const chartKind = v.running && (v.phase === "downloading" || v.phase === "repairing") ? "network" : v.running && v.phase === "installing" ? "disk" : null;
  const [num, unit] = (v.speedLabel ?? "").split(" ");
  return (
    <section className="@container rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
      <header className="flex items-start gap-4 p-5 pb-0">
        <JobArt job={job} size="lg" />
        <div className="min-w-0 flex-1 pt-0.5">
          <h2 className="truncate text-md font-semibold tracking-tight text-fg">{job.title}</h2>
          <p className="mt-1 truncate text-sm text-fg-muted">
            <span className="font-medium text-fg">{v.paused ? "Paused" : v.waiting ? "Waiting" : v.stepName}</span>
            <span> · {v.meta}</span>
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <PauseButton job={job} />
          <CancelButton job={job} />
        </div>
      </header>

      <div className="px-5 pt-5">
        <div className="flex items-baseline justify-between gap-4">
          {v.lead.kind === "time" ? <TimeLead text={v.lead.text} /> : <StateLead text={v.lead.text} />}
          {v.bytes && (
            <p className="shrink-0 text-sm tabular-nums text-fg-muted">
              <span className="text-fg">{v.bytes.split(" of ")[0]}</span> of {v.bytes.split(" of ")[1]}
            </p>
          )}
        </div>
        <ProgressBar className="mt-3" size="lg" value={v.started ? v.percent : 0} tone={v.paused || v.waiting ? "neutral" : "accent"} paused={v.paused || v.waiting} speed={v.speed} still={!focused} label={`${v.stepName} ${job.title}`} />
      </div>

      {v.running && v.speedLabel && (
        <div className="mt-5 flex items-end justify-between gap-6 px-5">
          <div className="flex items-baseline gap-4">
            <p className="text-lg font-semibold leading-7 tracking-tight tabular-nums text-fg">
              {num}
              <span className="ml-1 text-sm font-medium text-fg-subtle">{unit}</span>
            </p>
            {v.cause && (
              <button type="button" className={cx("-ml-1 inline-flex items-center gap-1.5 rounded-md px-1.5 py-0.5 text-sm text-fg hover:bg-surface-hover")}>
                <Info size={14} aria-hidden className="text-fg-muted" />
                {v.cause.short}
              </button>
            )}
          </div>
          <span className="pb-1 text-sm tabular-nums text-fg-muted">{v.percent}%</span>
        </div>
      )}
      {chartKind && (
        <div className="mt-2 px-5">
          <SpeedChart job={job} active={visible} kind={chartKind} ceiling={chartKind === "network" && v.cause ? v.ceiling : null} ceilingLabel={`Your connection, up to ${speedText(v.ceiling)}`} plotClassName="h-14" />
        </div>
      )}
      {(v.paused || v.waiting) && <p className="px-5 pt-3 text-sm text-fg-muted">{v.waiting ? v.blocked : v.etaAtPace ? `About ${v.etaAtPace} left when you resume.` : null}</p>}
      <div className="h-5" />
    </section>
  );
}
