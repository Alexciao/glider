import { useCallback } from "react";
import { create } from "zustand";
import { navigate } from "./router";
import { getSnapshot, useSnap } from "./store";
import { LIBRARY_FILTERS, LIBRARY_SORTS, type LibraryFilter, type LibrarySort } from "./libraryIndex";

/**
 * What the user last chose in the Library (view, sort, filter, collection), remembered per Epic account in
 * localStorage. Storage can be missing, blocked or full (private windows, cleared site data, thumbnail capture), so
 * every access is wrapped: the library always works, it just forgets.
 */
export type LibraryView = "grid" | "list";

export interface LibraryPrefs {
  view: LibraryView;
  sort: LibrarySort;
  filter: LibraryFilter;
  /** A collection id, or "all". A collection that no longer exists is treated as "all" by the page. */
  collectionId: string;
}

export const DEFAULT_PREFS: LibraryPrefs = { view: "grid", sort: "az", filter: "all", collectionId: "all" };

const KEY = "glider.library.prefs.v1";
const storageKey = (user: string): string => `${KEY}.${user}`;

/** Accepts anything (corrupt JSON, an older shape) and returns a valid prefs object. */
export function sanitizePrefs(raw: unknown): LibraryPrefs {
  const o = (typeof raw === "object" && raw !== null ? raw : {}) as Record<string, unknown>;
  return {
    view: o.view === "list" ? "list" : "grid",
    sort: (LIBRARY_SORTS as readonly unknown[]).includes(o.sort) ? (o.sort as LibrarySort) : DEFAULT_PREFS.sort,
    filter: (LIBRARY_FILTERS as readonly unknown[]).includes(o.filter) ? (o.filter as LibraryFilter) : DEFAULT_PREFS.filter,
    collectionId: typeof o.collectionId === "string" && o.collectionId ? o.collectionId : "all",
  };
}

export function readPrefs(user: string): LibraryPrefs {
  try {
    const text = localStorage.getItem(storageKey(user));
    return text ? sanitizePrefs(JSON.parse(text)) : DEFAULT_PREFS;
  } catch {
    return DEFAULT_PREFS;
  }
}

function writePrefs(user: string, prefs: LibraryPrefs): void {
  try {
    localStorage.setItem(storageKey(user), JSON.stringify(prefs));
  } catch {
    /* quota or blocked: remembered for this session only */
  }
}

interface PrefsStore {
  byUser: Record<string, LibraryPrefs>;
  patch: (user: string, patch: Partial<LibraryPrefs>) => void;
}

export const useLibraryPrefsStore = create<PrefsStore>((set, get) => ({
  byUser: {},
  patch: (user, patch) => {
    const next = { ...(get().byUser[user] ?? readPrefs(user)), ...patch };
    writePrefs(user, next);
    set((s) => ({ byUser: { ...s.byUser, [user]: next } }));
  },
}));

/** The current account's Library preferences and a setter. */
export function useLibraryPrefs(): [LibraryPrefs, (patch: Partial<LibraryPrefs>) => void] {
  const user = useSnap((s) => s.auth.account?.accountId ?? "anonymous");
  const cached = useLibraryPrefsStore((s) => s.byUser[user]);
  const prefs = cached ?? readPrefs(user);
  const patch = useLibraryPrefsStore((s) => s.patch);
  const set = useCallback((p: Partial<LibraryPrefs>) => patch(user, p), [patch, user]);
  return [prefs, set];
}

/** Opens the Library with these preferences applied (Home's "See updates" → the Updates filter). */
export function showInLibrary(patch: Partial<LibraryPrefs>): void {
  const user = getSnapshot()?.auth.account?.accountId ?? "anonymous";
  useLibraryPrefsStore.getState().patch(user, patch);
  navigate("library");
}

export function resetLibraryPrefsForTests(): void {
  useLibraryPrefsStore.setState({ byUser: {} });
}
