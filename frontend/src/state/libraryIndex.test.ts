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
    expect(q("notInstalled").every((g) => g.installStatus === "notInstalled")).toBe(true);
    expect(q("updates").every((g) => g.updateAvailable && g.installStatus === "installed")).toBe(true);
    expect(q("updates").length).toBeGreaterThan(0);
    expect(q("cloudSaves").every((g) => g.cloudSaves)).toBe(true);
    expect(q("favorites").every((g) => g.favorite)).toBe(true);
    expect(q("hidden").every((g) => g.hidden)).toBe(true);
    expect(q("hidden").length).toBeGreaterThan(0);
    expect(q("installed").length + q("notInstalled").length).toBeLessThanOrEqual(q("all").length);
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
    // Warm up the JIT, then take the worst of several runs.
    for (const q of queries) idx.query(q);
    let worst = 0;
    for (let run = 0; run < 10; run++) {
      for (const q of queries) {
        const s = performance.now();
        idx.query(q);
        worst = Math.max(worst, performance.now() - s);
      }
    }
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
