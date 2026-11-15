import { useEffect, useState } from "react";
import { TriangleAlert } from "lucide-react";
import type { InstallPlan } from "../../../ipc/contract.gen";
import { actions, pendingKey } from "../../../state/actions";
import { dirname, driveOf, formatBytes } from "../../../state/format";
import { useGame } from "../../../state/library";
import { useSnap } from "../../../state/store";
import { usePending, useUi } from "../../../state/ui";
import { Button, Modal, Switch } from "../../../ui";
import { ErrorView } from "../../common/ErrorView";
import { PathText } from "../../common/PathText";
import { FolderChooser, SpaceMeter, useFolderOptions } from "./FolderChooser";

const same = (a: string, b: string) => a.replace(/[\\/]+$/, "").toLowerCase() === b.replace(/[\\/]+$/, "").toLowerCase();

/**
 * Move: the destination is a library folder, and the folder the game lands in is the name Core gave it (the plan's
 * game folder), never a guess from the title or the app id.
 */
export function MoveDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const game = useGame(appName);
  const eglSync = useSnap((s) => s.settings.eglSync);
  const error = useUi((s) => s.errors.move);
  const moving = usePending(pendingKey("move", appName));
  const [base, setBase] = useState<string | null>(null);
  const [answer, setAnswer] = useState<{ base: string; plan: InstallPlan | null } | null>(null);

  useEffect(() => () => useUi.getState().setError("move", undefined), []);
  useEffect(() => {
    if (!base) return;
    let alive = true;
    void actions.planInstall(appName, base, null, null, "move").then((p) => {
      if (alive) setAnswer({ base, plan: p ?? null });
    });
    return () => {
      alive = false;
    };
  }, [appName, base]);

  const current = game?.installPath ?? null;
  const options = useFolderOptions(base ? { path: base, freeBytes: answer?.base === base ? (answer.plan?.freeBytes ?? null) : null } : null, current ? dirname(current) : null);
  if (!game || !current) return null;

  const size = game.installedSizeBytes ?? game.installSizeBytes ?? 0;
  const plan = answer && base && answer.base === base ? answer.plan : null;
  const planning = !!base && (!answer || answer.base !== base);
  const target = plan?.gamePath ?? null;
  const here = !!target && same(target, current);
  const free = plan?.freeBytes ?? options.find((o) => base && same(o.path, base))?.freeBytes ?? null;
  const fits = free == null || free >= size;
  const drive = driveOf(base);
  const egl = game.sharedWithEgl;
  const pick = async () => {
    const p = await actions.pickFolder(base ?? dirname(current), `Choose where to move ${game.title}`);
    if (p) setBase(p);
  };
  const verdict = !base
    ? "Choose a folder to check the space."
    : free == null
      ? "Checking free space…"
      : fits
        ? `Fits, with ${formatBytes(free - size)} to spare.`
        : `Needs ${formatBytes(size - free)} more on ${drive || "that drive"}. Choose another folder or free up space.`;

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && !moving && close()}
      size="lg"
      anchor="top"
      title={`Move ${game.title}`}
      description="You can't play while the files move."
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={moving}>
            Cancel
          </Button>
          <Button variant="primary" loading={moving} disabled={!base || !plan || planning || !fits || here} onClick={() => base && void actions.move(appName, base)}>
            Move
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-5 pb-2">
        <SpaceMeter
          loading={planning}
          fits={fits}
          neededBytes={size}
          freeBytes={free}
          verdict={verdict}
          stats={[
            { label: "To move", value: formatBytes(size) },
          ]}
        />

        <section aria-label="Move to">
          <FolderChooser label="Move to" options={options} value={base} neededBytes={size} onChange={setBase} onChooseOther={() => void pick()} disabled={moving} />
          {/* The folder the game lands in is the name Core gave it, so it is shown once a destination is chosen. */}
          {target && (
            <p className="mt-2 flex items-baseline gap-2 text-xs text-fg-muted">
              <span className="shrink-0">Lands in</span>
              <PathText path={target} className="min-w-0 font-medium text-fg" />
            </p>
          )}
          {here && <p className="mt-2 text-xs text-warning-text">It's already there. Choose a different folder.</p>}
        </section>

        {egl && (
          <div className="flex flex-col gap-2.5">
            <label className="flex items-center gap-3 text-sm font-medium text-fg">
              <Switch aria-label="Keep Epic Games Launcher in sync" checked={eglSync} onCheckedChange={(v) => void actions.updateSettings({ eglSync: v })} />
              Keep Epic Games Launcher in sync
            </label>
            {!eglSync && (
              <p className="flex items-center gap-2 text-xs text-warning-text" role="status">
                <TriangleAlert size={14} aria-hidden className="shrink-0" />
                Epic Games Launcher will lose track of this install.
              </p>
            )}
          </div>
        )}
        {error && <ErrorView error={error.error} retry={error.retry} />}
      </div>
    </Modal>
  );
}
