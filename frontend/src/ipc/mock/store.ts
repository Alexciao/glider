import type { FreeGamesResult, StoreState, StoreTarget } from "../contract.gen";
import { generatedArt } from "./art";

/** The state before the native view exists (and after it is closed). Mirrors Core's `StoreService.Default`. */
export const idleStore = (): StoreState => ({
  created: false,
  visible: false,
  loading: false,
  canGoBack: false,
  title: null,
  phase: "idle",
  url: null,
  host: null,
  secure: false,
  canGoForward: false,
  checkoutActive: false,
  covered: false,
  coverUrl: null,
  error: null,
});

const PATHS: Record<StoreTarget["kind"], string> = {
  home: "/en-US/",
  freeGames: "/en-US/free-games",
  wishlist: "/en-US/wishlist",
  browse: "/en-US/browse",
  library: "/en-US/",
  lastVisited: "/en-US/",
  product: "/en-US/p/",
  bundle: "/en-US/bundles/",
};

/** What the native view would report for a target (origin + path only, like the real host). */
export function mockStoreLocation(target: StoreTarget | null): { url: string; title: string; deep: boolean } {
  const kind = target?.kind ?? "home";
  const slug = target?.slug?.replace(/[^a-z0-9-]/gi, "").toLowerCase() ?? "";
  const path = kind === "product" || kind === "bundle" ? (slug ? PATHS[kind] + slug : PATHS.freeGames) : PATHS[kind];
  const title = kind === "freeGames" ? "Free Games | Epic Games Store" : kind === "wishlist" ? "Wishlist | Epic Games Store" : slug ? `${slug.replace(/-/g, " ")} | Epic Games Store` : "Epic Games Store";
  return { url: `https://store.epicgames.com${path}`, title, deep: kind !== "home" && kind !== "library" && kind !== "lastVisited" };
}

/** A realistic "Free this week" snapshot: one live giveaway pair, one upcoming. `owned` marks the second one as already claimed. */
export function mockFreeGames(now: number, iso: (t: number) => string, ownedTitle: string | null): FreeGamesResult {
  const DAY = 86_400_000;
  const art = (k: string) => ({ imageUrl: generatedArt(k).wide ?? "", tallImageUrl: generatedArt(k).tall });
  return {
    fetchedAt: iso(now),
    stale: false,
    items: [
      { id: "free-1", title: "Lanternfall", ...art("free-1"), slug: "lanternfall", startsAt: iso(now - 2 * DAY), endsAt: iso(now + 4 * DAY), status: "freeNow", owned: false, regularPrice: "$14.99", seller: "Moth Lantern Games", bundle: false },
      { id: "free-2", title: ownedTitle ?? "Orchard Tactics", ...art("free-2"), slug: "orchard-tactics", startsAt: iso(now - 1 * DAY), endsAt: iso(now + 5 * DAY), status: "freeNow", owned: ownedTitle !== null, regularPrice: "$19.99", seller: "Pomona Studio", bundle: false },
      { id: "free-3", title: "Saltmarsh Courier", ...art("free-3"), slug: "saltmarsh-courier", startsAt: iso(now + 4 * DAY), endsAt: iso(now + 11 * DAY), status: "upcoming", owned: false, regularPrice: "$9.99", seller: null, bundle: false },
    ],
  };
}
