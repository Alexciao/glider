import { useState, type ReactNode } from "react";
import { CircleArrowUp, Download, Eye, EyeOff, FolderInput, FolderOpen, Layers, MoreHorizontal, Play, Plus, ShieldCheck, Star, Trash2, Wrench } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { collectionActions } from "../../state/collections";
import { isBusy } from "../../state/derive";
import { useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { ContextMenu, ContextMenuTrigger, contextKit, cx, type MenuKit } from "../../ui";
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
          icon={quick.kind === "play" ? <Play size={14} className="fill-current" /> : quick.kind === "install" ? <Download size={14} /> : <Wrench size={14} />}
          disabled={quick.kind === "install" && offline}
          onSelect={() => void runQuick(quick.kind, appName)}
        >
          {quick.label}
        </Item>
      )}
      {canUpdate && (
        <Item icon={<CircleArrowUp size={14} />} disabled={!signedIn || offline} onSelect={() => void runUpdate(appName)}>
          Update
        </Item>
      )}
      {(quick || canUpdate) && <Separator />}
      <Item icon={<Star size={14} className={game.favorite ? "fill-current" : undefined} />} onSelect={() => void actions.setFavorite(appName, !game.favorite)}>
        {game.favorite ? "Remove from favorites" : "Add to favorites"}
      </Item>
      <Sub label="Add to collection" icon={<Layers size={14} />}>
        {collections.map((c) => (
          <CheckItem key={c.id} checked={c.appNames.includes(appName)} onCheckedChange={(on) => void collectionActions.setMember(c.id, appName, on === true)}>
            {c.name}
          </CheckItem>
        ))}
        {collections.length > 0 && <Separator />}
        <Item icon={<Plus size={14} />} onSelect={() => askNewCollection(appName)}>
          New collection…
        </Item>
      </Sub>
      <Item icon={game.hidden ? <Eye size={14} /> : <EyeOff size={14} />} onSelect={() => void actions.setHidden(appName, !game.hidden)}>
        {game.hidden ? "Show in library" : "Hide from library"}
      </Item>
      {installed && (
        <>
          <Separator />
          <Item icon={<ShieldCheck size={14} />} disabled={busy || !signedIn || offline} onSelect={() => void actions.verify(appName)}>
            Verify files
          </Item>
          <Item icon={<FolderInput size={14} />} disabled={busy} onSelect={() => openDialog({ kind: "move", appName })}>
            Move…
          </Item>
          <Item icon={<FolderOpen size={14} />} onSelect={() => void actions.openInstallFolder(appName)}>
            Open install folder
          </Item>
          <Separator />
          <Item icon={<Trash2 size={14} />} destructive disabled={busy} onSelect={() => openDialog({ kind: "uninstall", appName })}>
            Uninstall…
          </Item>
        </>
      )}
    </>
  );
}

/**
 * One context menu for the whole library, not one per tile: mounting a Radix menu root per tile made every tile several
 * milliseconds dearer to mount, which is what a search or a filter pays 30 times over. The wrapper reads which tile was
 * hit (right-click, the context-menu key, or a tile's "…" button) and renders that game's items when it opens.
 */
export function LibraryContextMenu({ games, children }: { games: readonly GameSummary[]; children: ReactNode }) {
  const [target, setTarget] = useState<GameSummary | null>(null);
  return (
    <ContextMenu>
      <ContextMenuTrigger asChild>
        <div
          className="h-full"
          onContextMenuCapture={(e) => {
            const li = (e.target as HTMLElement).closest<HTMLElement>("li[data-index]");
            const game = li ? games[Number(li.dataset.index)] : undefined;
            // Anywhere that is not a game keeps the browser's (or the host's) own behaviour.
            if (!game) e.stopPropagation();
            else setTarget(game);
          }}
        >
          {children}
        </div>
      </ContextMenuTrigger>
      {target && (
        <contextKit.Content className="w-60">
          <GameMenuItems kit={contextKit} game={target} />
        </contextKit.Content>
      )}
    </ContextMenu>
  );
}

/** The "…" button: asks the library's context menu to open at the button. Not a tab stop (the context-menu key does the same). */
export function MoreButton({ game, className, tone = "glass" }: { game: GameSummary; className?: string; tone?: "glass" | "plain" }) {
  return (
    <button
      type="button"
      tabIndex={-1}
      aria-label={`More actions for ${game.title}`}
      onClick={(e) => {
        const r = e.currentTarget.getBoundingClientRect();
        e.currentTarget.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, clientX: r.left, clientY: r.bottom + 4 }));
      }}
      className={cx(
        "grid place-items-center outline-none transition-[background-color,color,opacity,transform,scale] duration-150 ease-out press:scale-95 focus-visible:outline-2 focus-visible:outline-focus",
        tone === "glass"
          ? "size-7 rounded-full bg-[oklch(0.14_0.006_265/0.72)] text-white/90 ring-1 ring-inset ring-white/10 hover:bg-[oklch(0.14_0.006_265/0.88)]"
          : "size-8 rounded-md text-fg-muted hover:bg-surface-active hover:text-fg",
        className,
      )}
    >
      <MoreHorizontal size={16} />
    </button>
  );
}
