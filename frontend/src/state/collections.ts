import type { Collection } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { toGliderError } from "./actions";
import { toast } from "./notices";

/**
 * Collection edits. The backend answers with the changed collection and pushes the new snapshot, so there is no local
 * state to keep: the Library reads `snapshot.collections`. Failures are a toast (these are quick, global edits).
 */
async function attempt<T>(fn: () => Promise<T>, what: string): Promise<T | undefined> {
  try {
    return await fn();
  } catch (e) {
    const err = toGliderError(e);
    if (err.code !== "cancelled") toast({ level: "error", title: `Couldn't ${what}`, message: err.message, duration: 5000 });
    return undefined;
  }
}

export const collectionActions = {
  /** Creates a collection, optionally putting one game in it right away. Returns the new collection. */
  async create(name: string, appName?: string): Promise<Collection | undefined> {
    const created = await attempt(() => rpc().call("collections.create", { name: name.trim() }), "create the collection");
    if (created && appName) return (await collectionActions.setMember(created.id, appName, true)) ?? created;
    return created;
  },
  rename(id: string, name: string): Promise<Collection | undefined> {
    return attempt(() => rpc().call("collections.rename", { id, name: name.trim() }), "rename the collection");
  },
  async remove(id: string): Promise<boolean> {
    return (await attempt(async () => (await rpc().call("collections.delete", { id }), true), "delete the collection")) === true;
  },
  setMember(id: string, appName: string, member: boolean): Promise<Collection | undefined> {
    return attempt(() => rpc().call("collections.setMembership", { id, appName, member }), member ? "add the game" : "remove the game");
  },
};
