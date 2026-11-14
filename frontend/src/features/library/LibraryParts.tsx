import { CircleCheck, EyeOff, FolderInput, FolderOpen, MoreHorizontal, RefreshCw, Star, Trash2, TriangleAlert, Wrench } from "lucide-react";
import type { GameSummary, JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { PHASE_LABEL, isBusy, jobVerb } from "../../state/derive";
import { buildOf, formatBytes, formatEta, formatPercent, formatPlaytime, formatRelativeTime, formatSpeed, prettyVersion } from "../../state/format";
import { useJobProgress } from "../../state/progress";
import { useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  ProgressBar,
  Tooltip,
  cx,
} from "../../ui";
import { PathText } from "../common/PathText";
import { heroButtonBase, heroButtonTone, heroRadius } from "./PrimaryAction";

/** Live bar + numbers for the running job. Re-renders at 10 Hz; nothing else on the page does. */
export function JobLine({ job }: { job: JobInfo }) {
  const p = useJobProgress(job.id, job.progress);
  const percent = p?.percent ?? 0;
  const paused = job.status === "paused";
  const net = p?.network ?? null;
  const verify = p?.verify ?? null;
  const left = p
    ? net
      ? `${formatBytes(net.downloadedBytes)} of ${formatBytes(net.totalBytes)}`
      : verify && p.phase === "verifying"
        ? `${verify.filesChecked.toLocaleString()} of ${verify.filesTotal.toLocaleString()} files${verify.badFiles > 0 ? `  ·  ${verify.badFiles} to fix` : ""}`
        : `${PHASE_LABEL[p.phase]} · ${formatPercent(percent)}`
    : "Starting…";
  const right = net ? formatSpeed(net.bytesPerSecond) : verify && p?.phase === "verifying" ? formatSpeed(verify.readBytesPerSecond) : null;
  return (
    <div className="w-[min(440px,100%)]">
      <ProgressBar
        value={job.status === "running" && !p ? null : percent}
        tone={paused ? "warning" : "accent"}
        paused={paused}
        speed={net ? net.bytesPerSecond : null}
        label={`${jobVerb(job.kind, p?.phase ?? job.phase)} ${job.title}`}
      />
      <div className="mt-2 flex items-center justify-between gap-4 text-xs text-fg-muted tabular-nums">
        <span>{left}</span>
        {p && !paused && job.status === "running" && (
          <span>
            {right ? `${right}  ·  ` : ""}
            {formatEta(p.etaSeconds)} left
          </span>
        )}
        {paused && <span>Paused at {formatPercent(percent)}</span>}
      </div>
    </div>
  );
}

function Fact({ label, children, grow, hint }: { label: string; children: React.ReactNode; grow?: boolean; hint?: string }) {
  return (
    <div className={cx("min-w-0 max-w-full", grow ? "min-w-[220px] flex-1" : "shrink-0")}>
      <dt className="text-xs text-fg-muted">
        {hint ? (
          <Tooltip content={hint} side="bottom" align="start">
            <span className="cursor-help">{label}</span>
          </Tooltip>
        ) : (
          label
        )}
      </dt>
      <dd className="mt-0.5 truncate text-sm font-medium text-fg">{children}</dd>
    </div>
  );
}

/** Quiet definition list pinned to the bottom of the hero. */
export function Facts({ game }: { game: GameSummary }) {
  const eglSync = useSnap((s) => s.settings.eglSync);
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const items: React.ReactNode[] = [];

  if (installed) {
    const build = buildOf(game.installedVersion);
    items.push(
      <Fact key="v" label="Version">
        <span className="tabular-nums">{prettyVersion(game.installedVersion)}</span>
        {build && <span className="ml-1.5 text-xs font-normal text-fg-subtle tabular-nums">build {build}</span>}
      </Fact>,
      <Fact key="s" label="Installed size" hint="Space the game files take on your drive right now, measured from disk.">
        {game.installedSizeBytes == null ? (
          <span className="font-normal text-fg-muted">Measuring…</span>
        ) : (
          <span className="tabular-nums">{formatBytes(game.installedSizeBytes)}</span>
        )}
      </Fact>,
    );
    if (game.updateAvailable && game.downloadSizeBytes) {
      items.push(
        <Fact key="d" label="Download size" hint="How much data the pending update downloads. Smaller than the installed size because only changed files are fetched.">
          <span className="tabular-nums">{formatBytes(game.downloadSizeBytes)}</span>
        </Fact>,
      );
    }
    items.push(
      <Fact key="p" label="Playtime" hint="Only counts time played through Glider, not in the Epic Games Launcher or before Glider.">
        <span className="tabular-nums">{game.playtimeSeconds > 0 ? formatPlaytime(game.playtimeSeconds) : "Not played yet"}</span>
      </Fact>,
    );
    if (game.lastPlayedAt) {
      items.push(
        <Fact key="lp" label="Last played">
          <span>{formatRelativeTime(game.lastPlayedAt)}</span>
        </Fact>,
      );
    }
    if (game.installPath) {
      items.push(
        <Fact key="l" label="Location" grow>
          <PathText path={game.installPath} />
        </Fact>,
      );
    }
    if (game.sharedWithEgl) {
      items.push(
        <Fact key="e" label="Epic Games Launcher">
          <span className={cx("inline-flex items-center gap-1.5", eglSync ? "text-success-text" : "text-warning-text")}>
            {eglSync ? <CircleCheck size={14} /> : <TriangleAlert size={14} />}
            {eglSync ? "Shared, in sync" : "Shared, not syncing"}
          </span>
        </Fact>,
      );
    }
  } else if (game.downloadSizeBytes || game.installSizeBytes) {
    if (game.downloadSizeBytes) items.push(<Fact key="d" label="Download size" hint="How much data is downloaded to install the game."><span className="tabular-nums">{formatBytes(game.downloadSizeBytes)}</span></Fact>);
    if (game.installSizeBytes) items.push(<Fact key="i" label="Installed size" hint="Space the game will take on your drive once installed."><span className="tabular-nums">{formatBytes(game.installSizeBytes)}</span></Fact>);
    if (game.latestVersion) items.push(<Fact key="v" label="Latest version"><span className="tabular-nums">{prettyVersion(game.latestVersion)}</span></Fact>);
  }
  if (game.developer) items.push(<Fact key="dev" label="Developer">{game.developer}</Fact>);
  if (items.length === 0) return null;
  return <dl className="mt-8 flex flex-wrap items-start gap-x-9 gap-y-4 border-t border-border pt-5 max-[1100px]:mt-6 max-[1100px]:gap-x-6">{items}</dl>;
}

export function MoreMenu({ game, primary }: { game: GameSummary; primary: boolean }) {
  const openDialog = useUi((s) => s.openDialog);
  const snap = useSnap((s) => s);
  const busy = isBusy(snap, game.appName);
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const signedIn = snap.auth.status === "signedIn";
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label="More actions"
          style={{
            borderTopLeftRadius: heroRadius.inner,
            borderBottomLeftRadius: heroRadius.inner,
            borderTopRightRadius: heroRadius.outer,
            borderBottomRightRadius: heroRadius.outer,
          }}
          className={cx(heroButtonBase, "w-12 shrink-0", heroButtonTone(primary))}
        >
          <MoreHorizontal size={20} />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-60">
        <DropdownMenuItem icon={<Star size={15} />} onSelect={() => void actions.setFavorite(game.appName, !game.favorite)}>
          {game.favorite ? "Remove from favorites" : "Add to favorites"}
        </DropdownMenuItem>
        <DropdownMenuItem icon={<EyeOff size={15} />} onSelect={() => void actions.setHidden(game.appName, !game.hidden)}>
          {game.hidden ? "Show in library" : "Hide from library"}
        </DropdownMenuItem>
        {installed && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem icon={<Wrench size={15} />} disabled={busy || !signedIn} onSelect={() => void actions.verify(game.appName)}>
              Verify and repair
            </DropdownMenuItem>
            <DropdownMenuItem icon={<RefreshCw size={15} />} disabled={!signedIn} onSelect={() => void actions.checkForUpdates()}>
              Check for updates
            </DropdownMenuItem>
            <DropdownMenuItem icon={<FolderInput size={15} />} disabled={busy} onSelect={() => openDialog({ kind: "move", appName: game.appName })}>
              Move install…
            </DropdownMenuItem>
            <DropdownMenuItem icon={<FolderOpen size={15} />} onSelect={() => void actions.openInstallFolder(game.appName)}>
              Open install folder
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem icon={<Trash2 size={15} />} destructive disabled={busy} onSelect={() => openDialog({ kind: "uninstall", appName: game.appName })}>
              Uninstall…
            </DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** Minimal inline resolution for a save conflict: choose a side (the other is backed up first) or play without syncing. */
export function SaveConflictCallout({ appName }: { appName: string }) {
  return (
    <div className="mt-6 flex w-[min(560px,100%)] flex-wrap items-center gap-3 rounded-xl border border-border-strong bg-surface-2 p-3 pl-4 backdrop-blur-md">
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-fg">Your cloud and local saves both changed</p>
        <p className="mt-0.5 text-xs text-fg-muted">Choose which to keep. The other one is backed up first.</p>
      </div>
      <Button size="sm" variant="secondary" onClick={() => void actions.resolveSaves(appName, "local", false)}>
        Keep local
      </Button>
      <Button size="sm" variant="secondary" onClick={() => void actions.resolveSaves(appName, "cloud", false)}>
        Keep cloud
      </Button>
      <Button size="sm" variant="ghost" onClick={() => void actions.launch(appName, { skipSaveSync: true })}>
        Play without syncing
      </Button>
    </div>
  );
}
