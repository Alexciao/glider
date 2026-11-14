import { useEffect, useState } from "react";
import { RadioGroup } from "radix-ui";
import type { InstallPlan, UninstallMode } from "../../ipc/contract.gen";
import { actions, pendingKey, runFix } from "../../state/actions";
import { JOB_NOUN } from "../../state/derive";
import { formatBytes } from "../../state/format";
import { useGame } from "../../state/library";
import { useApp, useSnap } from "../../state/store";
import { FIX_LABELS, usePending, useUi, type DialogState } from "../../state/ui";
import { AlertModal, Button, ErrorCallout, Modal, Switch, cx } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { InstallPlanner } from "../common/InstallPlanner";
import { PathText } from "../common/PathText";

/* ----------------------------------------------------------------- install */

function InstallDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const error = useUi((s) => s.errors.install);
  const title = useGame(appName)?.title ?? "game";
  const starting = usePending(pendingKey("install", appName));
  const [plan, setPlan] = useState<InstallPlan | null>(null);

  useEffect(() => () => useUi.getState().setError("install", undefined), []);

  const install = async () => {
    if (!plan) return;
    const job = await actions.install(appName, plan.basePath, "install");
    if (job) {
      close();
      useUi.getState().go("downloads");
    }
  };
  const importExisting = async (path: string) => {
    const job = await actions.importFolder(path, "auto", "install");
    if (job) {
      close();
      useUi.getState().go("downloads");
    }
  };

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && !starting && close()}
      size="lg"
      title={`Install ${title}`}
      description="Pick a drive with enough room. Downloads resume if you pause or lose connection."
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={starting}>
            Cancel
          </Button>
          <Button variant="primary" loading={starting} disabled={!plan?.hasEnoughSpace} onClick={() => void install()}>
            Install
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 pb-1">
        <InstallPlanner appName={appName} onPlan={setPlan} onUseExisting={(p) => void importExisting(p)} />
        {error && <ErrorView error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("install", undefined)} />}
      </div>
    </Modal>
  );
}

/* --------------------------------------------------------------- uninstall */

const modeCard =
  "flex w-full items-start gap-3 rounded-xl border p-3.5 text-left transition-[background-color,border-color] duration-150 " +
  "border-border bg-surface-1 hover:bg-surface-2";

function RadioDot({ checked, danger }: { checked: boolean; danger?: boolean }) {
  return (
    <span
      aria-hidden
      className={cx(
        "mt-0.5 grid size-4 shrink-0 place-items-center rounded-full border transition-colors duration-150",
        checked ? (danger ? "border-danger bg-danger" : "border-accent bg-accent") : "border-border-strong",
      )}
    >
      <span className={cx("size-1.5 rounded-full bg-accent-fg transition-transform duration-150", checked ? "scale-100" : "scale-0", danger && "bg-danger-fg")} />
    </span>
  );
}

function UninstallDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const game = useGame(appName);
  const error = useUi((s) => s.errors.uninstall);
  const working = usePending(pendingKey("uninstall", appName));
  const shared = game?.sharedWithEgl ?? false;
  const [mode, setMode] = useState<UninstallMode>(shared ? "gliderOnly" : "deleteFiles");

  useEffect(() => () => useUi.getState().setError("uninstall", undefined), []);
  if (!game || !game.installPath) return null;
  const title = game.title;

  const destructive = mode === "deleteFiles";
  return (
    <AlertModal
      open={open}
      onOpenChange={(o) => !o && !working && close()}
      size="md"
      title={`Uninstall ${title}?`}
      description={
        shared
          ? "This install is shared with Epic Games Launcher, so choose how far to go."
          : "Choose whether to keep the game files on this PC."
      }
      cancel={
        <Button variant="ghost" disabled={working}>
          Cancel
        </Button>
      }
      action={
        <Button
          variant={destructive ? "danger" : "primary"}
          loading={working}
          onClick={(e) => {
            e.preventDefault();
            void actions.uninstall(appName, mode);
          }}
        >
          {destructive ? "Uninstall and delete files" : "Remove from Glider"}
        </Button>
      }
    >
      <RadioGroup.Root value={mode} onValueChange={(v) => setMode(v as UninstallMode)} aria-label="Uninstall mode" className="flex flex-col gap-2">
        <RadioGroup.Item
          value="gliderOnly"
          className={cx(modeCard, "data-[state=checked]:border-accent/60 data-[state=checked]:bg-accent-soft")}
        >
          <RadioDot checked={mode === "gliderOnly"} />
          <span className="min-w-0">
            <span className="block text-sm font-medium text-fg">
              Remove from Glider only{shared && <span className="ml-2 text-xs font-normal text-accent-text">Recommended</span>}
            </span>
            <span className="mt-0.5 block text-xs leading-[1.5] text-fg-muted">
              {shared
                ? "Glider forgets this install. Your files stay where they are and Epic Games Launcher keeps working."
                : "Glider forgets this install. Your files stay where they are."}
            </span>
          </span>
        </RadioGroup.Item>
        <RadioGroup.Item
          value="deleteFiles"
          className={cx(modeCard, "data-[state=checked]:border-danger/60 data-[state=checked]:bg-danger-soft")}
        >
          <RadioDot checked={mode === "deleteFiles"} danger />
          <span className="min-w-0">
            <span className="block text-sm font-medium text-fg">Uninstall and delete files</span>
            <span className="mt-0.5 block text-xs leading-[1.5] text-fg-muted">
              Deletes {formatBytes(game.installedSizeBytes ?? game.installSizeBytes ?? 0)} from <PathText path={game.installPath} className="inline-flex" />.
              {shared && " Epic Games Launcher will lose the game too."}
            </span>
          </span>
        </RadioGroup.Item>
      </RadioGroup.Root>
      {error && <ErrorView className="mt-3" error={error.error} retry={error.retry} />}
    </AlertModal>
  );
}

/* -------------------------------------------------------------------- move */

function MoveDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const game = useGame(appName);
  const eglSync = useSnap((s) => s.settings.eglSync);
  const error = useUi((s) => s.errors.move);
  const moving = usePending(pendingKey("move", appName));
  const [base, setBase] = useState<string | null>(null);
  const [planned, setPlanned] = useState<{ base: string; plan: InstallPlan | null } | null>(null);
  const plan = planned && planned.base === base ? planned.plan : null;

  useEffect(() => () => useUi.getState().setError("move", undefined), []);
  useEffect(() => {
    if (!base) return;
    let alive = true;
    void actions.planInstall(appName, base).then((p) => alive && setPlanned({ base, plan: p ?? null }));
    return () => {
      alive = false;
    };
  }, [appName, base]);

  if (!game || !game.installPath) return null;
  const size = game.installedSizeBytes ?? game.installSizeBytes ?? 0;
  const pick = async () => {
    const p = await actions.pickFolder(base ?? game.installPath, `Choose where to move ${game.title}`);
    if (p) setBase(p);
  };
  // The destination folder name comes from Core (the plan's game folder), never from a guess.
  const folderName = plan?.gamePath.split(/[\\/]/).pop() ?? null;
  const target = base && plan && folderName ? `${base.replace(/[\\/]+$/, "")}\\${folderName}` : null;
  const enough = plan ? plan.freeBytes >= size : false;
  const egl = game.sharedWithEgl && !eglSync;

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && !moving && close()}
      title="Move install"
      description="Glider moves the files and updates its records. The game can't be played while it moves."
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={moving}>
            Cancel
          </Button>
          <Button variant="primary" loading={moving} disabled={!target || !enough} onClick={() => target && void actions.move(appName, base!)}>
            Move install
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3 pb-1">
        <div className="rounded-xl border border-border bg-surface-1 p-3.5">
          <p className="text-xs text-fg-muted">Now in</p>
          <PathText path={game.installPath} className="mt-0.5 text-sm font-medium" />
          <div className="my-3 h-px bg-border" />
          <div className="flex items-center justify-between gap-3">
            <div className="min-w-0 flex-1">
              <p className="text-xs text-fg-muted">Move to</p>
              {target ? <PathText path={target} className="mt-0.5 text-sm font-medium" /> : <p className="mt-0.5 text-sm text-fg-subtle">Choose a folder</p>}
            </div>
            <Button variant="secondary" onClick={() => void pick()}>
              {base ? "Change…" : "Choose…"}
            </Button>
          </div>
          {base && (
            <p className={cx("mt-3 text-xs tabular-nums", plan && !enough ? "text-danger-text" : "text-fg-muted")} aria-live="polite">
              {!plan
                ? "Checking free space…"
                : enough
                  ? `${formatBytes(size)} to move · ${formatBytes(plan.freeBytes)} free there`
                  : `Needs ${formatBytes(size)}, but only ${formatBytes(plan.freeBytes)} is free there.`}
            </p>
          )}
        </div>

        {egl && (
          <ErrorCallout
            tone="warning"
            title="Epic Games Launcher will lose track of this install"
            message="Keeping it in sync updates Epic Games Launcher with the new location after the move."
            secondary={
              <label className="inline-flex items-center gap-2 text-xs text-fg">
                <Switch aria-label="Keep Epic Games Launcher in sync" checked={eglSync} onCheckedChange={(v) => void actions.updateSettings({ eglSync: v })} />
                Keep Epic Games Launcher in sync
              </label>
            }
          />
        )}
        {error && <ErrorView error={error.error} retry={error.retry} />}
      </div>
    </Modal>
  );
}

