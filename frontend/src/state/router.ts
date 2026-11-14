import { create } from "zustand";

/**
 * A tiny hash router. Routes are plain strings so the host can send them in the `navigate` event
 * ("game/Fortnite", "downloads", …) and glider:// links map onto them one to one.
 *
 *   home · library · game/<appName> · downloads · store · store/p/<slug> · settings
 *
 * `game/<appName>/install` (glider://install/<app>) shows the game page; {@link installIntent} tells the bridge to open its
 * install dialog on top. A link never starts a download by itself.
 */
export type Route =
  | { page: "home" }
  | { page: "library" }
  | { page: "game"; appName: string }
  | { page: "downloads" }
  | { page: "store"; /** Epic product slug from a glider://store/p/<slug> link; the Store view navigates there. */ slug?: string }
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
    if (rest.length === 2 && rest[1] !== "install") return null;
    if (rest.length < 1 || rest.length > 2) return null;
    const name = safeDecode(rest[0]);
    return name ? { page: "game", appName: name } : null;
  }
  if (head === "store" && rest.length === 2 && rest[0] === "p") {
    const slug = safeDecode(rest[1]);
    return slug && /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/.test(slug) ? { page: "store", slug } : null;
  }
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

/** "game/Fortnite"; appNames are percent-encoded so odd characters can't break the hash. */
export function formatRoute(r: Route): string {
  if (r.page === "game") return `game/${encodeURIComponent(r.appName)}`;
  return r.page === "store" && r.slug ? `store/p/${r.slug}` : r.page;
}

export const sameRoute = (a: Route, b: Route): boolean =>
  a.page === b.page &&
  (a.page !== "game" || (b.page === "game" && a.appName === b.appName)) &&
  (a.page !== "store" || (b.page === "store" && a.slug === b.slug));

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
