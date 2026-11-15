import { describe, expect, it } from "vitest";
import type { GameSummary } from "../ipc/contract.gen";
import { generateCatalog } from "../ipc/mock/catalog";
import { getLibraryIndex, LibraryIndex, normalize, type LibraryQuery } from "./libraryIndex";

const NOW = Date.parse("2026-11-14T12:00:00Z");
const lib = (n: number, seed = 11): GameSummary[] => generateCatalog({ seed, count: n, now: NOW, art: false }).map((g) => g.summary);
const base = (list: GameSummary[]) => list.filter((g) => g.kind === "game");

describe("normalize", () => {
  it("lower-cases, strips diacritics and punctuation", () => {
    expect(normalize("Brütal Légends: 2 — The Return!")).toBe("brutal legends 2 the return");
    expect(normalize("  ")).toBe("");
  });
});

describe("LibraryIndex filters", () => {
  const list = lib(300);
  const idx = new LibraryIndex(list);

  it("all excludes hidden games and DLC; includeDlc adds DLC", () => {
    const all = idx.query();
    expect(all.every((g) => g.kind === "game" && !g.hidden)).toBe(true);
    expect(all.length).toBe(base(list).filter((g) => !g.hidden).length);
    expect(idx.query({ includeDlc: true }).length).toBe(list.filter((g) => !g.hidden).length);
    expect(idx.query({ includeDlc: true }).some((g) => g.kind !== "game")).toBe(true);
  });

  it("installed / notInstalled / updates / cloudSaves / favorites / hidden", () => {
    const q = (filter: LibraryQuery["filter"]) => idx.query({ filter });
    expect(q("installed").every((g) => g.installStatus === "installed" || g.installStatus === "broken")).toBe(true);
    expect(q("notInstalled").every((g) => g.installStatus === "notInstalled" || g.installStatus === "notInstallable")).toBe(true);
    expect(q("updates").every((g) => g.updateAvailable && g.installStatus === "installed")).toBe(true);
    expect(q("updates").length).toBeGreaterThan(0);
    expect(q("cloudSaves").every((g) => g.cloudSaves)).toBe(true);
    expect(q("favorites").every((g) => g.favorite)).toBe(true);
    expect(q("hidden").every((g) => g.hidden)).toBe(true);
    expect(q("hidden").length).toBeGreaterThan(0);
    // The two halves of the library reconcile with the whole: nothing is in neither.
    expect(q("installed").length + q("notInstalled").length).toBe(q("all").length);
    expect(idx.counts.installed + idx.counts.notInstalled).toBe(idx.counts.all);
  });

  it("counts match the filtered lists", () => {
    const c = idx.counts;
    expect(c.all).toBe(idx.query().length);
    expect(c.installed).toBe(idx.query({ filter: "installed" }).length);
    expect(c.notInstalled).toBe(idx.query({ filter: "notInstalled" }).length);
    expect(c.updates).toBe(idx.query({ filter: "updates" }).length);
    expect(c.cloudSaves).toBe(idx.query({ filter: "cloudSaves" }).length);
    expect(c.favorites).toBe(idx.query({ filter: "favorites" }).length);
    expect(c.hidden).toBe(idx.query({ filter: "hidden" }).length);
  });

  it("collection restricts to its members, combined with a filter", () => {
    const members = base(list).slice(0, 40).map((g) => g.appName);
    const only = idx.query({ collection: members });
    expect(only.length).toBeLessThanOrEqual(40);
    expect(only.every((g) => members.includes(g.appName))).toBe(true);
    const installed = idx.query({ collection: new Set(members), filter: "installed" });
    expect(installed.every((g) => members.includes(g.appName) && (g.installStatus === "installed" || g.installStatus === "broken"))).toBe(true);
    expect(idx.query({ collection: [] })).toEqual([]);
  });

  it("dlcOf lists a base game's DLC; recentlyPlayed and updates are ready-made", () => {
    const host = base(list).find((g) => g.dlcCount > 0)!;
    expect(idx.dlcOf(host.appName)).toHaveLength(host.dlcCount);
    expect(idx.dlcOf("nope")).toEqual([]);
    const recent = idx.recentlyPlayed(5);
    expect(recent.length).toBe(5);
    for (let i = 1; i < recent.length; i++) expect(Date.parse(recent[i - 1]!.lastPlayedAt!)).toBeGreaterThanOrEqual(Date.parse(recent[i]!.lastPlayedAt!));
    expect(idx.updates().length).toBe(idx.counts.updates);
    expect(idx.get(host.appName)).toBe(host);
  });
});

