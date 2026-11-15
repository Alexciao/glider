import { create } from "zustand";
import type { StoreTarget } from "../ipc/contract.gen";

/**
 * A tiny hash router. Routes are plain strings so the host can send them in the `navigate` event
 * ("game/Fortnite", "downloads", …) and glider:// links map onto them one to one.
 *
 *   home · library · game/<appName> · downloads · settings · store
 *   store/product/<slug> · store/bundle/<slug> · store/free-games · store/wishlist · store/browse   (a typed Store destination)
 *   store/p/<slug>  (legacy alias of store/product/<slug>: what `glider://store/p/<slug>` links and older hosts send)
 *   settings?focus=downloadMode | settings?focus=speedLimit   (Settings → Downloads, with that row highlighted)
 *
 * `game/<appName>/install` (glider://install/<app>) shows the game page; {@link installIntent} tells the bridge to open its
 * install dialog on top. A link never starts a download by itself.
 */
export type Route =
  | { page: "home" }
  | { page: "library" }
  | { page: "game"; appName: string }
  | { page: "downloads" }
  | { page: "store"; target?: StoreTarget }
  | { page: "settings"; focus?: SettingsFocus };

/** A Settings row a link can point at: the page opens on its section, scrolls to the row and gives it a brief highlight. */
export type SettingsFocus = "downloadMode" | "speedLimit";
const SETTINGS_FOCUS: readonly SettingsFocus[] = ["downloadMode", "speedLimit"];

export type Page = Route["page"];

export const DEFAULT_ROUTE: Route = { page: "home" };

const SIMPLE = new Set<Page>(["home", "library", "downloads", "store", "settings"]);

/** Accepts "game/Fortnite", "/game/Fortnite", "#/game/Fortnite" and "#game/Fortnite"; anything unknown → null. */
export function parseRoute(input: string | null | undefined): Route | null {
  if (!input) return null;
  const [pathPart = "", query] = input.replace(/^#?\/?/, "").split("?", 2);
  const path = pathPart.replace(/\/+$/, "");
  if (!path) return null;
  const [head, ...rest] = path.split("/");
  if (head === "settings" && rest.length === 0) {
    const focus = new URLSearchParams(query ?? "").get("focus");
    return SETTINGS_FOCUS.includes(focus as SettingsFocus) ? { page: "settings", focus: focus as SettingsFocus } : { page: "settings" };
  }
  if (head === "game") {
    if (rest.length === 2 && rest[1] !== "install") return null;
    if (rest.length < 1 || rest.length > 2) return null;
    const name = safeDecode(rest[0]);
    return name ? { page: "game", appName: name } : null;
  }
  if (head === "store" && rest.length > 0) return parseStore(rest);
  if (rest.length === 0 && SIMPLE.has(head as Page)) return { page: head as Exclude<Page, "game"> } as Route;
  return null;
}

function safeDecode(part: string | undefined): string | null {
  if (part === undefined) return null;
  try {
    return decodeURIComponent(part);
  } catch {
    return null;
  }
}

/** The app whose install dialog a `game/<app>/install` route asks for, else null. */
export function installIntent(input: string | null | undefined): string | null {
  const route = parseRoute(input);
  if (route?.page !== "game") return null;
  return /\/install\/*$/.test(input ?? "") ? route.appName : null;
}

/** Epic's slug shape (the same rule Core's `StoreSlug` applies before it builds any address). */
const SLUG = /^[a-z0-9][a-z0-9-]{0,127}$/;

function parseStore(rest: string[]): Route | null {
  const [kind, slug, ...more] = rest;
  if (more.length > 0) return null;
  // "p" is the alias used by glider://store/p/<slug> and the Epic launcher's own links.
  if ((kind === "product" || kind === "p" || kind === "bundle") && slug) {
    const clean = safeDecode(slug)?.toLowerCase();
    return clean && SLUG.test(clean) ? { page: "store", target: { kind: kind === "bundle" ? "bundle" : "product", slug: clean } } : null;
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
  if (r.page === "settings" && r.focus) return `settings?focus=${r.focus}`;
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

/** Settings has shown the row a link asked for: forget the request, so going back to Settings later doesn't replay it. */
export function consumeSettingsFocus(): void {
  const { route } = useRouter.getState();
  if (route.page !== "settings" || !route.focus) return;
  useRouter.setState({ route: { page: "settings" } });
  if (typeof location !== "undefined" && location.hash.includes("?")) history.replaceState(null, "", "#/settings");
}
export const goGame = (appName: string): void => navigate({ page: "game", appName });
