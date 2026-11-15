import { useCallback, useEffect, useId, useState } from "react";
import { Collapsible } from "radix-ui";
import { ChevronDown, Gauge, Play } from "lucide-react";
import type { JobInfo } from "../../ipc/contract.gen";
import { actions, pendingKey } from "../../state/actions";
import { useWindowVisible } from "../../state/hooks";
import { useJobProgressThrottled } from "../../state/progress";
import { goToSetting } from "../../state/settingsFocus";
import { useApp, useSnap } from "../../state/store";
import { cardOf, readyOf, taskOf, type CardView } from "../../state/transfer";
import { usePending } from "../../state/ui";
import { Button, ProgressBar, cx } from "../../ui";
import { PathText } from "../common/PathText";
import { Band, BandControls, StateLead, TimeLead } from "./CardBand";
import { JobChart } from "./JobChart";

/** The card's surface: the raised container, clipped so the band's art takes its corners. */
const SURFACE = "overflow-hidden rounded-lg border border-border-strong bg-surface-2 [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]";
const FADE = "motion-safe:animate-[g-fade-in_180ms_var(--ease-out)]";

// ------------------------------------------------------------------------------------------------ remembered Details

const DETAILS_KEY = "glider.downloads.details";

/** Details stays as the player left it (per viewer). Storage can be blocked or missing: the card works without it. */
function useDetailsOpen(): [boolean, (open: boolean) => void] {
  const [open, setOpen] = useState(() => {
    try {
      return localStorage.getItem(DETAILS_KEY) === "1";
    } catch {
      return false;
    }
  });
  const set = useCallback((next: boolean) => {
    setOpen(next);
    try {
      localStorage.setItem(DETAILS_KEY, next ? "1" : "0");
    } catch {
      /* not remembered, still works */
    }
  }, []);
  return [open, set];
}

// ------------------------------------------------------------------------------------------------ screen reader

/**
 * What assistive tech hears, politely and rarely: when the step, a 10 % step, a hold, a pause or the cause changes, never at
 * progress rate. The time left rides along at each of those moments instead of ticking down on its own.
 */
function announcement(job: JobInfo, v: CardView): string {
  if (v.paused) return `${job.title} paused.`;
  if (v.held) return `${v.held.lead}. ${job.title} continues on its own.`;
  if (!v.moving || !v.started) return `${v.stepName} ${job.title}.`;
  return `${v.stepName} ${job.title}, ${Math.floor(v.percent / 10) * 10} percent${v.etaWords ? `, ${v.etaWords} left` : ""}.${v.cause ? ` ${v.cause.label}.` : ""}`;
}

function Announcer({ job, view }: { job: JobInfo; view: CardView }) {
  const key = [job.status, view.held?.lead, view.phase, Math.floor(view.percent / 10), view.cause?.label].join("|");
  // Held in state and recomposed only when the key moves, so the second-by-second numbers never re-announce.
  const [said, setSaid] = useState({ key: "", text: "" });
  if (said.key !== key) setSaid({ key, text: announcement(job, view) });
  return (
    <span className="sr-only" role="status" aria-live="polite">
      {said.text}
    </span>
  );
}

// ------------------------------------------------------------------------------------------------ the card

export interface ActiveJobCardProps {
  job: JobInfo;
  className?: string;
}

/**
 * The running job (docs/design-download-card.md §7): the game's wide art as a band with the title and the icon controls on top,
 * the time left (the headline) and the percent underneath, the whole-job bar on its bottom edge. Under it one line for the
 * current step, ending in the disclosure that names what limits the speed (or says "Details"); open, it holds why, the chart
 * against the connection's ceiling, what to do and the task. A finished job stays here as "Ready to play" until the page moves on.
 */
export function ActiveJobCard({ job, className }: ActiveJobCardProps) {
  return job.status === "completed" ? <ReadyCard job={job} className={className} /> : <LiveCard job={job} className={className} />;
}

