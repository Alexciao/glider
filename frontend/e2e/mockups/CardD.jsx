// Direction D, "B×C": B's information model and behaviour in C's look. Scratch mockup; spec in docs/design-download-card.md §7.
// The band is the whole job: the game's wide art, the title and two icon controls on its top row, the time left (the headline)
// and the percent on its bottom row, the bar on its bottom edge. The line under it is the current step and ends in B's
// disclosure (amber for a real bottleneck, neutral otherwise); the panel holds why, the chart, what to do, and the task field.
import { useState } from "react";
import { Collapsible } from "radix-ui";
import { ChevronDown, Gauge, Pause, Play, X } from "lucide-react";
import { PAUSABLE_KINDS } from "../../src/features/downloads/kindIcon";
import { actions } from "../../src/state/actions";
import { prettyVersion } from "../../src/state/format";
import { useGame } from "../../src/state/library";
import { goToSetting } from "../../src/state/settingsFocus";
import { KIND_NOUN } from "../../src/state/transfer";
import { usePending, useUi } from "../../src/state/ui";
import { Button, IconButton, ProgressBar, cx } from "../../src/ui";
import { hueOf } from "../../src/ui/Avatar";
import { whyOf } from "./CardB";
import { speedText } from "./model";
import { SpeedChart, TimeLead, useCard } from "./parts";

const INK = "oklch(0.15 0.006 265";
/**
 * Readability shades scoped to the text they carry, not the whole band: three soft ellipses anchored at the corners the text
 * sits in (title top-left, headline bottom-left, percent bottom-right). The two on the left overlap into one smooth column; the
 * rest of the art is untouched. Sized so every line keeps AA on flat white art (measured with e2e/mockups/contrast-card.mjs);
 * the title is capped at 46 % of the band so it never leaves its shade.
 */
const SHADES = [
  `radial-gradient(ellipse 80% 112px at 0% 0%, ${INK} / 0.8) 0%, ${INK} / 0.66) 60%, ${INK} / 0) 100%)`,
  `radial-gradient(ellipse 80% 120px at 0% 100%, ${INK} / 0.82) 0%, ${INK} / 0.66) 60%, ${INK} / 0) 100%)`,
  `radial-gradient(ellipse 26% 84px at 100% 100%, ${INK} / 0.7) 0%, ${INK} / 0.58) 55%, ${INK} / 0) 100%)`,
].join(", ");
function Shades() {
  return <div aria-hidden className="absolute inset-0" style={{ background: SHADES }} />;
}

/** The band's picture: wide art; else the tall cover blurred into a wash; else a calm gradient on the game's hue. */
function BandArt({ job }) {
  const game = useGame(job.appName);
  const noWide = new URLSearchParams(location.search).get("nowide") === "1";
  const wide = noWide ? null : (game?.art.wide ?? null);
  const tall = game?.art.tall ?? game?.art.tallThumb ?? null;
  if (wide) return <div aria-hidden className="absolute inset-0 bg-cover bg-center" style={{ backgroundImage: `url("${wide}")`, backgroundColor: game?.art.placeholder ?? undefined }} />;
  if (tall)
    return (
      <div aria-hidden className="absolute inset-0 overflow-hidden" style={{ backgroundColor: game?.art.placeholder ?? undefined }}>
        <div className="absolute -inset-10 scale-110 bg-cover bg-center opacity-90 blur-2xl" style={{ backgroundImage: `url("${tall}")` }} />
      </div>
    );
  const h = hueOf(job.title);
  return <div aria-hidden className="absolute inset-0" style={{ background: `linear-gradient(115deg, oklch(0.46 0.08 ${h}) 0%, oklch(0.3 0.05 ${h}) 55%, oklch(0.22 0.03 ${h}) 100%)` }} />;
}