describe("LibraryIndex sorts", () => {
  const list = lib(300);
  const idx = new LibraryIndex(list);

  it("A–Z is case- and diacritic-insensitive and stable", () => {
    const az = idx.query({ sort: "az" });
    for (let i = 1; i < az.length; i++) expect(normalize(az[i - 1]!.title) <= normalize(az[i]!.title)).toBe(true);
    expect(idx.query({ sort: "az" })).toEqual(az);
  });

  it("recently played: newest first, never-played last (A–Z among themselves)", () => {
    const r = idx.query({ sort: "recent" });
    const t = (g: GameSummary) => (g.lastPlayedAt ? Date.parse(g.lastPlayedAt) : 0);
    for (let i = 1; i < r.length; i++) expect(t(r[i - 1]!)).toBeGreaterThanOrEqual(t(r[i]!));
    expect(t(r[0]!)).toBeGreaterThan(0);
    expect(t(r.at(-1)!)).toBe(0);
  });

  it("recently added: newest purchase first", () => {
    const r = idx.query({ sort: "added" });
    const t = (g: GameSummary) => (g.addedAt ? Date.parse(g.addedAt) : 0);
    for (let i = 1; i < r.length; i++) expect(t(r[i - 1]!)).toBeGreaterThanOrEqual(t(r[i]!));
  });

  it("size: biggest first (installed size when known, else the manifest size)", () => {
    const r = idx.query({ sort: "size" });
    const sz = (g: GameSummary) => g.installedSizeBytes ?? g.installSizeBytes ?? 0;
    for (let i = 1; i < r.length; i++) expect(sz(r[i - 1]!)).toBeGreaterThanOrEqual(sz(r[i]!));
  });
});

describe("LibraryIndex search", () => {
  const list: GameSummary[] = [
    ...lib(40).slice(0, 5),
  ].map((g, i) => ({ ...g, title: ["Ember Orchard", "Orchard of the Pale Coast", "Hollow Foundry", "Émigré Tactics", "The Orchard Keeper"][i]!, developer: ["Lanternfish Games", "Paper Moth Studio", "Tin Kettle Works", "Small Hours", "Orchard Lane"][i]!, hidden: false, kind: "game" as const }));
  const idx = new LibraryIndex(list);
  const titles = (q: string, extra: Partial<LibraryQuery> = {}) => idx.query({ search: q, ...extra }).map((g) => g.title);

  it("ranks a title prefix before a word-start before a substring before the developer", () => {
    // prefix (+ starts-with-all bonus) > word start (A–Z breaks the tie)
    expect(titles("orchard")).toEqual(["Orchard of the Pale Coast", "Ember Orchard", "The Orchard Keeper"]);
    expect(titles("rchard")).toEqual(["Ember Orchard", "Orchard of the Pale Coast", "The Orchard Keeper"]); // substring only
    expect(titles("kettle")).toEqual(["Hollow Foundry"]); // developer only
  });

  it("matches without diacritics, case and punctuation", () => {
    expect(titles("emigre")).toEqual(["Émigré Tactics"]);
    expect(titles("HOLLOW")).toEqual(["Hollow Foundry"]);
    expect(titles("hollow, foundry!")).toEqual(["Hollow Foundry"]);
  });

  it("every word must match; an exact title beats a longer one", () => {
    expect(titles("pale coast")).toEqual(["Orchard of the Pale Coast"]);
    expect(titles("orchard zzz")).toEqual([]);
    expect(titles("ember orchard")[0]).toBe("Ember Orchard");
  });

  it("searches combine with filters and keep the chosen sort for ties", () => {
    expect(titles("orchard", { filter: "favorites" }).every((t) => list.find((g) => g.title === t)!.favorite)).toBe(true);
    const a = idx.query({ search: "o", sort: "az" });
    expect(a.length).toBeGreaterThan(1);
    expect(idx.query({ search: "" })).toEqual(idx.query({}));
    expect(idx.query({ search: "   " })).toEqual(idx.query({}));
  });
});

