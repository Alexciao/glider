import { useEffect, useState } from "react";
import { actions, runFix } from "../../state/actions";
import { JOB_NOUN } from "../../state/derive";
import { useApp } from "../../state/store";
import { FIX_LABELS, useUi, type DialogState } from "../../state/ui";
import { AlertModal, Button, Modal } from "../../ui";
import { InstallDialog } from "../game/dialogs/InstallDialog";
import { MoveDialog } from "../game/dialogs/MoveDialog";
import { SaveConflictDialog } from "../game/dialogs/SaveConflictDialog";
import { UninstallDialog } from "../game/dialogs/UninstallDialog";

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

const RELEASES_URL = "https://github.com/Alexciao/glider/releases/latest";

function ErrorDialog({ dialog, open }: { dialog: Extract<DialogState, { kind: "error" }>; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const { error, retry } = dialog;
  // A damaged install can only be fixed by installing again: that is the primary, and the logs are the way to see why.
  const damaged = error.code === "legendaryMissing";
  const fix = error.fix && !(damaged && error.fix === "openLogs") ? error.fix : null;
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
          {damaged && (
            <Button
              variant="secondary"
              onClick={() => {
                close();
                runFix("openLogs", { appName: error.appName });
              }}
            >
              Open logs
            </Button>
          )}
          {damaged ? (
            <Button
              variant="primary"
              onClick={() => {
                close();
                void actions.openExternal(RELEASES_URL);
              }}
            >
              Download Glider
            </Button>
          ) : (
            fix && (
              <Button
                variant="primary"
                onClick={() => {
                  close();
                  runFix(fix, { retry, appName: error.appName });
                }}
              >
                {FIX_LABELS[fix]}
              </Button>
            )
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
    case "saveConflict":
      return <SaveConflictDialog appName={last.appName} conflict={last.conflict} thenPlay={last.thenPlay} open={open} />;
    case "cancelJob":
      return <CancelJobDialog jobId={last.jobId} open={open} />;
    case "error":
      return <ErrorDialog dialog={last} open={open} />;
    default:
      return null;
  }
}
