import { useMemo } from "react";
import type { GameSummary } from "../ipc/contract.gen";
import { useLibrary } from "./library";

/**
 * In-memory search / filter / sort index over the library. Built once per library change (a few ms for 600 games) and
 * queried synchronously on every keystroke: each query is one pass over a pre-sorted array (no sorting at query time),
 * so it answers in well under a frame (16 ms) for several hundred games.
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
  /** Position in the A–Z order; the stable tie-breaker everywhere. */
  az: number;
}

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
    case "notInstalled": return !g.hidden && g.installStatus === "notInstalled";
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

export class LibraryIndex {
  readonly size: number;
  readonly counts: LibraryCounts;
  /** Items by sort order, base games and add-ons together (queries filter by kind). */
  private readonly orders: Record<LibrarySort, Entry[]>;
  private readonly dlcByBase = new Map<string, GameSummary[]>();
  private readonly byApp = new Map<string, GameSummary>();

  constructor(games: Iterable<GameSummary>) {
    const entries: Entry[] = [];
    for (const g of games) {
      const key = normalize(g.title);
      entries.push({
        g,
        key,
        words: key.split(" "),
        dev: g.developer ? normalize(g.developer) : "",
        added: time(g.addedAt),
        played: time(g.lastPlayedAt),
        size: g.installedSizeBytes ?? g.installSizeBytes ?? 0,
        az: 0,
      });
      this.byApp.set(g.appName, g);
    }
    const az = entries.sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : a.g.appName < b.g.appName ? -1 : 1));
    az.forEach((e, i) => (e.az = i));
    const by = (cmp: (a: Entry, b: Entry) => number): Entry[] => [...az].sort((a, b) => cmp(a, b) || a.az - b.az);
    this.orders = {
      az,
      recent: by((a, b) => b.played - a.played),
      added: by((a, b) => b.added - a.added),
      size: by((a, b) => b.size - a.size),
    };
    this.size = az.length;

    const counts: LibraryCounts = { all: 0, installed: 0, notInstalled: 0, updates: 0, cloudSaves: 0, favorites: 0, hidden: 0 };
    for (const e of az) {
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
    return this.byApp.get(appName);
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

/** The index for one library state, built on first use and shared by every selector until the library changes. */
export function getLibraryIndex(games: ReadonlyMap<string, GameSummary>): LibraryIndex {
  let idx = cache.get(games);
  if (!idx) {
    idx = new LibraryIndex(games.values());
    cache.set(games, idx);
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