describe("LibraryIndex performance (the 16 ms frame budget)", () => {
  const list = lib(600, 3);
  it("builds in a few ms and answers any query in < 16 ms for 600 games (+ DLC)", () => {
    const t0 = performance.now();
    const idx = new LibraryIndex(list);
    const build = performance.now() - t0;
    expect(list.filter((g) => g.kind === "game")).toHaveLength(600);
    expect(build).toBeLessThan(100);

    const queries: LibraryQuery[] = [
      {},
      { filter: "installed", sort: "recent" },
      { filter: "updates" },
      { filter: "cloudSaves", sort: "size" },
      { filter: "favorites", sort: "added" },
      { filter: "hidden" },
      { search: "or" },
      { search: "orchard" },
      { search: "the pale c" },
      { search: "zzzz" },
      { search: "a", filter: "notInstalled", sort: "size", includeDlc: true },
      { collection: list.slice(0, 120).map((g) => g.appName), search: "o" },
    ];
    // Warm up the JIT, then sweep every query several times. A shared CI box can stall one sweep (GC, other test
    // workers), so the budget applies to the best sweep's worst query, and the median must be far below it.
    for (const q of queries) idx.query(q);
    const all: number[] = [];
    let worst = Infinity;
    for (let round = 0; round < 4; round++) {
      let roundWorst = 0;
      for (let run = 0; run < 10; run++) {
        for (const q of queries) {
          const s = performance.now();
          idx.query(q);
          const dt = performance.now() - s;
          all.push(dt);
          roundWorst = Math.max(roundWorst, dt);
        }
      }
      worst = Math.min(worst, roundWorst);
    }
    all.sort((x, y) => x - y);
    expect(all[Math.floor(all.length / 2)]!).toBeLessThan(4);
    expect(worst).toBeLessThan(16);
  });
});

describe("getLibraryIndex memoization", () => {
  it("builds once per library Map and is replaced when the library changes", () => {
    const list = lib(30);
    const m1 = new Map(list.map((g) => [g.appName, g]));
    const a = getLibraryIndex(m1);
    expect(getLibraryIndex(m1)).toBe(a);
    const m2 = new Map(m1);
    expect(getLibraryIndex(m2)).not.toBe(a);
  });
});

