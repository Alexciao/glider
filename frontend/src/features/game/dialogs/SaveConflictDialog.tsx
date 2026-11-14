import { useEffect, useState } from "react";
import { HardDrive, Cloud } from "lucide-react";
import type { SaveConflict, SaveSide } from "../../../ipc/contract.gen";
import { rpc } from "../../../ipc/rpc";
import { actions, pendingKey } from "../../../state/actions";
import { formatBytes, formatRelativeTime } from "../../../state/format";
import { useGame } from "../../../state/library";
import { usePending, useUi } from "../../../state/ui";
import { Button, Modal, cx } from "../../../ui";
import { formatDateTime } from "../format";

/** One copy of the saves: where it lives, when it last changed (relative and exact), how big, and the way to keep it. */
function SideCard({
  icon,
  label,
  iso,
  size,
  newer,
  action,
  busy,
  disabled,
}: {
  icon: React.ReactNode;
  label: string;
  iso: string | null;
  size: number | null;
  newer: boolean;
  action: { label: string; onPress: () => void };
  busy: boolean;
  disabled: boolean;
}) {
  return (
    <div className="flex min-w-0 flex-col rounded-xl border border-border-strong bg-surface-1 p-4">
      <p className="flex items-center gap-2 text-sm font-medium text-fg">
        <span className="text-fg-muted" aria-hidden>
          {icon}
        </span>
        {label}
        {newer && <span className="rounded-full bg-accent-soft px-2 py-px text-2xs font-medium text-accent-text">Newer</span>}
      </p>
      <p className="mt-3 text-md font-semibold tracking-tight text-fg">{iso ? formatRelativeTime(iso) : "No copy"}</p>
      <p className="mt-0.5 text-xs text-fg-muted tabular-nums">{formatDateTime(iso) ?? "Nothing saved here yet"}</p>
      <div className="min-h-4 flex-1" aria-hidden />
      {size != null && <p className="mt-1 text-xs text-fg-muted tabular-nums">{formatBytes(size)}</p>}
      <Button variant="secondary" size="lg" className="mt-auto w-full" loading={busy} disabled={disabled} onClick={action.onPress}>
        {action.label}
      </Button>
    </div>
  );
}

/**
 * Cloud and local saves both changed. Side by side, with when and how big, and the choice is an explicit press on one
 * of them: nothing is pre-selected and focus starts on the dialog, so no key press can silently overwrite anything.
 * The side you don't keep is backed up first, and the dialog says so.
 */
export function SaveConflictDialog({
  appName,
  conflict: given,
  thenPlay,
  open,
}: {
  appName: string;
  conflict?: SaveConflict | null;
  thenPlay?: boolean;
  open: boolean;
}) {
  const close = useUi((s) => s.closeDialog);
  const game = useGame(appName);
  const working = usePending(pendingKey("saves", appName));
  const [fetched, setFetched] = useState<SaveConflict | null>(null);
  const [side, setSide] = useState<SaveSide | null>(null);

  // Opened from a fix action or the saves section (no launch result in hand): read the saves state instead.
  useEffect(() => {
    if (given) return;
    let alive = true;
    void rpc()
      .call("saves.get", { appName })
      .then((s) => {
        if (alive)
          setFetched({ appName, title: game?.title ?? appName, localModifiedAt: s.localModifiedAt, cloudModifiedAt: s.cloudModifiedAt, localSizeBytes: null, cloudSizeBytes: null, lastSyncedAt: s.lastSyncedAt });
      })
      .catch(() => undefined);
    return () => {
      alive = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appName, given]);

  const c = given ?? fetched;
  const title = game?.title ?? c?.title ?? "this game";
  const l = c?.localModifiedAt ? Date.parse(c.localModifiedAt) : null;
  const k = c?.cloudModifiedAt ? Date.parse(c.cloudModifiedAt) : null;
  const localNewer = l !== null && (k === null || l > k);
  const cloudNewer = k !== null && (l === null || k > l);

  const keep = async (which: SaveSide) => {
    setSide(which);
    const ok = await actions.resolveSaves(appName, which, !!thenPlay);
    if (ok) close();
    else setSide(null);
  };
  const play = thenPlay ? " and play" : "";

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && !working && close()}
      size="lg"
      title="Which saves do you want to keep?"
      description={
        c?.lastSyncedAt
          ? `${title}'s saves changed on this PC and in the cloud since they last synced ${formatRelativeTime(c.lastSyncedAt).toLowerCase()} (${formatDateTime(c.lastSyncedAt)}).`
          : `${title}'s saves differ between this PC and the cloud.`
      }
      footer={
        <>
          <p className="mr-auto max-w-[34ch] text-xs leading-[1.5] text-fg-muted">The one you don't keep is backed up first, so nothing is lost.</p>
          <Button variant="ghost" onClick={close} disabled={working}>
            Cancel
          </Button>
        </>
      }
    >
      <div className="pb-2">
        <div className={cx("grid grid-cols-2 gap-3 transition-opacity duration-150", !c && "opacity-60")}>
          <SideCard
            icon={<HardDrive size={16} />}
            label="On this PC"
            iso={c?.localModifiedAt ?? null}
            size={c?.localSizeBytes ?? null}
            newer={localNewer}
            action={{ label: `Keep local${play}`, onPress: () => void keep("local") }}
            busy={working && side === "local"}
            disabled={!c || working}
          />
          <SideCard
            icon={<Cloud size={16} />}
            label="In the cloud"
            iso={c?.cloudModifiedAt ?? null}
            size={c?.cloudSizeBytes ?? null}
            newer={cloudNewer}
            action={{ label: `Keep cloud${play}`, onPress: () => void keep("cloud") }}
            busy={working && side === "cloud"}
            disabled={!c || working}
          />
        </div>
      </div>
    </Modal>
  );
}
