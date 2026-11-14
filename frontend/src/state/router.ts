import { create } from "zustand";
import type { StoreTarget } from "../ipc/contract.gen";

/**
 * A tiny hash router. Routes are plain strings so the host can send them in the `navigate` event
 * ("game/Fortnite", "downloads", …) and glider:// links map onto them one to one.
 *
 *   home · library · game/<appName> · downloads · store · settings
 *   store/product/<slug> · store/bundle/<slug> · store/free-games · store/wishlist · store/browse   (a typed Store destination)
 */
export type Route =
  | { page: "home" }
  | { page: "library" }
  | { page: "game"; appName: string }
  | { page: "downloads" }
  | { page: "store"; target?: StoreTarget }
  | { page: "settings" };

export type Page = Route["page"];

export const DEFAULT_ROUTE: Route = { page: "home" };

const SIMPLE = new Set<Page>(["home", "library", "downloads", "store", "settings"]);

/** Accepts "game/Fortnite", "/game/Fortnite", "#/game/Fortnite" and "#game/Fortnite"; anything unknown → null. */
export function parseRoute(input: string | null | undefined): Route | null {
  if (!input) return null;
  const path = input.replace(/^#?\/?/, "").replace(/\/+$/, "");
  if (!path) return null;
  const [head, ...rest] = path.split("/");
  if (head === "game") {
    const name = decodeURIComponent(rest.join("/"));
    return name ? { page: "game", appName: name } : null;
  }
  if (head === "store" && rest.length > 0) return parseStore(rest);
  if (rest.length === 0 && SIMPLE.has(head as Page)) return { page: head as Exclude<Page, "game"> } as Route;
  return null;
}

const SLUG = /^[a-z0-9][a-z0-9-]{0,127}$/;

function parseStore(rest: string[]): Route | null {
  const [kind, slug, ...more] = rest;
  if (more.length > 0) return null;
  if ((kind === "product" || kind === "bundle") && slug) {
    const clean = decodeURIComponent(slug).toLowerCase();
    return SLUG.test(clean) ? { page: "store", target: { kind, slug: clean } } : null;
  }
  if (slug) return null;
  if (kind === "free-games") return { page: "store", target: { kind: "freeGames", slug: null } };
  if (kind === "wishlist" || kind === "browse") return { page: "store", target: { kind, slug: null } };
  return null;
}

/** The route that opens the Store at a typed target (never a raw URL). */
export function storeRoute(target: StoreTarget | null | undefined): Route {
  return target ? { page: "store", target } : { page: "store" };
}

/** "game/Fortnite"; appNames are percent-encoded so odd characters can't break the hash. */
export function formatRoute(r: Route): string {
  if (r.page === "game") return `game/${encodeURIComponent(r.appName)}`;
  if (r.page === "store" && r.target) {
    const t = r.target;
    if ((t.kind === "product" || t.kind === "bundle") && t.slug) return `store/${t.kind}/${encodeURIComponent(t.slug)}`;
    if (t.kind === "freeGames") return "store/free-games";
    if (t.kind === "wishlist" || t.kind === "browse") return `store/${t.kind}`;
  }
  return r.page;
}

export const sameRoute = (a: Route, b: Route): boolean => a.page === b.page && formatRoute(a) === formatRoute(b);

const readHash = (): Route => parseRoute(typeof location === "undefined" ? "" : location.hash) ?? DEFAULT_ROUTE;

interface RouterStore {
  route: Route;
  /** Where "Back" goes from a game page (the page the user came from). */
  previous: Route | null;
  go: (route: Route | string) => void;
  /** Called by the hashchange listener; never writes the hash. */
  sync: (route: Route) => void;
}

export const useRouter = create<RouterStore>((set, get) => ({
  route: readHash(),
  previous: null,
  go: (target) => {
    const route = typeof target === "string" ? parseRoute(target) : target;
    if (!route) return;
    const cur = get().route;
    if (sameRoute(cur, route)) return;
    set({ route, previous: cur });
    if (typeof location !== "undefined") {
      const hash = `#/${formatRoute(route)}`;
      if (location.hash !== hash) history.pushState(null, "", hash);
    }
  },
  sync: (route) => {
    const cur = get().route;
    if (!sameRoute(cur, route)) set({ route, previous: cur });
  },
}));

let installed = false;

/** Listens to the browser's back/forward and manual hash edits. Idempotent; returns an uninstaller for tests. */
export function installRouter(): () => void {
  if (installed || typeof window === "undefined") return () => {};
  installed = true;
  const onChange = () => useRouter.getState().sync(readHash());
  window.addEventListener("hashchange", onChange);
  window.addEventListener("popstate", onChange);
  // The first paint may have happened with a hash the store was created with: re-read it once.
  onChange();
  return () => {
    window.removeEventListener("hashchange", onChange);
    window.removeEventListener("popstate", onChange);
    installed = false;
  };
}

export const useRoute = (): Route => useRouter((s) => s.route);
export const usePage = (): Page => useRouter((s) => s.route.page);

/** Imperative helpers for actions and non-React code. */
export const navigate = (route: Route | string): void => useRouter.getState().go(route);
export const goGame = (appName: string): void => navigate({ page: "game", appName });
