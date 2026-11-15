// Direction B, "Cover and one line" (recommended). Scratch mockup; spec in docs/design-download-card.md.
// Closed: cover, title, the time left as the lead, the whole-job bar, and one line for the current step whose right end is the
// disclosure: labelled with what limits the speed when something does, "Details" otherwise. Open: why, a chart drawn against
// the connection's ceiling, what to do.
import { useState } from "react";
import { Collapsible } from "radix-ui";
import { ChevronDown, Gauge, Play } from "lucide-react";
import { driveOf } from "../../src/state/format";
import { goToSetting } from "../../src/state/settingsFocus";
import { ProgressBar, Button, cx } from "../../src/ui";
import { CancelButton, Cover, PauseButton, SpeedChart, StateLead, TimeLead, useCard } from "./parts";
import { speedText } from "./model";

/** The explanation behind the disclosure, per cause. Copy in the spec. */
export function whyOf(cause, v, ctx) {
  if (!cause) return null;
  const drive = driveOf(ctx.path) || "your drive";
  switch (cause.cause) {
    case "disk":
      return {
        title: `Drive ${drive} is the limit`,
        body: "It can't write game files as fast as your connection delivers them, so Glider downloads at the pace the drive allows.",
        advice: [`Free up space on ${drive}. Drives slow down as they fill up.`, `Pause backups, scans or other downloads that use ${drive}.`, "Put big games on a faster drive. You can move this one later from its page."],
        link: null,
      };
    case "cpu":
      return { title: "The processor is busy", body: "Unpacking files needs more processor time than your PC has free right now. Speed comes back as it settles.", advice: ["Close heavy programs while this finishes."], link: null };
    case "cdn":
      return { title: "Epic's server is slow right now", body: "Glider switches to another server on its own if it stays slow.", advice: [], link: { focus: "cdn", label: "Choose a server" } };
    case "speedLimit":
      return { title: `Held to your ${speedText(ctx.speedLimitBytesPerSecond) ?? ""} limit`.replace("  ", " "), body: "Downloads stay under the limit you set, so the rest of your network keeps its share.", advice: [], link: { focus: "speedLimit", label: "Change limit" } };
    case "mode":
      return { title: "Background mode is on", body: "Glider downloads gently, with few connections and low priority, so games and apps stay smooth.", advice: [], link: { focus: "downloadMode", label: "Change mode" } };
    case "playing":
      return { title: "Slowed while you play", body: `${cause.reason} Full speed returns when the game closes.`, advice: [], link: { focus: "whilePlaying", label: "Change" } };
    default:
      return null;
  }
}