/* ------------------------------------------------------------- cancel job */

function CancelJobDialog({ jobId, open }: { jobId: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const job = useApp((s) => s.snapshot?.queue.jobs.find((j) => j.id === jobId));
  if (!job) return null;
  const noun = `${JOB_NOUN[job.kind].toLowerCase()} of ${job.title}`;
  return (
    <AlertModal
      open={open}
      onOpenChange={(o) => !o && close()}
      title={`Cancel ${noun}?`}
      description={
        job.kind === "install" || job.kind === "update" || job.kind === "modify"
          ? "What's already downloaded stays on disk, so starting again picks up where this left off."
          : "The task stops where it is."
      }
      cancel={<Button variant="ghost">Keep going</Button>}
      action={
        <Button variant="danger" onClick={() => void actions.cancelJob(jobId)}>
          Cancel {noun}
        </Button>
      }
    />
  );
}

/* ------------------------------------------------------------------- error */

function ErrorDialog({ dialog, open }: { dialog: Extract<DialogState, { kind: "error" }>; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const { error, retry } = dialog;
  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && close()}
      title={error.title}
      description={error.message}
      footer={
        <>
          <Button variant="ghost" onClick={close}>
            Close
          </Button>
          {error.fix && (
            <Button
              variant="primary"
              onClick={() => {
                close();
                runFix(error.fix!, { retry, appName: error.appName });
              }}
            >
              {FIX_LABELS[error.fix]}
            </Button>
          )}
        </>
      }
    >
      {error.detail && <pre className="selectable max-h-32 overflow-auto whitespace-pre-wrap break-all rounded-md bg-surface-2 p-2.5 font-mono text-2xs leading-[1.5] text-fg-muted">{error.detail}</pre>}
    </Modal>
  );
}

/* -------------------------------------------------------------------- root */

export function Dialogs() {
  const dialog = useUi((s) => s.dialog);
  // Keep the last dialog mounted while Radix plays its exit animation.
  const [last, setLast] = useState<DialogState | null>(dialog);
  if (dialog && dialog !== last) setLast(dialog);
  useEffect(() => {
    if (dialog) return;
    const t = setTimeout(() => setLast(null), 220);
    return () => clearTimeout(t);
  }, [dialog]);

  const open = dialog !== null;
  switch (last?.kind) {
    case "install":
      return <InstallDialog appName={last.appName} open={open} />;
    case "uninstall":
      return <UninstallDialog appName={last.appName} open={open} />;
    case "move":
      return <MoveDialog appName={last.appName} open={open} />;
    case "cancelJob":
      return <CancelJobDialog jobId={last.jobId} open={open} />;
    case "error":
      return <ErrorDialog dialog={last} open={open} />;
    default:
      return null;
  }
}