function LiveCard({ job, className }: ActiveJobCardProps) {
  const titleId = useId();
  const visible = useWindowVisible();
  // The one thing that keeps moving on a long download is the bar's sheen, and only while someone could be looking at it.
  const focused = useApp((s) => s.window.focused);
  // The figures are read, not watched: 2 Hz, and not at all while the window is hidden. The bar feeds on its own.
  const p = useJobProgressThrottled(job.id, job.progress, visible);
  const speedLimit = useSnap((s) => s.settings.speedLimitBytesPerSecond);
  const chosenMode = useSnap((s) => s.settings.downloadMode);
  const effectiveMode = useSnap((s) => s.queue.effectiveMode);
  const modeReason = useSnap((s) => s.queue.modeReason);
  const hold = useSnap((s) => s.queue.hold);
  const [open, setOpen] = useDetailsOpen();
  const v = cardOf(job, p, { speedLimitBytesPerSecond: speedLimit, path: job.targetPath, chosenMode, effectiveMode, modeReason, hold });
  // A bottleneck outside the player's control (drive, processor, server) is amber; a limit they chose reads neutral.
  const fault = v.cause?.status.kind === "slow";
  const stopped = v.paused || !!v.held;
  const why = v.cause?.explain ?? null;
  const showTime = v.lead.kind === "time";

  return (
    <section aria-labelledby={titleId} className={cx("@container", className)}>
      <Announcer job={job} view={v} />
      <Collapsible.Root open={open} onOpenChange={setOpen} className={SURFACE}>
        <div className="relative">
          <Band
            job={job}
            titleId={titleId}
            lead={
              <div key={showTime ? "time" : v.lead.text} className={FADE}>
                {v.lead.kind === "time" ? <TimeLead text={v.lead.text} /> : <StateLead text={v.lead.text} />}
              </div>
            }
            percent={v.started && <p aria-hidden className="shrink-0 text-sm font-medium tabular-nums text-white">{v.percent}%</p>}
            controls={<BandControls job={job} started={p !== null && v.started} />}
          />
          <div className="absolute inset-x-0 bottom-0 bg-white/10">
            <ProgressBar
              className="rounded-none"
              size="sm"
              value={v.indeterminate ? null : v.started ? v.percent : 0}
              tone={stopped ? "neutral" : "accent"}
              paused={stopped}
              speed={v.speed}
              still={!focused}
              label={`${v.stepName} ${job.title}`}
              valueLabel={v.started ? `${v.percent} percent${v.etaWords ? `, ${v.etaWords} left` : ""}` : undefined}
            />
          </div>
        </div>

        <div className="flex h-[52px] items-center justify-between gap-4 px-5">
          <p key={`${v.paused}-${v.held?.lead}-${v.phase}`} className={cx("min-w-0 truncate text-sm tabular-nums text-fg-muted", FADE)}>
            {v.line.map((s, i) => (
              <span key={i}>
                {i > 0 && " · "}
                {s}
              </span>
            ))}
            {v.extra && <span className="text-fg"> · {v.extra}</span>}
          </p>
          <Collapsible.Trigger
            className={cx(
              "group/t -mr-2 inline-flex shrink-0 items-center gap-1.5 rounded-md px-2 py-1 text-xs font-medium transition-colors duration-150",
              fault ? "text-warning-text hover:bg-warning-soft data-[state=open]:bg-warning-soft" : v.cause ? "text-fg hover:bg-surface-hover" : "text-fg-muted hover:bg-surface-hover hover:text-fg data-[state=open]:text-fg",
            )}
          >
            {v.cause && <Gauge size={14} aria-hidden className={fault ? "text-warning-text" : "text-fg-muted"} />}
            {v.cause ? v.cause.label : "Details"}
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
            {v.chart && (
              <div>
                <p className="mb-1 text-xs text-fg-muted">{v.chart === "network" ? "Download speed" : "Writing to disk"}</p>
                <JobChart jobId={job.id} kind={v.chart} ceiling={v.ceiling} active={visible && open} />
              </div>
            )}
            {why && (why.advice.length > 0 || why.link) && (
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
                  <Button size="sm" variant="secondary" onClick={() => goToSetting(why.link!.focus)}>
                    {why.link.label}
                  </Button>
                )}
              </div>
            )}
            <dl className="grid grid-cols-[minmax(0,10rem)_1fr] items-center gap-x-6 gap-y-2 text-sm">
              <dt className="text-fg-muted">Task</dt>
              <dd className="min-w-0 text-fg">{job.kind === "move" && job.targetPath ? <span className="flex min-w-0 gap-1">Move to <PathText path={job.targetPath} /></span> : <span className="block truncate">{taskOf(job)}</span>}</dd>
              {v.held?.setting && (
                <>
                  <dt className="text-fg-muted">While a game is running</dt>
                  <dd className="flex items-center justify-between gap-4 text-fg">
                    Pause downloads
                    <Button size="sm" variant="secondary" onClick={() => goToSetting(v.held!.setting!)}>
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

// ------------------------------------------------------------------------------------------------ ready

/**
 * The end state: the band stays, the headline says what happened, and an install or update offers Play (labelled: it is the
 * payoff, so it keeps the one bright button the band ever carries). The card leaves when the next job starts, the page is left
 * or `READY_MS` pass (`Downloads`).
 */
function ReadyCard({ job, className }: ActiveJobCardProps) {
  const titleId = useId();
  const r = readyOf(job);
  const launching = usePending(pendingKey("launch", job.appName));
  return (
    <section aria-labelledby={titleId} className={cx("@container", className)}>
      <ReadyAnnouncer text={`${job.title}: ${r.lead}.`} />
      <div className={SURFACE}>
        <div className="relative">
          <Band
            job={job}
            titleId={titleId}
            lead={<StateLead text={r.lead} />}
            controls={
              r.play ? (
                <Button variant="primary" className="-mr-1.5 -mt-1" loading={launching} icon={<Play size={14} fill="currentColor" strokeWidth={0} />} onClick={() => void actions.launch(job.appName)}>
                  Play
                </Button>
              ) : undefined
            }
          />
          <div className="absolute inset-x-0 bottom-0 bg-white/10">
            <ProgressBar className="rounded-none" size="sm" value={100} label={`${job.title} finished`} />
          </div>
        </div>
        <div className="flex h-[52px] items-center px-5">
          <p className={cx("truncate text-sm text-fg-muted", FADE)}>{r.line}</p>
        </div>
      </div>
    </section>
  );
}

/** Said once, after mount (a live region that arrives with its text is often not read). */
function ReadyAnnouncer({ text }: { text: string }) {
  const [said, setSaid] = useState("");
  useEffect(() => {
    const id = setTimeout(() => setSaid(text), 50);
    return () => clearTimeout(id);
  }, [text]);
  return (
    <span className="sr-only" role="status" aria-live="polite">
      {said}
    </span>
  );
}
