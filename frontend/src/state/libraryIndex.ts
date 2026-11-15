import { useMemo } from "react";
import type { GameSummary } from "../ipc/contract.gen";
import { useLibrary } from "./library";

/**
 * In-memory search / filter / sort index over the library. Built once per library change and queried synchronously on every
 * keystroke: each query is one pass over a pre-sorted array (no sorting at query time), so it answers in well under a frame
 * (16 ms) for several hundred games. A library delta does not rebuild it from scratch: the next index reuses the previous one's
 * entries for every game whose summary is the same object (the store keeps unchanged games' identity), and when no game's
 * sort position changed (a favorite, a job tick, playtime) it only swaps the changed entries into copies of the sorted arrays.
 */

export type LibraryFilter = "all" | "installed" | "notInstalled" | "updates" | "cloudSaves" | "favorites" | "hidden";
export type LibrarySort = "az" | "recent" | "added" | "size";

export const LIBRARY_FILTERS: readonly LibraryFilter[] = ["all", "installed", "notInstalled", "updates", "cloudSaves", "favorites", "hidden"];
export const LIBRARY_SORTS: readonly LibrarySort[] = ["az", "recent", "added", "size"];

export interface LibraryQuery {
  /** Free text over title, developer and add-on names. Every word must match (prefix, word-start or substring). */
  search?: string;
  filter?: LibraryFilter;
  sort?: LibrarySort;
  /** Only these appNames (a collection's members). */
  collection?: readonly string[] | ReadonlySet<string> | null;
  /** Include content DLC and launchable add-ons as their own entries (default false: base games only). */
  includeDlc?: boolean;
}

export interface LibraryCounts {
  /** Visible base games (not hidden). */
  all: number;
  installed: number;
  notInstalled: number;
  updates: number;
  cloudSaves: number;
  favorites: number;
  hidden: number;
}

interface Entry {
  g: GameSummary;
  /** Normalized title: lower case, diacritics stripped, punctuation → single spaces. */
  key: string;
  words: string[];
  /** Normalized developer. */
  dev: string;
  added: number;
  played: number;
  size: number;
  /** Position in each order (SORTS); az is the stable tie-breaker everywhere. Only written while an index is being built. */
  pos: number[];
}

const SORTS: readonly LibrarySort[] = LIBRARY_SORTS;

