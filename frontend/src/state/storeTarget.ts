import type { StoreTarget } from "../ipc/contract.gen";
import { navigate, storeRoute } from "./router";

/**
 * Go to the Store, landing on `target` (omit for its home page). The route carries the target (`store/product/<slug>`,
 * `store/free-games`, …): the Store page reads it from there and passes it to `store.show`, so a deep link from Home, a
 * `glider://store/p/<slug>` link and a Store link clicked inside the Store all land the same way and survive a reload.
 */
export function openStore(target?: StoreTarget | null): void {
  navigate(storeRoute(target));
}

/** The deep link for a product slug on the Store (the free-games page when the feed gave no slug). */
export const productTarget = (slug: string | null): StoreTarget => (slug ? { kind: "product", slug } : { kind: "freeGames", slug: null });
