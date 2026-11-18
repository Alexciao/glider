import type { ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import {
  CircleArrowUp,
  Ellipsis,
  Star,
  Download,
  EyeOff,
  FolderInput,
  FolderOpen,
  Link2,
  Layers,
  LogIn,
  Pause,
  Play,
  ShieldCheck,
  Square,
  Trash2,
  Eye,
  ExternalLink,
  X,
  Wrench,
  ArrowUpToLine,
} from "lucide-react";
import type { AppSnapshot, GameDetail, GameSummary, ShortcutLocation, ShortcutState } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_NOUN, isBusy } from "../../state/derive";
import { openInstall, prefetchInstallPlan } from "../../state/installPlan";
import { useUi } from "../../state/ui";
import {
  DropdownMenuCheckboxItem,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuSkin,
  DropdownMenuTrigger,
  Spinner,
  Tooltip,
  cx,
  labelSwap,
  tween,
} from "../../ui";
import type { ActionKind, GameAction } from "./actionModel";
import { HeroProgress } from "./HeroProgress";

const ICONS: Partial<Record<ActionKind, ReactNode>> = {
  signIn: <LogIn size={18} />,
  install: <Download size={18} />,
  update: <CircleArrowUp size={18} />,
  play: <Play size={16} fill="currentColor" strokeWidth={0} />,
  resume: <Play size={16} fill="currentColor" strokeWidth={0} />,
  repair: <Wrench size={16} />,
  pause: <Pause size={16} fill="currentColor" strokeWidth={0} />,
  cancel: <X size={18} />,
  closeGame: <Square size={14} fill="currentColor" strokeWidth={0} />,
};

/** Core refuses to cancel some running steps (an uninstall mid-delete, a move rewriting its metadata). */
const cancelRefused = (action: GameAction): boolean => action.intent === "cancel" && action.job?.cancellable === false;

/** Runs what the primary segment says it does. */
export function pressPrimary(action: GameAction, appName: string): void {
  if (cancelRefused(action)) return;
  const ui = useUi.getState();
  switch (action.intent) {
    case "signIn":
      void actions.signIn("hero");
      break;
    case "install":
      void openInstall(appName);
      break;
    case "update":
      void actions.update(appName);
      break;
    case "launch":
      void actions.launch(appName);
      break;
    case "verify":
      void actions.verify(appName);
      break;
    case "pause":
      if (action.job) void actions.pauseJob(action.job.id);
      break;
    case "resume":
      if (action.job) void actions.resumeJob(action.job.id);
      break;
    case "cancel":
      if (action.job) ui.openDialog({ kind: "cancelJob", jobId: action.job.id });
      break;
    case "close":
      void actions.closeGame(appName);
      break;
    case "none":
      break;
  }
}

/** Hover/focus prefetch for the primary: warms the launch command, or the install plan. */
const warmFor = (action: GameAction, appName: string): (() => void) | undefined =>
  action.intent === "launch" ? () => void actions.prepareLaunch(appName) : action.intent === "install" ? () => void prefetchInstallPlan(appName) : undefined;

/** Icon (or spinner) and label; crossfades only when the face's identity changes, so "Launching…" never flickers. */
function Face({ action }: { action: GameAction }) {
  return (
    <AnimatePresence mode="popLayout" initial={false}>
      <m.span
        key={action.faceKey}
        initial={labelSwap.initial}
        animate={labelSwap.animate}
        exit={labelSwap.exit}
        transition={tween.base}
        className="relative flex items-center gap-2.5 whitespace-nowrap"
      >
        {action.busy ? <Spinner size={16} /> : ICONS[action.kind]}
        {action.label}
      </m.span>
    </AnimatePresence>
  );
}

function FavoriteToggle({ game }: { game: GameSummary }) {
  const on = game.favorite;
  return (
    <Tooltip content={on ? "Remove from favorites" : "Add to favorites"}>
      <button type="button" aria-pressed={on} aria-label="Favorite" onClick={() => void actions.setFavorite(game.appName, !on)} className="ga-dock-icon">
        <Star size={18} className={cx("ga-star", on && "fill-current")} />
      </button>
    </Tooltip>
  );
}

