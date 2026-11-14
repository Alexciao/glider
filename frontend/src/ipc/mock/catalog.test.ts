import { describe, expect, it } from "vitest";
import { generateCatalog } from "./catalog";

const NOW = Date.parse("2026-11-14T12:00:00Z");
const gen = (count = 300, seed = 1337) => generateCatalog({ seed, count, now: NOW, art: true });

describe("mock catalog", () => {
  const lib = gen();
  const base = lib.filter((g) => g.summary.kind === "game");
  const s = lib.map((g) => g.summary);

  it("is deterministic for a seed, and different for another", () => {
    expect(JSON.stringify(gen(80).map((g) => g.summary))).toBe(JSON.stringify(gen(80).map((g) => g.summary)));
    expect(gen(80, 1).map((g) => g.summary.title)).not.toEqual(gen(80, 2).map((g) => g.summary.title));
  });

  it("has ~300 base games plus DLC, unique appNames and titles", () => {
    expect(base.length).toBe(300);
    expect(lib.length).toBeGreaterThan(330);
    expect(new Set(s.map((x) => x.appName)).size).toBe(s.length);
    expect(new Set(base.map((g) => g.summary.title)).size).toBe(base.length);
    expect(s[0]!.appName).toBe("Fortnite");
  });

  it("is a realistic mix of installed / not installed / updates / broken", () => {
    const by = (st: string) => base.filter((g) => g.summary.installStatus === st).length;
    expect(by("installed")).toBeGreaterThanOrEqual(30);
    expect(by("installed")).toBeLessThan(80);
    expect(by("notInstalled")).toBeGreaterThan(150);
    expect(by("broken")).toBeGreaterThanOrEqual(1);
    expect(by("notInstallable")).toBeGreaterThanOrEqual(5);
    expect(base.filter((g) => g.summary.updateAvailable).length).toBeGreaterThanOrEqual(5);
    expect(base.filter((g) => g.summary.updateAvailable).every((g) => g.summary.installStatus === "installed" && g.summary.downloadSizeBytes)).toBe(true);
    expect(base.filter((g) => g.summary.needsVerification).length).toBeGreaterThanOrEqual(1);
    expect(base.filter((g) => g.summary.installedSizeBytes === null && g.summary.installStatus === "installed").length).toBeGreaterThanOrEqual(1);
  });

  it("installs live on several drives / library folders", () => {
    const drives = new Set(base.map((g) => g.summary.installPath?.[0]).filter(Boolean));
    expect(drives.size).toBeGreaterThanOrEqual(3);
    for (const g of base.filter((x) => x.summary.installPath)) expect(g.summary.installPath).toMatch(/^[A-Z]:\\/);
  });

  it("has DLC: installable, entitlement-only, and a launchable add-on, each pointing at its base game", () => {
    const dlc = lib.filter((g) => g.summary.kind !== "game");
    expect(dlc.length).toBeGreaterThan(30);
    expect(dlc.some((g) => g.summary.kind === "launchableAddon")).toBe(true);
    expect(dlc.some((g) => g.summary.kind === "dlc" && g.summary.installStatus === "notInstallable")).toBe(true);
    expect(dlc.some((g) => g.summary.kind === "dlc" && (g.summary.installStatus === "notInstalled" || g.summary.installStatus === "installed"))).toBe(true);
    const byApp = new Map(lib.map((g) => [g.summary.appName, g]));
    for (const d of dlc) {
      const host = byApp.get(d.summary.baseAppName!);
      expect(host?.dlc).toContain(d.summary.appName);
      expect(host?.summary.dlcCount).toBe(host?.dlc.length);
    }
  });

  it("has cloud saves, exactly one conflict, and other save states", () => {
    expect(base.filter((g) => g.summary.cloudSaves).length).toBeGreaterThan(150);
    const conflicts = base.filter((g) => g.summary.saveStatus === "conflict");
    expect(conflicts).toHaveLength(1);
    expect(conflicts[0]!.conflict).not.toBeNull();
    expect(conflicts[0]!.summary.installStatus).toBe("installed");
    const states = new Set(base.map((g) => g.summary.saveStatus));
    for (const st of ["notSupported", "inSync", "localNewer", "cloudNewer", "conflict"]) expect(states.has(st as never)).toBe(true);
    for (const g of base) expect(g.saves?.status ?? "notSupported").toBe(g.summary.saveStatus);
  });

  it("has third-party titles, exactly one needing activation, games that can't run offline, broken installs", () => {
    expect(base.filter((g) => g.summary.thirdParty === "ubisoft").length).toBeGreaterThanOrEqual(3);
    expect(base.filter((g) => g.summary.thirdParty === "ea").length).toBeGreaterThanOrEqual(3);
    expect(base.filter((g) => g.summary.thirdParty !== "none").every((g) => g.summary.thirdPartyName)).toBe(true);
    expect(base.filter((g) => g.needsActivation)).toHaveLength(1);
    expect(base.filter((g) => !g.summary.canRunOffline).length).toBeGreaterThanOrEqual(5);
    const broken = base.filter((g) => g.summary.installStatus === "broken");
    expect(broken.every((g) => g.badFiles > 0 && g.badBytes > 0)).toBe(true);
  });

  it("has favorites, hidden games, and recently played games with playtime", () => {
    expect(base.filter((g) => g.summary.favorite).length).toBeGreaterThanOrEqual(8);
    expect(base.filter((g) => g.summary.hidden).length).toBeGreaterThanOrEqual(3);
    const played = base.filter((g) => g.summary.lastPlayedAt);
    expect(played.length).toBeGreaterThan(20);
    expect(played.every((g) => g.summary.playtimeSeconds > 0)).toBe(true);
    const recent = played.filter((g) => NOW - Date.parse(g.summary.lastPlayedAt!) < 14 * 86_400_000);
    expect(recent.length).toBeGreaterThan(8);
  });

  it("art: tall 3:4, wide 16:9 and a placeholder for most; a few lack one side to exercise fallbacks", () => {
    expect(base.filter((g) => g.summary.art.tall && g.summary.art.wide && g.summary.art.placeholder).length).toBeGreaterThan(270);
    expect(base.some((g) => g.summary.art.tall === null)).toBe(true);
    expect(base.some((g) => g.summary.art.wide === null)).toBe(true);
    expect(lib.find((g) => g.summary.appName === "Fortnite")!.summary.art.wide).toMatch(/^data:image\/svg/);
  });

  it("art: false strips every image (the no-art fallback)", () => {
    const none = generateCatalog({ seed: 1, count: 40, now: NOW, art: false });
    for (const g of none) expect(g.summary.art).toEqual({ tall: null, tallThumb: null, wide: null, logo: null, placeholder: null });
  });

  it("artVariant: every game, Fortnite included, gets the same worst-case key art in every shape", () => {
    for (const kind of ["busy", "bright", "white"] as const) {
      const worst = generateCatalog({ seed: 1, count: 40, now: NOW, art: true, artVariant: kind });
      const first = worst[0]!.summary.art;
      expect(first.wide).toMatch(/^data:image\/svg/);
      for (const g of worst) expect(g.summary.art).toEqual(first);
    }
    const [busy, white] = (["busy", "white"] as const).map((k) => generateCatalog({ seed: 1, count: 10, now: NOW, art: true, artVariant: k })[0]!.summary.art.wide);
    expect(busy).not.toBe(white);
  });

  it("titles are invented: no obviously real trademarks", () => {
    const banned = /hollow knight|zelda|mario|minecraft|elden|witcher|cyberpunk|half-life|portal|doom|halo|skyrim|stardew|celeste|hades|control|alan wake|borderlands|rocket league/i;
    for (const g of lib) if (g.summary.appName !== "Fortnite") expect(g.summary.title).not.toMatch(banned);
  });

  it("scales down for tiny libraries and up to 600 quickly", () => {
    expect(gen(12).filter((g) => g.summary.kind === "game")).toHaveLength(12);
    const t0 = performance.now();
    const big = gen(600);
    expect(performance.now() - t0).toBeLessThan(1500);
    expect(big.filter((g) => g.summary.kind === "game")).toHaveLength(600);
  });
});
