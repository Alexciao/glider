import { useState } from "react";
import { create } from "zustand";
import { collectionActions } from "../../state/collections";
import { useSnap } from "../../state/store";
import { Button, Modal, TextField } from "../../ui";

/**
 * One name prompt for creating and renaming collections, opened from any menu (`openCreate(appName)` also puts that game
 * in the new collection). Mounted once by the Library page.
 */
type Request = { mode: "create"; appName: string | null } | { mode: "rename"; id: string };

interface DialogStore {
  request: Request | null;
  openCreate: (appName?: string | null) => void;
  openRename: (id: string) => void;
  close: () => void;
}

export const useCollectionDialog = create<DialogStore>((set) => ({
  request: null,
  openCreate: (appName = null) => set({ request: { mode: "create", appName } }),
  openRename: (id) => set({ request: { mode: "rename", id } }),
  close: () => set({ request: null }),
}));

const MAX_NAME = 40;

function NameForm({ request, onDone }: { request: Request; onDone: () => void }) {
  const collections = useSnap((s) => s.collections);
  const current = request.mode === "rename" ? collections.find((c) => c.id === request.id) : undefined;
  const [name, setName] = useState(current?.name ?? "");
  const [busy, setBusy] = useState(false);
  const trimmed = name.trim();
  const taken = collections.some((c) => c.name.toLowerCase() === trimmed.toLowerCase() && c.id !== current?.id);
  const valid = trimmed.length > 0 && !taken;

  const submit = async () => {
    if (!valid || busy) return;
    setBusy(true);
    const done = request.mode === "create" ? await collectionActions.create(trimmed, request.appName ?? undefined) : await collectionActions.rename(request.id, trimmed);
    setBusy(false);
    if (done) onDone();
  };

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        void submit();
      }}
    >
      <TextField
        autoFocus
        aria-label="Collection name"
        placeholder="Collection name"
        maxLength={MAX_NAME}
        value={name}
        invalid={taken}
        onChange={(e) => setName(e.target.value)}
        wrapperClassName="w-full"
      />
      <p className="mt-2 min-h-4 text-xs text-danger-text">{taken ? "You already have a collection with that name." : ""}</p>
      <div className="mt-2 flex items-center justify-end gap-2 pb-2">
        <Button variant="ghost" onClick={onDone}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" disabled={!valid} loading={busy}>
          {request.mode === "create" ? "Create" : "Rename"}
        </Button>
      </div>
    </form>
  );
}

export function CollectionDialog() {
  const request = useCollectionDialog((s) => s.request);
  const close = useCollectionDialog((s) => s.close);
  return (
    <Modal
      open={request !== null}
      onOpenChange={(o) => !o && close()}
      title={request?.mode === "rename" ? "Rename collection" : "New collection"}
      description={request?.mode === "create" && request.appName ? "The game is added to it right away." : undefined}
      size="sm"
    >
      {request && <NameForm key={request.mode === "rename" ? request.id : "new"} request={request} onDone={close} />}
    </Modal>
  );
}