/**
 * The hero's action group: one glass dock (the same blur and hairline as the hero's other glass) holding the primary action
 * from the action model, favorite, and the one menu every game shares. The primary is lit with the accent only when it's what
 * the page is for (Play, Install, Update, Resume, Sign in); work in progress (Pause, Close game, Cancel) stays glass, so a lit
 * button always means "go". Hovering or focusing Play warms the launch command (`game.prepareLaunch`), so the press is
 * near-instant. While a job runs, its bar sits beside the dock, never under the title, so the title never jumps.
 */
export function GameActions({
  snap,
  game,
  detail,
  action,
  moment,
  showBar = false,
  onShortcuts,
}: {
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  moment: boolean;
  /** The job has a bar to show (running or paused, not queued/waiting). */
  showBar?: boolean;
  /** game.setShortcut's answer; shortcuts aren't in the summary, so the detail would otherwise never refetch. */
  onShortcuts?: (state: ShortcutState) => void;
}) {
  const { appName } = game;
  const warm = warmFor(action, appName);
  const inert = action.busy || action.disabled;
  return (
    <>
      <div role="group" aria-label={`${game.title} actions`} className="ga-dock" data-lit={action.tone === "primary" || undefined}>
        <m.button
          layout="position"
          type="button"
          data-kind={action.kind}
          data-tone={action.tone}
          disabled={action.disabled || cancelRefused(action)}
          aria-busy={action.busy || undefined}
          onClick={action.busy ? undefined : () => pressPrimary(action, appName)}
          onPointerEnter={warm}
          onFocus={warm}
          className={cx("ga-dock-primary", inert && "ga-inert")}
        >
          <Face action={action} />
          {moment && (
            <>
              <span aria-hidden className="ga-sheen" />
              <span aria-hidden className="g-ready-ring" />
            </>
          )}
        </m.button>
        <m.span layout="position" aria-hidden className="ga-dock-sep" />
        <m.span layout="position" className="flex">
          <FavoriteToggle game={game} />
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <button type="button" aria-label="More actions" className="ga-dock-icon">
                <Ellipsis size={18} />
              </button>
            </DropdownMenuTrigger>
            <DropdownMenuSkin value="ga-menu ga-menu-glass">
              <DropdownMenuContent align="end" alignOffset={-4} sideOffset={10} className="w-60">
                <GameMenuItems snap={snap} game={game} detail={detail} action={action} onShortcuts={onShortcuts} />
              </DropdownMenuContent>
            </DropdownMenuSkin>
          </DropdownMenu>
        </m.span>
      </div>
      {showBar && action.job && (
        <div className="ml-4 min-w-0 max-w-[440px] flex-1">
          <HeroProgress job={action.job} />
        </div>
      )}
    </>
  );
}

