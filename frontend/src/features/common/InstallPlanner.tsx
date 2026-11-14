import { useEffect, useRef, useState } from "react";
import { FolderOpen, HardDrive } from "lucide-react";
import type { InstallPlan } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { useGame } from "../../state/library";
import { driveOf, formatBytes } from "../../state/format";
import { Button, Skeleton, cx } from "../../ui";
import { PathText } from "./PathText";

function Stat({ label, children, tone }: { label: string; children: React.ReactNode; tone?: "danger" }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs text-fg-muted">{label}</dt>
      <dd className={cx("mt-0.5 text-md font-semibold tabular-nums", tone === "danger" ? "text-danger-text" : "text-fg")}>{children}</dd>
    </div>
  );
}

/**
 * Where it goes and whether it fits, from the backend's real plan (sizes, free space), not guesses.
 * Used by onboarding's fresh-install path and by the Library install dialog.
 */
export function InstallPlanner({
  appName,
  onPlan,
  onUseExisting,
}: {
  appName: string;
  /** Reports the current plan upward (null while loading) so the parent can enable its Install button. */
  onPlan: (plan: InstallPlan | null) => void;
  /** Offered when the chosen folder already holds a valid install. */
  onUseExisting?: (gamePath: string) => void;
}) {
  const title = useGame(appName)?.title ?? "This game";
  const [base, setBase] = useState<string | null>(null);
  // A plan answers a request; while the newest request has no answer we are loading.
  const [request, setRequest] = useState(0);
  const [answer, setAnswer] = useState<{ request: number; plan: InstallPlan | null } | null>(null);
  const plan = answer?.plan ?? null;
  const loading = answer?.request !== request;
  const report = useRef(onPlan);
  useEffect(() => {
    report.current = onPlan;
  });

  useEffect(() => {
    let alive = true;
    void actions.planInstall(appName, base).then((p) => {
      if (!alive) return;
      setAnswer({ request, plan: p ?? null });
      report.current(p ?? null);
    });
    return () => {
      alive = false;
    };
  }, [appName, base, request]);

  const change = async () => {
    const picked = await actions.pickFolder(plan?.basePath ?? null, `Choose where to install ${title}`);
    if (!picked) return;
    onPlan(null);
    setBase(picked);
    setRequest((n) => n + 1);
  };

  const drive = driveOf(plan?.basePath);
  const needed = plan?.installSizeBytes ?? 0;
  const free = plan?.freeBytes ?? 0;
  const enough = plan?.hasEnoughSpace ?? true;
  const fill = free > 0 ? Math.min(1, needed / free) : 1;

  return (
    <div className="flex flex-col gap-4">
      <div className="flex items-center gap-3 rounded-xl border border-border-strong bg-surface-1 p-3 pl-4">
        <span className="grid size-9 shrink-0 place-items-center rounded-lg bg-surface-2 text-fg-muted">
          <FolderOpen size={17} />
        </span>
        <div className="min-w-0 flex-1">
          <p className="text-xs text-fg-muted">Install location</p>
          {plan ? <PathText path={plan.gamePath} className="mt-0.5 text-sm font-medium text-fg" /> : <Skeleton className="mt-1.5 h-4 w-56" />}
        </div>
        <Button variant="secondary" onClick={() => void change()}>
          Change…
        </Button>
      </div>

      <div className="rounded-xl border border-border bg-surface-1 p-4">
        <dl className="grid grid-cols-3 gap-4">
          <Stat label="Download">{plan && !loading ? formatBytes(plan.downloadSizeBytes) : <Skeleton className="mt-1 h-5 w-16" />}</Stat>
          <Stat label="Space needed">{plan && !loading ? formatBytes(plan.installSizeBytes) : <Skeleton className="mt-1 h-5 w-16" />}</Stat>
          <Stat label={drive ? `Free on ${drive}` : "Free space"} tone={plan && !enough ? "danger" : undefined}>
            {plan && !loading ? formatBytes(plan.freeBytes) : <Skeleton className="mt-1 h-5 w-16" />}
          </Stat>
        </dl>
        <div
          role="img"
          aria-label={plan ? `${formatBytes(needed)} of ${formatBytes(free)} free space` : "Checking free space"}
          className="mt-4 h-1.5 overflow-hidden rounded-full bg-surface-3"
        >
          <div
            className={cx("h-full w-full origin-left rounded-full transition-[transform,background-color] duration-300 ease-out", enough ? "bg-accent" : "bg-danger")}
            style={{ transform: `scaleX(${plan && !loading ? fill : 0})` }}
          />
        </div>
        <p className={cx("mt-2.5 flex items-center gap-1.5 text-xs", plan && !enough ? "text-danger-text" : "text-fg-muted")} aria-live="polite">
          <HardDrive size={13} className="shrink-0" />
          {!plan || loading
            ? "Checking free space…"
            : enough
              ? `Fits with ${formatBytes(free - needed)} to spare`
              : `Needs ${formatBytes(needed)}, but only ${formatBytes(free)} is free. Free up space or choose another drive.`}
        </p>
      </div>

      {plan?.existingInstallFound && onUseExisting && (
        <div className="flex items-center gap-3 rounded-xl border border-accent/30 bg-accent-soft p-3 pl-4">
          <p className="min-w-0 flex-1 text-sm text-fg">{title} is already in this folder. Use it instead of downloading again.</p>
          <Button variant="secondary" onClick={() => onUseExisting(plan.gamePath)}>
            Use existing
          </Button>
        </div>
      )}
    </div>
  );
}
