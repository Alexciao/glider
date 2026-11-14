import { create } from "zustand";
import type { StoreTarget } from "../ipc/contract.gen";
import { navigate } from "./router";

/**
 * Where the Store page should land the next time it shows (a product page from "Free this week", the free-games page, …).
 * The Store page reads and clears it when it opens (`takeStoreTarget`), then calls `store.show` with it. It lives apart
 * from the router so deep links from Home don't need a second route shape.
 */
interface StoreTargetStore {
  target: StoreTarget | null;
}

export const useStoreTarget = create<StoreTargetStore>(() => ({ target: null }));

/** Go to the Store, landing on `target` (omit for its home page). */
export function openStore(target?: StoreTarget | null): void {
  useStoreTarget.setState({ target: target ?? null });
  navigate("store");
}

/** Read and clear the pending target (the Store page calls this once when it opens). */
export function takeStoreTarget(): StoreTarget | null {
  const { target } = useStoreTarget.getState();
  if (target) useStoreTarget.setState({ target: null });
  return target;
}

/** The deep link for a product slug on the Store. */
export const productTarget = (slug: string | null): StoreTarget => (slug ? { kind: "product", slug } : { kind: "freeGames", slug: null });