/** "Brütal Légends: 2" → "brutal legends 2". */
export function normalize(s: string): string {
  return s
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

const time = (iso: string | null): number => {
  if (!iso) return 0;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? 0 : t;
};

const isBase = (g: GameSummary): boolean => g.kind === "game";

function matches(g: GameSummary, filter: LibraryFilter): boolean {
  switch (filter) {
    case "all": return !g.hidden;
    case "installed": return !g.hidden && (g.installStatus === "installed" || g.installStatus === "broken");
    // Everything that is not on this PC, including titles that play through another launcher: Installed + Not installed = All games.
    case "notInstalled": return !g.hidden && (g.installStatus === "notInstalled" || g.installStatus === "notInstallable");
    case "updates": return !g.hidden && g.installStatus === "installed" && g.updateAvailable;
    case "cloudSaves": return !g.hidden && g.cloudSaves;
    case "favorites": return !g.hidden && g.favorite;
    case "hidden": return g.hidden;
  }
}

/** Score of one query word against an entry; 0 = no match. */
function wordScore(e: Entry, w: string): number {
  if (e.key.startsWith(w)) return 4;
  for (const word of e.words) if (word.startsWith(w)) return 3;
  if (e.key.includes(w)) return 2;
  if (e.dev.includes(w)) return 1;
  return 0;
}

function makeEntry(g: GameSummary, old?: Entry): Entry {
  const sameTitle = old !== undefined && old.g.title === g.title;
  const key = sameTitle ? old.key : normalize(g.title);
  return {
    g,
    key,
    words: sameTitle ? old.words : key.split(" "),
    dev: old !== undefined && old.g.developer === g.developer ? old.dev : g.developer ? normalize(g.developer) : "",
    added: time(g.addedAt),
    played: time(g.lastPlayedAt),
    size: g.installedSizeBytes ?? g.installSizeBytes ?? 0,
    pos: [0, 0, 0, 0],
  };
}

const sortKeysEqual = (a: Entry, b: Entry): boolean => a.key === b.key && a.added === b.added && a.played === b.played && a.size === b.size;

export class LibraryIndex {
  readonly size: number;
  readonly counts: LibraryCounts;
  /** Items by sort order, base games and add-ons together (queries filter by kind). */
  private readonly orders: Record<LibrarySort, Entry[]>;
  private readonly dlcByBase = new Map<string, GameSummary[]>();
  private readonly entries: ReadonlyMap<string, Entry>;
  /** Entries built for a new or changed summary (the rest were taken over from the previous index): tests. */
  readonly rebuilt: number;

  /** `previous`: an earlier index to build on (any library state works; the closer, the cheaper). */
  constructor(games: Iterable<GameSummary>, previous?: LibraryIndex) {
    let entries: Entry[] = [];
    const swapped: Entry[] = []; // same game, new summary, same sort position
    let reorder = previous === undefined;
    let carried = 0; // games that were in the previous index
    let rebuilt = 0;
    for (const g of games) {
      const old = previous?.entries.get(g.appName);
      if (old === undefined) {
        reorder = true;
        rebuilt++;
        entries.push(makeEntry(g));
        continue;
      }
      carried++;
      if (old.g === g) {
        entries.push(old);
        continue;
      }
      rebuilt++;
      const e = makeEntry(g, old);
      if (sortKeysEqual(e, old)) {
        e.pos = old.pos; // the same position in every order: nothing to sort
        swapped.push(e);
      } else {
        reorder = true;
      }
      entries.push(e);
    }
    if (previous !== undefined && previous.size !== carried) reorder = true; // something was removed
    this.rebuilt = rebuilt;
    this.size = entries.length;

    let orders: Record<LibrarySort, Entry[]>;
    if (!reorder && previous !== undefined) {
      const patch = (i: number): Entry[] => {
        if (swapped.length === 0) return previous.orders[SORTS[i]!];
        const out = previous.orders[SORTS[i]!].slice();
        for (const e of swapped) out[e.pos[i]!] = e;
        return out;
      };
      orders = { az: patch(0), recent: patch(1), added: patch(2), size: patch(3) };
    } else {
      // Entries are never changed once an index is built (the previous index may still be in use): take new positions on copies.
      entries = entries.map((e) => ({ ...e, pos: [0, 0, 0, 0] }));
      const az = entries.sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : a.g.appName < b.g.appName ? -1 : 1));
      az.forEach((e, i) => (e.pos[0] = i));
      const by = (i: number, cmp: (a: Entry, b: Entry) => number): Entry[] => {
        const sorted = [...az].sort((a, b) => cmp(a, b) || a.pos[0]! - b.pos[0]!);
        sorted.forEach((e, n) => (e.pos[i] = n));
        return sorted;
      };
      orders = {
        az,
        recent: by(1, (a, b) => b.played - a.played),
        added: by(2, (a, b) => b.added - a.added),
        size: by(3, (a, b) => b.size - a.size),
      };
    }
    this.orders = orders;
    this.entries = !reorder && previous !== undefined && swapped.length === 0 ? previous.entries : new Map(orders.az.map((e) => [e.g.appName, e]));

    const counts: LibraryCounts = { all: 0, installed: 0, notInstalled: 0, updates: 0, cloudSaves: 0, favorites: 0, hidden: 0 };
    for (const e of orders.az) {
      const g = e.g;
      if (!isBase(g)) {
        if (g.baseAppName) {
          const list = this.dlcByBase.get(g.baseAppName);
          if (list) list.push(g);
          else this.dlcByBase.set(g.baseAppName, [g]);
        }
        continue;
      }
      if (g.hidden) counts.hidden++;
      else counts.all++;
      for (const f of LIBRARY_FILTERS) if (f !== "all" && f !== "hidden" && matches(g, f)) counts[f]++;
    }
    this.counts = counts;
  }

  get(appName: string): GameSummary | undefined {
    return this.entries.get(appName)?.g;
  }

  /** Owned DLC / add-ons of a base game, A–Z. */
  dlcOf(baseAppName: string): readonly GameSummary[] {
    return this.dlcByBase.get(baseAppName) ?? [];
  }

  /** Filter + search + sort in one pass. Returns the summaries in display order. */
  query(q: LibraryQuery = {}): GameSummary[] {
    const filter = q.filter ?? "all";
    const order = this.orders[q.sort ?? "az"];
    const words = q.search ? normalize(q.search).split(" ").filter(Boolean) : [];
    const allow = q.collection ? (q.collection instanceof Set ? (q.collection as ReadonlySet<string>) : new Set(q.collection)) : null;
    const includeDlc = q.includeDlc === true;

    const out: GameSummary[] = [];
    if (words.length === 0) {
      for (const e of order) {
        const g = e.g;
        if (!includeDlc && !isBase(g)) continue;
        if (allow && !allow.has(g.appName)) continue;
        if (matches(g, filter)) out.push(g);
      }
      return out;
    }

    const scored: { e: Entry; score: number; pos: number }[] = [];
    let pos = 0;
    const whole = words.join(" ");
    for (const e of order) {
      const g = e.g;
      pos++;
      if (!includeDlc && !isBase(g)) continue;
      if (allow && !allow.has(g.appName)) continue;
      if (!matches(g, filter)) continue;
      let score = 0;
      for (const w of words) {
        const s = wordScore(e, w);
        if (s === 0) {
          score = 0;
          break;
        }
        score += s;
      }
      if (score === 0) continue;
      if (e.key === whole) score += 8;
      else if (e.key.startsWith(whole)) score += 2;
      scored.push({ e, score, pos });
    }
    scored.sort((a, b) => b.score - a.score || a.pos - b.pos);
    for (const s of scored) out.push(s.e.g);
    return out;
  }

  /** Installed games, most recently played first. */
  recentlyPlayed(limit = 12): GameSummary[] {
    const out: GameSummary[] = [];
    for (const e of this.orders.recent) {
      if (e.played === 0 || out.length >= limit) break;
      if (isBase(e.g) && !e.g.hidden && e.g.installStatus === "installed") out.push(e.g);
    }
    return out;
  }

  /** Installed games with an update pending, A–Z. */
  updates(): GameSummary[] {
    return this.query({ filter: "updates" });
  }
}

