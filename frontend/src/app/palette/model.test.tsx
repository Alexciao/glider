import { describe, expect, it } from "vitest";
import type { AppSnapshot, GameSummary } from "../../ipc/contract.gen";
import { worldFor } from "../../test/fixtures";
import { buildCommands, buildSections, gameActions, gameItem, rankItems, type PaletteContext } from "./model";

const ctxFor = (snap: AppSnapshot, patch: Partial<PaletteContext> = {}): PaletteContext => ({ snap, theme: "dark", go: () => {}, openDialog: () => {}, ...patch });

describe("gameActions", () => {
  it("installed → Play then Open; update pending → Update, Play, Open; not installed → Install, Open", async () => {
    const w = await worldFor("returning");
    const ctx = ctxFor(w.snap);
    const plain = w.find((g) => g.kind === "game" && g.installStatus === "installed" && !g.updateAvailable && g.thirdParty === "none" && g.runState === "idle");
    expect(gameActions(plain, ctx).map((a) => a.label)).toEqual(["Play", "Open"]);
    const updatable = w.find((g) => g.kind === "game" && g.installStatus === "installed" && g.updateAvailable && g.runState === "idle" && !w.snap.queue.jobs.some((j) => j.appName === g.appName));
    expect(gameActions(updatable, ctx).map((a) => a.keys)).toEqual(["Enter", "Shift+Enter", "Ctrl+Enter"]);
    expect(gameActions(updatable, ctx)[0]!.label).toBe("Update");
    const absent = w.find((g) => g.kind === "game" && g.installStatus === "notInstalled" && g.thirdParty === "none");
    expect(gameActions(absent, ctx).map((a) => a.label)).toEqual(["Install", "Open"]);
  });

  it("when the natural action is blocked (offline) Enter opens the page instead", async () => {
    const w = await worldFor("offline");
    const absent = w.find((g) => g.kind === "game" && g.installStatus === "notInstalled" && g.thirdParty === "none");
    const actions = gameActions(absent, ctxFor(w.snap));
    expect(actions.map((a) => a.label)).toEqual(["Open"]);
    expect(actions[0]!.keys).toBe("Enter");
  });

  it("a running game only opens", async () => {
    const w = await worldFor("playing");
    const playing = w.find((g) => g.runState === "running");
    expect(gameActions(playing, ctxFor(w.snap)).map((a) => a.label)).toEqual(["Open"]);
  });
});

describe("commands", () => {
  it("offers the app actions, every Settings section, download modes (current marked) and the pages", async () => {
    const w = await worldFor("returning");
    const items = buildCommands(ctxFor(w.snap));
    const labels = items.map((i) => i.label);
    for (const l of ["Check for updates", "Update all games", "Run setup again", "Download mode: Balanced", "Store", "Home", "Library", "Downloads", "Settings"]) expect(labels).toContain(l);
    expect(items.filter((i) => i.group === "settings")).toHaveLength(12);
    expect(items.find((i) => i.label === "Download mode: Balanced")!.hint).toBe("Current");
    expect(labels.some((l) => /^Pause all downloads$|^Resume all downloads$/.test(l))).toBe(true);
  });

  it("disables what can't run and says why in a few words", async () => {
    const w = await worldFor("offline");
    const items = buildCommands(ctxFor(w.snap));
    expect(items.find((i) => i.label === "Check for updates")!.disabled).toBe("You're offline");
    expect(items.find((i) => i.label === "Pause all downloads")!.disabled).toBe("No downloads running");
  });

  it("on a game page the game's own actions come first, in their own group", async () => {
    const w = await worldFor("returning");
    const g: GameSummary = w.find((x) => x.kind === "game" && x.installStatus === "installed" && !x.updateAvailable && x.runState === "idle");
    const items = buildCommands(ctxFor(w.snap, { currentGame: g }));
    const mine = items.filter((i) => i.group === "game").map((i) => i.label);
    expect(mine[0]).toBe(`Play ${g.title}`);
    expect(mine).toEqual(expect.arrayContaining([`Verify ${g.title}`, `Move ${g.title}…`, `Uninstall ${g.title}…`]));
  });
});

describe("ranking and sections", () => {
  it("a query groups results by kind, best group first, and caps each group", async () => {
    const w = await worldFor("returning");
    const ctx = ctxFor(w.snap);
    const commands = buildCommands(ctx);
    const sections = buildSections("down", [], commands, [], []);
    expect(sections[0]!.rows[0]!.item.label).toMatch(/^Downloads$|^Download mode/);
    expect(sections.every((s) => s.rows.length <= 5)).toBe(true);
    expect(sections.map((s) => s.group)).toEqual(expect.arrayContaining(["pages", "settings", "actions"]));
  });

  it("highlights the matched letters of a label", async () => {
    const w = await worldFor("returning");
    const [top] = rankItems("upd", buildCommands(ctxFor(w.snap)));
    expect(top!.match.ranges[0]).toEqual([0, 3]);
  });

  it("with no query: recents, then suggestions, then the rest, with no duplicates", async () => {
    const w = await worldFor("returning");
    const ctx = ctxFor(w.snap);
    const commands = buildCommands(ctx);
    const recent = { ...commands.find((c) => c.id === "cmd:go-store")!, group: "recent" as const };
    const sections = buildSections("", [], commands, [recent], []);
    expect(sections[0]!.group).toBe("recent");
    const ids = sections.flatMap((s) => s.rows.map((r) => r.item.id));
    expect(new Set(ids).size).toBe(ids.length);
  });

  it("a game item carries its status for screen readers and its primary action", async () => {
    const w = await worldFor("returning");
    const g = w.find((x) => x.kind === "game" && x.installStatus === "installed" && !x.updateAvailable && x.runState === "idle");
    const item = gameItem(g, ctxFor(w.snap));
    expect(item.hint).toBe("Installed");
    expect(item.actions[0]!.label).toBe("Play");
  });
});
