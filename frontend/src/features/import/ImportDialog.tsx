import { FolderSearch } from "lucide-react";
import { plural } from "../../state/format";
import { useApp } from "../../state/store";
import { Button, Modal } from "../../ui";
import { ImportList } from "./ImportList";
import { useImportSession } from "./useImportSession";

/** "Import games…" from Settings: the same list and results as onboarding's Find games step, in a dialog. */
export function ImportDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const session = useImportSession("settings");
  const launcherInstalled = useApp((s) => s.snapshot!.egl.launcherInstalled);
  const { phase } = session;
  const busy = phase === "importing";
  const close = (next: boolean) => {
    if (!next && phase === "done") session.reset();
    onOpenChange(next);
  };
  return (
    <Modal
      open={open}
      onOpenChange={close}
      size="lg"
      title="Import games"
      footer={
        <>
          <Button variant="ghost" className="mr-auto" icon={<FolderSearch size={14} />} loading={session.locating} disabled={busy || phase === "scanning"} onClick={() => void session.locate()}>
            Locate folder…
          </Button>
          <Button variant="secondary" onClick={() => close(false)}>
            {phase === "done" ? "Done" : "Close"}
          </Button>
          {phase === "list" && session.groups.ready.length > 0 && (
            <Button variant="primary" disabled={session.selectedCount === 0 || !session.signedIn} loading={session.starting} onClick={() => void session.start()}>
              {session.selectedCount === 0 ? "Import" : `Import ${plural(session.selectedCount, "game")}`}
            </Button>
          )}
        </>
      }
    >
      <ImportList session={session} surface="settings" launcherInstalled={launcherInstalled} withSummary />
    </Modal>
  );
}
