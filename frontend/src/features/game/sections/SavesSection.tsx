import type { GameDetail, GameSummary, SaveSyncStatus } from "../../../ipc/contract.gen";
import { actions, pendingKey } from "../../../state/actions";
import { formatRelativeTime } from "../../../state/format";
import { useSnap } from "../../../state/store";
import { usePending, useUi } from "../../../state/ui";
import { Button, Spinner, cx, type BadgeTone } from "../../../ui";
import { formatDateTime } from "../format";
import { TONE_TEXT, statusTone } from "../statusLevels";

export interface SaveStatusModel {
  tone: BadgeTone;
  text: string;
}

/** One short phrase per state Core can report; it says what will happen, not just what is. */
export function saveStatusModel(status: SaveSyncStatus, opts: { enabled: boolean; offline: boolean }): SaveStatusModel {
  if (!opts.enabled && status !== "notSupported") return { tone: "neutral", text: "Off" };
  switch (status) {
    case "inSync":
      return { tone: statusTone("inSync"), text: "In sync" };
    case "localNewer":
      return { tone: statusTone("savesNewer"), text: "This PC is newer" };
    case "cloudNewer":
      return { tone: statusTone("savesNewer"), text: "The cloud is newer" };
    case "conflict":
      return { tone: statusTone("savesDiffer"), text: "Saves differ" };
    case "syncing":
      return { tone: statusTone("working"), text: "Syncing…" };
    case "error":
      return { tone: statusTone("syncFailed"), text: "Last sync failed" };
    case "disabled":
      return { tone: "neutral", text: "Off" };
    case "notSupported":
      return { tone: "neutral", text: "Not supported" };
    case "unknown":
      return { tone: "neutral", text: opts.offline ? "Checks when you're online" : "Not checked yet" };
  }
}

/**
 * Cloud saves as one row: the state, the one number that matters ("synced 2 days ago"), and the buttons that apply.
 * Local-versus-cloud times appear only when they differ (the decision is which to trust). A conflict tints the row
 * and its Resolve button opens the comparison dialog: this row is the conflict's only surface on the page.
 * Games without cloud saves show nothing at all.
 */
export function SavesSection({ game, detail }: { game: GameSummary; detail: GameDetail }) {
  const saves = detail.saves;
  const offline = useSnap((s) => s.connectivity === "offline");
  const signedIn = useSnap((s) => s.auth.status === "signedIn");
  const globalOn = useSnap((s) => s.settings.cloudSaves);
  const openDialog = useUi((s) => s.openDialog);
  const syncing = usePending(pendingKey("sync", game.appName));
  // Nothing to sync before the game is on this PC.
  if (!saves || !saves.supported || (game.installStatus !== "installed" && game.installStatus !== "broken")) return null;

  const status: SaveSyncStatus = syncing ? "syncing" : saves.status;
  const enabled = saves.enabled && globalOn;
  const model = saveStatusModel(status, { enabled, offline });
  const conflict = enabled && status === "conflict";
  const differs = enabled && (status === "localNewer" || status === "cloudNewer");
  const idle = game.runState === "idle" && game.installStatus === "installed";

  return (
    <section aria-labelledby="saves-h" className={cx("flex flex-wrap items-center gap-x-6 gap-y-2 rounded-lg", conflict && "-mx-3 w-fit bg-warning-soft px-3 py-2.5")}>
      <div className="min-w-0">
        <h2 id="saves-h" className="text-sm font-semibold text-fg">
          Cloud saves
        </h2>
        <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-fg-muted" aria-live="polite">
          {status === "syncing" && <Spinner size={12} />}
          <span className={cx("font-medium", TONE_TEXT[model.tone])}>{model.text}</span>
          {enabled && saves.lastSyncedAt && !differs && !conflict && (
            <span title={formatDateTime(saves.lastSyncedAt) ?? undefined}>Synced {formatRelativeTime(saves.lastSyncedAt)}</span>
          )}
          {(differs || conflict) && (
            <span className="tabular-nums" title={`This PC ${formatDateTime(saves.localModifiedAt) ?? "no copy"}. Cloud ${formatDateTime(saves.cloudModifiedAt) ?? "no copy"}.`}>
              PC {saves.localModifiedAt ? formatRelativeTime(saves.localModifiedAt) : "none"} · cloud {saves.cloudModifiedAt ? formatRelativeTime(saves.cloudModifiedAt) : "none"}
            </span>
          )}
          {status === "error" && saves.lastError && <span>{saves.lastError.message}</span>}
          {enabled && !signedIn && <span className="text-fg-subtle">Sign in to sync</span>}
        </p>
      </div>
      <div className="flex items-center gap-2">
        {conflict ? (
          <Button size="sm" variant="secondary" onClick={() => openDialog({ kind: "saveConflict", appName: game.appName, thenPlay: false })}>
            Resolve…
          </Button>
        ) : (
          // Only when it can act: a disabled "Sync now" is a button that explains nothing (signed out says so in the status line).
          enabled && signedIn && !offline && idle && (
            <Button size="sm" variant="ghost" loading={syncing} onClick={() => void actions.syncSaves(game.appName)}>
              Sync now
            </Button>
          )
        )}
        {saves.backups > 0 && (
          <Button size="sm" variant="ghost" onClick={() => void actions.openSaveBackups(game.appName)}>
            Backups
          </Button>
        )}
      </div>
    </section>
  );
}