function GameMenuItems({
  snap,
  game,
  detail,
  action,
  onShortcuts,
}: {
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  onShortcuts?: (state: ShortcutState) => void;
}) {
  const openDialog = useUi((s) => s.openDialog);
  const go = useUi((s) => s.go);
  const { appName } = game;
  const busy = isBusy(snap, appName);
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const signedIn = snap.auth.status === "signedIn";
  const job = action.job;
  const collections = snap.collections;

  const toggleShortcut = (location: ShortcutLocation, create: boolean) =>
    void actions.setShortcut(appName, location, create).then((s) => s && onShortcuts?.(s));
  const items: ReactNode[] = [];

  if (action.kind === "update" && game.installStatus === "installed") {
    items.push(
      <DropdownMenuItem key="play" icon={<Play size={14} fill="currentColor" strokeWidth={0} />} onSelect={() => void actions.launch(appName)}>
        Play without updating
      </DropdownMenuItem>,
      <DropdownMenuSeparator key="s-play" />,
    );
  }

  // Signed out, the primary says Sign in; an installed game that can run without Epic's services still starts from here.
  if (action.kind === "signIn" && game.installStatus === "installed" && game.canRunOffline) {
    items.push(
      <DropdownMenuItem key="offline" icon={<Play size={14} fill="currentColor" strokeWidth={0} />} onSelect={() => void actions.launch(appName, { mode: "offline" })}>
        Play offline
      </DropdownMenuItem>,
      <DropdownMenuSeparator key="s-offline" />,
    );
  }

  if (job) {
    items.push(
      <DropdownMenuItem key="downloads" icon={<Download size={14} />} onSelect={() => go("downloads")}>
        Open downloads
      </DropdownMenuItem>,
    );
    if (job.status === "queued") {
      items.push(
        <DropdownMenuItem key="top" icon={<ArrowUpToLine size={14} />} onSelect={() => void actions.moveJobToTop(job.id)}>
          Move to top of queue
        </DropdownMenuItem>,
      );
    }
    if (action.intent !== "cancel" && job.cancellable) {
      items.push(
        <DropdownMenuItem key="cancel" icon={<X size={14} />} onSelect={() => openDialog({ kind: "cancelJob", jobId: job.id })}>
          Cancel {JOB_NOUN[job.kind].toLowerCase()}…
        </DropdownMenuItem>,
      );
    }
    items.push(<DropdownMenuSeparator key="s-job" />);
  }

  if (installed) {
    items.push(
      <DropdownMenuItem key="verify" icon={<ShieldCheck size={14} />} disabled={busy || !signedIn} onSelect={() => void actions.verify(appName)}>
        Verify files
      </DropdownMenuItem>,
      <DropdownMenuItem key="move" icon={<FolderInput size={14} />} disabled={busy} onSelect={() => openDialog({ kind: "move", appName })}>
        Move…
      </DropdownMenuItem>,
      game.thirdParty !== "none" ? (
        <DropdownMenuItem key="activate" icon={<ExternalLink size={14} />} disabled={!signedIn} onSelect={() => void actions.activate(appName)}>
          Link {game.thirdPartyName ?? "launcher"} account
        </DropdownMenuItem>
      ) : null,
      <DropdownMenuItem key="open" icon={<FolderOpen size={14} />} onSelect={() => void actions.openInstallFolder(appName)}>
        Open install folder
      </DropdownMenuItem>,
      <DropdownMenuSub key="shortcut">
        <DropdownMenuSubTrigger icon={<Link2 size={14} />} disabled={!detail}>
          Create shortcut
        </DropdownMenuSubTrigger>
        <DropdownMenuSubContent className="w-48">
          <DropdownMenuCheckboxItem checked={detail?.shortcuts.desktop ?? false} onCheckedChange={(v) => toggleShortcut("desktop", v === true)}>
            On desktop
          </DropdownMenuCheckboxItem>
          <DropdownMenuCheckboxItem checked={detail?.shortcuts.startMenu ?? false} onCheckedChange={(v) => toggleShortcut("startMenu", v === true)}>
            In Start menu
          </DropdownMenuCheckboxItem>
        </DropdownMenuSubContent>
      </DropdownMenuSub>,
      <DropdownMenuSeparator key="s-installed" />,
    );
  }

  items.push(
    <DropdownMenuSub key="collection">
      <DropdownMenuSubTrigger icon={<Layers size={14} />}>Add to collection</DropdownMenuSubTrigger>
      <DropdownMenuSubContent className="w-56">
        {collections.length === 0 ? (
          <DropdownMenuItem disabled>No collections yet</DropdownMenuItem>
        ) : (
          collections.map((c) => (
            <DropdownMenuCheckboxItem key={c.id} checked={c.appNames.includes(appName)} onCheckedChange={(v) => void actions.setCollectionMembership(c.id, appName, v === true)}>
              {c.name}
            </DropdownMenuCheckboxItem>
          ))
        )}
      </DropdownMenuSubContent>
    </DropdownMenuSub>,
    <DropdownMenuItem key="hide" icon={game.hidden ? <Eye size={14} /> : <EyeOff size={14} />} onSelect={() => void actions.setHidden(appName, !game.hidden)}>
      {game.hidden ? "Show in library" : "Hide from library"}
    </DropdownMenuItem>,
  );

  if (installed) {
    items.push(
      <DropdownMenuSeparator key="s-danger" />,
      <DropdownMenuItem key="uninstall" icon={<Trash2 size={14} />} destructive disabled={busy} onSelect={() => openDialog({ kind: "uninstall", appName })}>
        Uninstall…
      </DropdownMenuItem>,
    );
  }
  return <>{items}</>;
}