const cache = new WeakMap<ReadonlyMap<string, GameSummary>, LibraryIndex>();
let latest: LibraryIndex | undefined;

/**
 * The index for one library state, built on first use and shared by every selector until the library changes. A new state is
 * built on the most recent index: a delta costs a pass over the games (reference checks) plus the games that changed.
 */
export function getLibraryIndex(games: ReadonlyMap<string, GameSummary>): LibraryIndex {
  let idx = cache.get(games);
  if (!idx) {
    idx = new LibraryIndex(games.values(), latest);
    cache.set(games, idx);
    latest = idx;
  }
  return idx;
}

/** The memoized index; components re-render only when the library actually changed. */
export function useLibraryIndex(): LibraryIndex {
  const games = useLibrary((s) => s.games);
  return getLibraryIndex(games);
}

/** Stable key for a query, so identical queries share a memo entry. */
function queryKey(q: LibraryQuery): string {
  const c = q.collection ? [...q.collection].join(",") : "";
  return `${q.search ?? ""}|${q.filter ?? "all"}|${q.sort ?? "az"}|${q.includeDlc ? 1 : 0}|${c}`;
}

/** Query results, recomputed only when the library or the query changes. */
export function useLibraryQuery(q: LibraryQuery): GameSummary[] {
  const index = useLibraryIndex();
  const key = queryKey(q);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- `key` captures every field of `q`
  return useMemo(() => index.query(q), [index, key]);
}

export function useLibraryCounts(): LibraryCounts {
  return useLibraryIndex().counts;
}

export function useRecentlyPlayed(limit = 12): GameSummary[] {
  const index = useLibraryIndex();
  return useMemo(() => index.recentlyPlayed(limit), [index, limit]);
}

export function useDlcOf(baseAppName: string | undefined): readonly GameSummary[] {
  const index = useLibraryIndex();
  return baseAppName ? index.dlcOf(baseAppName) : [];
}
