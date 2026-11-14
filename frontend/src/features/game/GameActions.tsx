import type { ReactNode } from "react";
import {
  ArrowDownToLine,
  Download,
  EyeOff,
  FolderInput,
  FolderOpen,
  Link2,
  ListPlus,
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
import type { AppSnapshot, GameDetail, GameSummary, ShortcutLocation } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_NOUN, isBusy } from "../../state/derive";
import { useUi } from "../../state/ui";
import {
  DropdownMenuCheckboxItem,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  SplitButton,
} from "../../ui";
import type { ActionKind, GameAction } from "./actionModel";

const ICONS: Partial<Record<ActionKind, ReactNode>> = {
  signIn: <LogIn size={18} />,
  install: <Download size={18} />,
  update: <ArrowDownToLine size={18} />,
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
      ui.openDialog({ kind: "install", appName });
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

/**
 * The hero's split button: the primary action from the action model and the one menu every game shares.
 * Hovering or focusing Play warms the launch command (`game.prepareLaunch`), so the press itself is near-instant.
 */
export function GameActions({
  snap,
  game,
  detail,
  action,
  moment,
}: {
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  moment: boolean;
}) {
  const { appName } = game;
  const warm = action.intent === "launch" ? () => void actions.prepareLaunch(appName) : undefined;
  return (
    <SplitButton
      size="lg"
      tone={action.tone}
      label={action.label}
      faceKey={action.faceKey}
      icon={ICONS[action.kind]}
      busy={action.busy}
      disabled={action.disabled || cancelRefused(action)}
      moment={moment}
      aria-label={`${game.title} actions`}
      onPress={() => pressPrimary(action, appName)}
      primaryProps={{ onPointerEnter: warm, onFocus: warm, "data-kind": action.kind }}
      menu={<GameMenuItems snap={snap} game={game} detail={detail} action={action} />}
    />
  );
}

function GameMenuItems({ snap, game, detail, action }: { snap: AppSnapshot; game: GameSummary; detail: GameDetail | null; action: GameAction }) {
  const openDialog = useUi((s) => s.openDialog);
  const go = useUi((s) => s.go);
  const { appName } = game;
  const busy = isBusy(snap, appName);
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const signedIn = snap.auth.status === "signedIn";
  const job = action.job;
  const collections = snap.collections;

  const toggleShortcut = (location: ShortcutLocation, create: boolean) => void actions.setShortcut(appName, location, create);
  const items: ReactNode[] = [];

  if (action.kind === "update" && game.installStatus === "installed") {
    items.push(
      <DropdownMenuItem key="play" icon={<Play size={14} fill="currentColor" strokeWidth={0} />} onSelect={() => void actions.launch(appName)}>
        Play without updating
      </DropdownMenuItem>,
      <DropdownMenuSeparator key="s-play" />,
    );
  }

  if (job) {
    items.push(
      <DropdownMenuItem key="downloads" icon={<ArrowDownToLine size={14} />} onSelect={() => go("downloads")}>
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
        Verify
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
        Open folder
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
      <DropdownMenuSubTrigger icon={<ListPlus size={14} />}>Add to collection</DropdownMenuSubTrigger>
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
