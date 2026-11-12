import { CircleCheck, FolderInput, FolderOpen, MoreHorizontal, RefreshCw, Trash2, Wrench, TriangleAlert } from "lucide-react";
import type { AppSnapshot, InstallCandidate, JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { isBusy } from "../../state/derive";
import { buildOf, formatBytes, formatEta, formatPercent, formatSpeed, prettyVersion } from "../../state/format";
import { useJobProgress } from "../../state/progress";
import { useUi } from "../../state/ui";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  ProgressBar,
  cx,
} from "../../ui";
import { PathText } from "../common/PathText";

/** Live bar + numbers for the running job. Re-renders at 10 Hz; nothing else on the page does. */
export function JobLine({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const percent = p?.percent ?? 0;
  const paused = job.status === "paused";
  const downloading = p?.phase === "downloading";
  return (
    <div className="w-[min(440px,100%)]">
      <ProgressBar value={job.status === "running" && !p ? null : percent} tone={paused ? "warning" : "accent"} label={`${job.kind} progress`} />
      <div className="mt-2 flex items-center justify-between gap-4 text-xs text-fg-muted tabular-nums">
        <span>{p ? (downloading ? `${formatBytes(p.downloadedBytes)} of ${formatBytes(p.totalDownloadBytes)}` : formatPercent(percent)) : "Starting…"}</span>
        {p && !paused && job.status === "running" && (
          <span>
            {downloading ? `${formatSpeed(p.downloadBytesPerSecond)}  ·  ` : ""}
            {formatEta(p.etaSeconds)} left
          </span>
        )}
        {paused && <span>Paused at {formatPercent(percent)}</span>}
      </div>
    </div>
  );
}

function Fact({ label, children, grow }: { label: string; children: React.ReactNode; grow?: boolean }) {
  return (
    <div className={cx("min-w-0", grow && "flex-1")}>
      <dt className="text-xs text-fg-muted">{label}</dt>
      <dd className="mt-0.5 truncate text-sm font-medium text-fg">{children}</dd>
    </div>
  );
}

/** Quiet definition list pinned to the bottom of the hero. */
export function Facts({ snap }: { snap: AppSnapshot }) {
  const { game, egl, settings } = snap;
  const installed = game.installed;
  const items: React.ReactNode[] = [];

  if (installed) {
    const build = buildOf(installed.version);
    items.push(
      <Fact key="v" label="Version">
        <span className="tabular-nums">{prettyVersion(installed.version)}</span>
        {build && <span className="ml-1.5 text-xs font-normal text-fg-subtle tabular-nums">build {build}</span>}
      </Fact>,
      <Fact key="s" label="On disk">
        <span className="tabular-nums">{formatBytes(installed.installSizeBytes)}</span>
      </Fact>,
      <Fact key="l" label="Location" grow>
        <PathText path={installed.installPath} />
      </Fact>,
    );
    if (installed.sharedWithEgl) {
      const synced = settings.eglSync && egl.syncAvailable;
      items.push(
        <Fact key="e" label="Epic Games Launcher">
          <span className={cx("inline-flex items-center gap-1.5", synced ? "text-success-text" : "text-warning-text")}>
            {synced ? <CircleCheck size={14} /> : <TriangleAlert size={14} />}
            {synced ? "Shared, in sync" : "Shared, not syncing"}
          </span>
        </Fact>,
      );
    }
  } else if (game.downloadSizeBytes || game.installSizeBytes) {
    if (game.downloadSizeBytes) items.push(<Fact key="d" label="Download"><span className="tabular-nums">{formatBytes(game.downloadSizeBytes)}</span></Fact>);
    if (game.installSizeBytes) items.push(<Fact key="i" label="Disk space needed"><span className="tabular-nums">{formatBytes(game.installSizeBytes)}</span></Fact>);
    if (game.latestVersion) items.push(<Fact key="v" label="Latest version"><span className="tabular-nums">{prettyVersion(game.latestVersion)}</span></Fact>);
  }
  if (items.length === 0) return null;
  return <dl className="mt-8 flex items-start gap-x-9 border-t border-border pt-5 max-[1100px]:gap-x-6">{items}</dl>;
}

export function MoreMenu({ snap }: { snap: AppSnapshot }) {
  const openDialog = useUi((s) => s.openDialog);
  const busy = isBusy(snap);
  const installed = snap.game.installed !== null;
  const signedIn = snap.auth.status === "signedIn";
  if (!installed) return null;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant="secondary"
          aria-label="More actions"
          className="size-12 rounded-[14px] border-border-strong bg-surface-3 p-0 backdrop-blur-md"
        >
          <MoreHorizontal size={20} />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-60">
        <DropdownMenuItem icon={<Wrench size={15} />} disabled={busy || !signedIn} onSelect={() => void actions.verify()}>
          Verify and repair
        </DropdownMenuItem>
        <DropdownMenuItem icon={<RefreshCw size={15} />} disabled={busy || !signedIn} onSelect={() => void actions.checkForUpdates()}>
          Check for updates
        </DropdownMenuItem>
        <DropdownMenuItem icon={<FolderInput size={15} />} disabled={busy} onSelect={() => openDialog({ kind: "move" })}>
          Move install…
        </DropdownMenuItem>
        <DropdownMenuItem icon={<FolderOpen size={15} />} onSelect={() => void actions.openInstallFolder()}>
          Open install folder
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem icon={<Trash2 size={15} />} destructive disabled={busy} onSelect={() => openDialog({ kind: "uninstall" })}>
          Uninstall…
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** "Fortnite is already on this PC": offered on Library when nothing is installed but EGL has a usable copy. */
export function ExistingInstallHint({ candidate, pending }: { candidate: InstallCandidate; pending: boolean }) {
  return (
    <div className="mt-6 flex w-[min(560px,100%)] items-center gap-3 rounded-xl border border-border-strong bg-surface-2 p-3 pl-4 backdrop-blur-md">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-fg">Fortnite is already on this PC</p>
        <p className="mt-0.5 flex min-w-0 text-xs text-fg-muted">
          <PathText path={candidate.installPath} />
        </p>
      </div>
      <Button
        size="md"
        variant="secondary"
        loading={pending}
        onClick={() => void actions.adopt({ candidateId: candidate.id, path: null, verify: "auto" }, "hero")}
      >
        Use this install
      </Button>
    </div>
  );
}