/** The end state: the card stays as "Ready to play" until the page is left or the next job starts. */
function Done({ job }) {
  return (
    <section aria-label={`${job.title} is ready to play`} className="@container">
      <div className="rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
        <div className="flex gap-5 p-5 @max-[620px]:gap-4 @max-[620px]:p-4">
          <Cover job={job} className="h-32 w-24 @max-[620px]:h-24 @max-[620px]:w-[72px]" />
          <div className="flex min-w-0 flex-1 flex-col">
            <header className="flex items-start justify-between gap-4">
              <div className="min-w-0 pt-0.5">
                <h2 className="truncate text-md font-semibold tracking-tight text-fg">{job.title}</h2>
                <p className="mt-0.5 truncate text-sm text-fg-muted">Installed · 1.2.4</p>
              </div>
              <div className="flex shrink-0 items-center gap-1">
                <Button variant="primary" icon={<Play size={14} fill="currentColor" strokeWidth={0} />}>Play</Button>
              </div>
            </header>
            <div className="mt-auto pt-4">
              <StateLead text="Ready to play" />
              <ProgressBar className="mt-3" size="md" value={100} label={`${job.title} installed`} />
              <div className="mt-2.5 flex min-h-7 items-center">
                <p className="text-sm text-fg-muted">Installed in 28 min.</p>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}

export function CardB({ job, defaultOpen = false }) {
  if (new URLSearchParams(location.search).get("done") === "1") return <Done job={job} />;
  return <CardBLive job={job} defaultOpen={defaultOpen} />;
}

function CardBLive({ job, defaultOpen }) {
  const { v, ctx, visible, focused } = useCard(job);
  const [open, setOpen] = useState(() => defaultOpen || new URLSearchParams(location.search).get("details") === "1");
  const why = whyOf(v.cause, v, ctx);
  const chartKind = v.phase === "downloading" || v.phase === "repairing" ? "network" : v.phase === "installing" ? "disk" : null;
  const toggleLabel = v.cause ? v.cause.short : "Details";
  const stepLine = [v.step, v.bytes, v.speedLabel].filter(Boolean);

  return (
    <section aria-label={`${job.title}, ${v.lead.text}${v.lead.kind === "time" ? " left" : ""}`} className="@container">
      <Collapsible.Root open={open} onOpenChange={setOpen} className="rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
        <div className="flex gap-5 p-5 @max-[620px]:gap-4 @max-[620px]:p-4">
          <Cover job={job} className="h-32 w-24 @max-[620px]:h-24 @max-[620px]:w-[72px]" />
          <div className="flex min-w-0 flex-1 flex-col">
            <header className="flex items-start justify-between gap-4">
              <div className="min-w-0 pt-0.5">
                <h2 className="truncate text-md font-semibold tracking-tight text-fg" title={job.title}>
                  {job.title}
                </h2>
                <p className="mt-0.5 truncate text-sm text-fg-muted">{v.meta}</p>
              </div>
              <div className="flex shrink-0 items-center gap-1">
                <PauseButton job={job} />
                <CancelButton job={job} />
              </div>
            </header>

            <div className="mt-auto pt-4">
              <div className="flex items-baseline justify-between gap-4">
                <div key={v.lead.kind === "time" ? "time" : v.lead.text} className="min-w-0 motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]">
                  {v.lead.kind === "time" ? <TimeLead text={v.lead.text} /> : <StateLead text={v.lead.text} />}
                </div>
                {v.started && <span className="shrink-0 text-sm tabular-nums text-fg-muted">{v.percent}%</span>}
              </div>
              <ProgressBar
                className="mt-3"
                size="md"
                value={v.started ? v.percent : v.phase === "preparing" ? null : 0}
                tone={v.waiting || v.paused ? "neutral" : "accent"}
                paused={v.paused || v.waiting}
                speed={v.speed}
                still={!focused}
                label={`${v.stepName} ${job.title}`}
              />
              <div className="mt-2.5 flex min-h-7 items-center justify-between gap-4">
                {v.waiting ? (
                  <p key="held" className="min-w-0 truncate text-sm text-fg-muted motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]">
                    {v.blocked}
                    {v.started && v.bytes && <span className="tabular-nums"> · {v.bytes}</span>}
                  </p>
                ) : (
                  <p key={`${v.paused}-${v.stepName}`} className="min-w-0 truncate text-sm tabular-nums text-fg-muted motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]">
                    {v.paused ? (
                      <>
                        {v.step} paused{v.bytes && <> · {v.bytes}</>}
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
                )}
                {v.held?.setting && (
                  <Button size="sm" variant="ghost" className="-mr-2" onClick={() => goToSetting(v.held.setting)}>
                    Change
                  </Button>
                )}
                {!v.waiting && (chartKind || why) && (
                  <Collapsible.Trigger
                    className={cx(
                      "group/t -mr-2 inline-flex shrink-0 items-center gap-1 rounded-md px-2 py-1 text-xs font-medium transition-colors duration-150",
                      v.cause ? "text-fg hover:bg-surface-hover" : "text-fg-muted hover:bg-surface-hover hover:text-fg data-[state=open]:text-fg",
                    )}
                  >
                    {v.cause && <Gauge size={14} aria-hidden className="text-fg-muted" />}
                    {toggleLabel}
                    <ChevronDown size={14} aria-hidden className="transition-transform duration-200 ease-out group-data-[state=open]/t:rotate-180" />
                  </Collapsible.Trigger>
                )}
              </div>
            </div>
          </div>
        </div>

        <Collapsible.Content className="g-collapse overflow-hidden">
          <div className="mx-5 border-t border-border pb-5 pt-4 @max-[620px]:mx-4">
            {why && (
              <div className="mb-4 max-w-[62ch]">
                <h3 className="text-sm font-semibold text-fg">{why.title}</h3>
                <p className="mt-1 text-sm leading-[1.45] text-fg-muted">{why.body}</p>
              </div>
            )}
            {chartKind && (
              <p className="mb-1 text-xs text-fg-muted">{chartKind === "network" ? "Download speed" : "Writing to disk"}</p>
            )}
            {chartKind && (
              <SpeedChart
                job={job}
                active={visible && open}
                kind={chartKind}
                ceiling={chartKind === "network" && v.cause && v.ceiling ? v.ceiling : null}
                ceilingLabel={`Your connection, up to ${speedText(v.ceiling)}`}
                plotClassName="h-16"
              />
            )}
            {(why?.advice?.length > 0 || why?.link) && (
              <div className="mt-4 flex flex-wrap items-end justify-between gap-x-8 gap-y-3">
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
          </div>
        </Collapsible.Content>
      </Collapsible.Root>
    </section>
  );
}