/** Icon controls on the art: a dark glass square each, white glyph (≥ 3:1 on any art). Resume, the paused card's next step, is white. */
const GLASS = { backgroundColor: `${INK} / 0.58)`, color: "white" };
function BandControls({ job }) {
  const busy = usePending(`job:${job.id}`);
  const openDialog = useUi((s) => s.openDialog);
  const paused = job.status === "paused";
  const canPause = (job.status === "running" || paused) && PAUSABLE_KINDS.includes(job.kind);
  return (
    <div className="-mr-1.5 -mt-1 flex shrink-0 items-center gap-1.5">
      {canPause &&
        (paused ? (
          <IconButton label="Resume" disabled={busy} style={{ backgroundColor: "white", color: "oklch(0.17 0.006 265)", boxShadow: `0 0 0 1px ${INK} / 0.28), 0 1px 3px oklch(0 0 0 / 0.35)` }} onClick={() => void actions.resumeJob(job.id)}>
            <Play size={14} fill="currentColor" strokeWidth={0} />
          </IconButton>
        ) : (
          <IconButton label="Pause" disabled={busy} className="border border-white/15 backdrop-blur-md hover:brightness-125" style={GLASS} onClick={() => void actions.pauseJob(job.id)}>
            <Pause size={14} fill="currentColor" strokeWidth={0} />
          </IconButton>
        ))}
      {job.cancellable && (
        <IconButton label="Cancel download" className="border border-white/15 backdrop-blur-md hover:brightness-125" style={GLASS} onClick={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
          <X size={15} />
        </IconButton>
      )}
    </div>
  );
}

/** What kind of job this is, for the Task field in Details (it left the band). */
function taskOf(job) {
  if (job.kind === "install" && job.toVersion) return `Install, version ${prettyVersion(job.toVersion)}`;
  if (job.kind === "update" && job.fromVersion && job.toVersion) return `Update, ${prettyVersion(job.fromVersion)} → ${prettyVersion(job.toVersion)}`;
  if (job.kind === "move" && job.targetPath) return `Move to ${job.targetPath}`;
  if (job.kind === "verify") return "Verify files, repair what's damaged";
  return KIND_NOUN[job.kind];
}

function Band({ job, children, lead, right }) {
  return (
    <div data-theme="dark" data-band className="relative h-[148px] overflow-hidden">
      <BandArt job={job} />
      <Shades />
      <div className="absolute inset-x-5 top-4 flex items-start justify-between gap-4">
        <h2 className="max-w-[46%] truncate text-md font-semibold leading-6 tracking-tight text-white" title={job.title}>
          {job.title}
        </h2>
        {right}
      </div>
      <div className="absolute inset-x-5 bottom-[18px] flex items-baseline justify-between gap-6">
        <div className="min-w-0">{lead}</div>
        {children}
      </div>
    </div>
  );
}

/** The end state: the band stays, the headline says "Ready to play", Play (labelled: it's the payoff) replaces the controls. */
function Ready({ job }) {
  return (
    <section aria-label={`${job.title} is ready to play`} className="@container overflow-hidden rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
      <div className="relative">
        <Band
          job={job}
          lead={<p className="text-xl font-semibold leading-none tracking-tight text-white">Ready to play</p>}
          right={
            <Button variant="primary" className="-mr-1.5 -mt-1" icon={<Play size={14} fill="currentColor" strokeWidth={0} />}>
              Play
            </Button>
          }
        />
        <div className="absolute inset-x-0 bottom-0 bg-white/10">
          <ProgressBar className="rounded-none" size="sm" value={100} label={`${job.title} installed`} />
        </div>
      </div>
      <div className="flex h-[52px] items-center px-5">
        <p className="text-sm text-fg-muted">Installed in 28 min.</p>
      </div>
    </section>
  );
}

/** The headline: time left (numbers big, units quiet), or the state in words. Cross-fades when the state changes. */
function Lead({ v }) {
  return (
    <div key={v.lead.kind === "time" ? "time" : v.lead.text} className="motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]">
      {v.lead.kind === "time" ? <TimeLead text={v.lead.text} tone="white" /> : <p className="truncate text-xl font-semibold leading-none tracking-tight text-white">{v.lead.text}</p>}
    </div>
  );
}

export function CardD({ job }) {
  if (new URLSearchParams(location.search).get("done") === "1") return <Ready job={job} />;
  return <CardDLive job={job} />;
}

function CardDLive({ job }) {
  const { v, ctx, visible, focused } = useCard(job);
  const [open, setOpen] = useState(() => new URLSearchParams(location.search).get("details") === "1");
  const why = whyOf(v.cause, v, ctx);
  const chartKind = v.running && (v.phase === "downloading" || v.phase === "repairing") ? "network" : v.running && v.phase === "installing" ? "disk" : null;
  // A bottleneck outside the player's control (drive, processor, server) reads amber; a limit they chose reads neutral.
  const fault = v.cause?.kind === "slow";
  const stepLine = [v.step, v.bytes, v.speedLabel].filter(Boolean);
  const stopped = v.paused || v.waiting;
  const lead = <Lead v={v} />;

  return (
    <section aria-label={job.title} className="@container">
      <Collapsible.Root open={open} onOpenChange={setOpen} className="overflow-hidden rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
        <div className="relative">
          <Band job={job} lead={lead} right={<BandControls job={job} />}>
            {v.started && <p className="shrink-0 text-sm font-medium tabular-nums text-white">{v.percent}%</p>}
          </Band>
          <div className="absolute inset-x-0 bottom-0 bg-white/10">
            <ProgressBar
              className="rounded-none"
              size="sm"
              value={v.started ? v.percent : v.phase === "preparing" ? null : 0}
              tone={stopped ? "neutral" : "accent"}
              paused={stopped}
              speed={v.speed}
              still={!focused}
              label={`${v.stepName} ${job.title}`}
            />
          </div>
        </div>

        <div className="flex h-[52px] items-center justify-between gap-4 px-5">
          <p key={`${v.paused}-${v.waiting}-${v.stepName}`} className="min-w-0 truncate text-sm tabular-nums text-fg-muted motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]">
            {v.waiting ? (
              <>
                {v.blocked}
                {v.started && v.bytes && <> · {v.bytes}</>}
              </>
            ) : v.paused ? (
              <>
                {v.step ?? "Download"} paused{v.bytes && <> · {v.bytes}</>}
                {v.etaAtPace && <> · about {v.etaAtPace} left</>}
              </>
            ) : (
              stepLine.map((s, i) => (
                <span key={i}>
                  {i > 0 && " · "}
                  {s}
                </span>
              ))
            )}
            {v.extra && <span className="text-fg"> · {v.extra}</span>}
          </p>
          <Collapsible.Trigger
            className={cx(
              "group/t -mr-2 inline-flex shrink-0 items-center gap-1.5 rounded-md px-2 py-1 text-xs font-medium transition-colors duration-150",
              fault ? "text-warning-text hover:bg-warning-soft data-[state=open]:bg-warning-soft" : v.cause ? "text-fg hover:bg-surface-hover" : "text-fg-muted hover:bg-surface-hover hover:text-fg data-[state=open]:text-fg",
            )}
          >
            {v.cause && <Gauge size={14} aria-hidden className={fault ? "text-warning-text" : "text-fg-muted"} />}
            {v.cause ? v.cause.short : "Details"}
            <span className="sr-only">, show details</span>
            <ChevronDown size={14} aria-hidden className="-ml-0.5 transition-transform duration-200 ease-out group-data-[state=open]/t:rotate-180" />
          </Collapsible.Trigger>
        </div>

        <Collapsible.Content className="g-collapse overflow-hidden">
          <div className="mx-5 flex flex-col gap-4 border-t border-border pb-5 pt-4">
            {why && (
              <div className="max-w-[62ch]">
                <h3 className="text-sm font-semibold text-fg">{why.title}</h3>
                <p className="mt-1 text-sm leading-[1.45] text-fg-muted">{why.body}</p>
              </div>
            )}
            {chartKind && (
              <div>
                <p className="mb-1 text-xs text-fg-muted">{chartKind === "network" ? "Download speed" : "Writing to disk"}</p>
                <SpeedChart
                  job={job}
                  active={visible && open}
                  kind={chartKind}
                  ceiling={chartKind === "network" && v.cause && v.ceiling ? v.ceiling : null}
                  ceilingLabel={`Your connection, up to ${speedText(v.ceiling)}`}
                  plotClassName="h-16"
                />
              </div>
            )}
            {(why?.advice?.length > 0 || why?.link) && (
              <div className="flex flex-wrap items-end justify-between gap-x-8 gap-y-3">
                {why.advice.length > 0 && (
                  <ul className="flex max-w-[62ch] flex-col gap-1 text-sm leading-[1.45] text-fg-muted">
                    {why.advice.map((a) => (
                      <li key={a} className="flex gap-2">
                        <span aria-hidden className="mt-[0.55em] size-1 shrink-0 rounded-full bg-fg-subtle" />
                        {a}
                      </li>
                    ))}
                  </ul>
                )}
                {why.link && (
                  <Button size="sm" variant="secondary" onClick={() => goToSetting(why.link.focus)}>
                    {why.link.label}
                  </Button>
                )}
              </div>
            )}
            <dl className="grid grid-cols-[minmax(0,10rem)_1fr] items-center gap-x-6 gap-y-2 text-sm">
              <dt className="text-fg-muted">Task</dt>
              <dd className="min-w-0 truncate text-fg">{taskOf(job)}</dd>
              {v.held?.setting && (
                <>
                  <dt className="text-fg-muted">While a game is running</dt>
                  <dd className="flex items-center justify-between gap-4 text-fg">
                    Pause downloads
                    <Button size="sm" variant="secondary" onClick={() => goToSetting(v.held.setting)}>
                      Change
                    </Button>
                  </dd>
                </>
              )}
            </dl>
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
    </section>
  );
}