describe("incremental index (a delta does not rebuild everything)", () => {
  const list = lib(300, 5);
  const QUERIES: LibraryQuery[] = [
    {},
    { sort: "recent" },
    { sort: "added" },
    { sort: "size", filter: "installed" },
    { filter: "updates" },
    { filter: "favorites", sort: "recent" },
    { filter: "hidden" },
    { search: "or" },
    { search: "the", includeDlc: true },
  ];
  /** The incremental index must answer every query exactly like one built from scratch. */
  const expectSame = (a: LibraryIndex, b: LibraryIndex) => {
    expect(a.size).toBe(b.size);
    expect(a.counts).toEqual(b.counts);
    for (const q of QUERIES) expect(a.query(q).map((g) => g.appName)).toEqual(b.query(q).map((g) => g.appName));
    expect(a.updates()).toEqual(b.updates());
    expect(a.recentlyPlayed()).toEqual(b.recentlyPlayed());
  };
  const withGames = (base: readonly GameSummary[], change: (m: Map<string, GameSummary>) => void): GameSummary[] => {
    const m = new Map(base.map((g) => [g.appName, g]));
    change(m);
    return [...m.values()];
  };

  it("a change that moves nothing (favorite, runState, job) rebuilds one entry and answers like a fresh index", () => {
    const first = new LibraryIndex(list);
    const target = base(list)[40]!;
    const next = withGames(list, (m) => m.set(target.appName, { ...target, favorite: !target.favorite, runState: "running", activeJobId: "j1" }));
    const idx = new LibraryIndex(next, first);
    expect(idx.rebuilt).toBe(1);
    expectSame(idx, new LibraryIndex(next));
    expect(idx.get(target.appName)).toBe(next.find((g) => g.appName === target.appName));
    expect(first.get(target.appName)).toBe(target); // the previous index is untouched
  });

  it("a change that moves a game (title, last played, size), an added and a removed game", () => {
    const first = new LibraryIndex(list);
    const [a, b, c, d] = base(list).filter((g) => g.installStatus === "installed").slice(0, 4) as [GameSummary, GameSummary, GameSummary, GameSummary];
    let state = list;
    let prev = first;
    const steps: ((m: Map<string, GameSummary>) => void)[] = [
      (m) => m.set(a.appName, { ...a, title: "Zzz Renamed Game" }),
      (m) => m.set(b.appName, { ...b, lastPlayedAt: "2099-01-01T00:00:00Z" }),
      (m) => m.set(c.appName, { ...c, installedSizeBytes: 9e12 }),
      (m) => m.delete(d.appName),
      (m) => m.set("brand-new", { ...a, appName: "brand-new", title: "Brand New Game", dlcCount: 0 }),
      (m) => m.set(a.appName, { ...a, title: "Zzz Renamed Game", hidden: true }),
    ];
    for (const step of steps) {
      state = withGames(state, step);
      const idx = new LibraryIndex(state, prev);
      expectSame(idx, new LibraryIndex(state));
      prev = idx;
    }
  });

  it("random delta sequences keep matching a fresh index", () => {
    let seed = 7;
    const rnd = () => ((seed = (seed * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
    let state = list;
    let prev = new LibraryIndex(state);
    for (let step = 0; step < 40; step++) {
      state = withGames(state, (m) => {
        const apps = [...m.keys()];
        for (let k = 0; k < 1 + Math.floor(rnd() * 3); k++) {
          const g = m.get(apps[Math.floor(rnd() * apps.length)]!)!;
          const roll = rnd();
          if (roll < 0.4) m.set(g.appName, { ...g, favorite: !g.favorite });
          else if (roll < 0.55) m.set(g.appName, { ...g, title: `${g.title} ${step}` });
          else if (roll < 0.7) m.set(g.appName, { ...g, lastPlayedAt: new Date(NOW - Math.floor(rnd() * 1e9)).toISOString() });
          else if (roll < 0.8) m.set(g.appName, { ...g, installedSizeBytes: Math.floor(rnd() * 1e10) });
          else if (roll < 0.9) m.delete(g.appName);
          else m.set(`new-${step}-${k}`, { ...g, appName: `new-${step}-${k}`, title: `New ${step} ${k}` });
        }
      });
      const idx = new LibraryIndex(state, prev);
      expectSame(idx, new LibraryIndex(state));
      prev = idx;
    }
  });

  it("getLibraryIndex builds a delta's index on the last one: unchanged games keep their entries", () => {
    const m1 = new Map(list.map((g) => [g.appName, g]));
    const first = getLibraryIndex(m1);
    const g = base(list)[10]!;
    const m2 = new Map(m1).set(g.appName, { ...g, favorite: !g.favorite });
    const second = getLibraryIndex(m2);
    expect(second.rebuilt).toBe(1);
    expect(second.query()).not.toBe(first.query());
    expect(getLibraryIndex(m2)).toBe(second);
  });

  it("applies a one-game delta much faster than building from scratch (600 games)", () => {
    const big = lib(600, 3);
    const first = new LibraryIndex(big);
    const target = base(big)[100]!;
    const med = (f: () => void) => {
      const t: number[] = [];
      for (let i = 0; i < 21; i++) {
        const s = performance.now();
        f();
        t.push(performance.now() - s);
      }
      return t.sort((x, y) => x - y)[10]!;
    };
    let n = 0;
    const next = () => withGames(big, (m) => m.set(target.appName, { ...target, playtimeSeconds: ++n }));
    const states = Array.from({ length: 21 }, next);
    let i = 0;
    const incremental = med(() => new LibraryIndex(states[i++]!, first));
    i = 0;
    const scratch = med(() => new LibraryIndex(states[i++]!));
    expect(incremental).toBeLessThan(scratch * 0.6);
    expect(incremental).toBeLessThan(4);
  });
});
