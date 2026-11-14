import type { ReactNode } from "react";
import { ArrowDownToLine, Download, Eye, EyeOff, FolderInput, FolderOpen, FolderPlus, Layers, MoreHorizontal, Play, Star, Trash2, Wrench } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { collectionActions } from "../../state/collections";
import { isBusy } from "../../state/derive";
import { useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { ContextMenu, ContextMenuTrigger, DropdownMenu, DropdownMenuTrigger, cx, contextKit, dropdownKit, type MenuKit } from "../../ui";
import { useCollectionDialog } from "./collectionDialog";
import { quickAction, runQuick, runUpdate } from "./status";

/**
 * The actions of one game, as menu items. The same list renders into a right-click menu and into the "…" button's menu
 * (pass the matching kit), so the two never drift. Nothing here is mounted until a menu opens.
 */
export function GameMenuItems({ kit, game }: { kit: MenuKit; game: GameSummary }) {
  const { Item, CheckItem, Separator, Sub } = kit;
  const { appName } = game;
  const collections = useSnap((s) => s.collections);
  const offline = useSnap((s) => s.connectivity === "offline");
  const signedIn = useSnap((s) => s.auth.status === "signedIn");
  const busy = useSnap((s) => isBusy(s, appName));
  const openDialog = useUi((s) => s.openDialog);
  const askNewCollection = useCollectionDialog((s) => s.openCreate);

  const quick = quickAction(game);
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const canUpdate = game.installStatus === "installed" && game.updateAvailable && !busy;

  return (
    <>
      {quick && (
        <Item
          icon={quick.kind === "play" ? <Play size={14} className="fill-current" /> : quick.kind === "install" ? <Download size={15} /> : <Wrench size={15} />}
          disabled={quick.kind === "install" && offline}
          onSelect={() => void runQuick(quick.kind, appName)}
        >
          {quick.label}
        </Item>
      )}
      {canUpdate && (
        <Item icon={<ArrowDownToLine size={15} />} disabled={!signedIn || offline} onSelect={() => void runUpdate(appName)}>
          Update
        </Item>
      )}
      {(quick || canUpdate) && <Separator />}
      <Item icon={<Star size={15} className={game.favorite ? "fill-current" : undefined} />} onSelect={() => void actions.setFavorite(appName, !game.favorite)}>
        {game.favorite ? "Remove from favorites" : "Add to favorites"}
      </Item>
      <Sub label="Add to collection" icon={<Layers size={15} />}>
        {collections.map((c) => (
          <CheckItem key={c.id} checked={c.appNames.includes(appName)} onCheckedChange={(on) => void collectionActions.setMember(c.id, appName, on === true)}>
            {c.name}
          </CheckItem>
        ))}
        {collections.length > 0 && <Separator />}
        <Item icon={<FolderPlus size={15} />} onSelect={() => askNewCollection(appName)}>
          New collection…
        </Item>
      </Sub>
      <Item icon={game.hidden ? <Eye size={15} /> : <EyeOff size={15} />} onSelect={() => void actions.setHidden(appName, !game.hidden)}>
        {game.hidden ? "Show in library" : "Hide from library"}
      </Item>
      {installed && (
        <>
          <Separator />
          <Item icon={<Wrench size={15} />} disabled={busy || !signedIn || offline} onSelect={() => void actions.verify(appName)}>
            Verify files
          </Item>
          <Item icon={<FolderInput size={15} />} disabled={busy} onSelect={() => openDialog({ kind: "move", appName })}>
            Move…
          </Item>
          <Item icon={<FolderOpen size={15} />} onSelect={() => void actions.openInstallFolder(appName)}>
            Open install folder
          </Item>
          <Separator />
          <Item icon={<Trash2 size={15} />} destructive disabled={busy} onSelect={() => openDialog({ kind: "uninstall", appName })}>
            Uninstall…
          </Item>
        </>
      )}
    </>
  );
}

/** Right-click (or the context-menu key / Shift+F10) on `children` opens the game's menu. */
export function GameContextMenu({ game, children }: { game: GameSummary; children: ReactNode }) {
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>{children}</ContextMenuTrigger>
      <contextKit.Content className="w-60">
        <GameMenuItems kit={contextKit} game={game} />
      </contextKit.Content>
    </ContextMenu>
  );
}

/** The "…" button. Not a tab stop (the game's own button is; its menu is reachable with the context-menu key). */
export function GameMoreMenu({ game, className, tone = "glass" }: { game: GameSummary; className?: string; tone?: "glass" | "plain" }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          tabIndex={-1}
          aria-label={`More actions for ${game.title}`}
          className={cx(
            "grid place-items-center outline-none transition-[background-color,color,opacity,transform] duration-150 ease-out active:scale-95 focus-visible:outline-2 focus-visible:outline-focus",
            tone === "glass"
              ? "size-7 rounded-full bg-[oklch(0.14_0.006_265/0.72)] text-white/90 ring-1 ring-inset ring-white/10 hover:bg-[oklch(0.14_0.006_265/0.88)]"
              : "size-8 rounded-md text-fg-muted hover:bg-surface-active hover:text-fg",
            className,
          )}
        >
          <MoreHorizontal size={15} />
        </button>
      </DropdownMenuTrigger>
      <dropdownKit.Content align="end" sideOffset={6} className="w-60">
        <GameMenuItems kit={dropdownKit} game={game} />
      </dropdownKit.Content>
    </DropdownMenu>
  );
}

