import { afterEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_PREFS, readPrefs, resetLibraryPrefsForTests, sanitizePrefs, useLibraryPrefsStore } from "./libraryPrefs";
import { freeWindowLabel, orderFreeGames } from "./freeGames";
import type { FreeGame } from "../ipc/contract.gen";

afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
  resetLibraryPrefsForTests();
});

describe("library preferences", () => {
  it("falls back to defaults for anything invalid", () => {
    expect(sanitizePrefs(null)).toEqual(DEFAULT_PREFS);
    expect(sanitizePrefs({ view: "wall", sort: "nope", filter: 4, collectionId: "" })).toEqual(DEFAULT_PREFS);
    expect(sanitizePrefs({ view: "list", sort: "size", filter: "favorites", collectionId: "col-1" })).toEqual({ view: "list", sort: "size", filter: "favorites", collectionId: "col-1" });
  });

  it("is remembered per user and survives a reload of the store", () => {
    useLibraryPrefsStore.getState().patch("alice", { view: "list", sort: "recent" });
    useLibraryPrefsStore.getState().patch("bob", { filter: "installed" });
    resetLibraryPrefsForTests();
    expect(readPrefs("alice")).toMatchObject({ view: "list", sort: "recent", filter: "all" });
    expect(readPrefs("bob")).toMatchObject({ view: "grid", filter: "installed" });
  });

  it("never throws when storage is blocked or corrupt", () => {
    localStorage.setItem("glider.library.prefs.v1.x", "{not json");
    expect(readPrefs("x")).toEqual(DEFAULT_PREFS);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("full");
    });
    expect(readPrefs("y")).toEqual(DEFAULT_PREFS);
    expect(() => useLibraryPrefsStore.getState().patch("y", { view: "list" })).not.toThrow();
    // Still applied for this session.
    expect(useLibraryPrefsStore.getState().byUser.y?.view).toBe("list");
  });
});

const free = (over: Partial<FreeGame>): FreeGame => ({
  id: "g",
  title: "G",
  imageUrl: null,
  slug: "g",
  startsAt: "2026-11-10T00:00:00Z",
  endsAt: "2026-11-20T00:00:00Z",
  status: "freeNow",
  owned: false,
  ...over,
});

describe("free games", () => {
  it("orders free-now by soonest end, then upcoming by soonest start", () => {
    const items = [
      free({ id: "u2", status: "upcoming", startsAt: "2026-11-25T00:00:00Z" }),
      free({ id: "n2", endsAt: "2026-11-22T00:00:00Z" }),
      free({ id: "u1", status: "upcoming", startsAt: "2026-11-21T00:00:00Z" }),
      free({ id: "n1", endsAt: "2026-11-18T00:00:00Z" }),
    ];
    expect(orderFreeGames(items).map((g) => g.id)).toEqual(["n1", "n2", "u1", "u2"]);
  });

  it("says when each is free, in words", () => {
    const now = Date.parse("2026-11-14T12:00:00Z");
    expect(freeWindowLabel(free({ endsAt: "2026-11-14T20:00:00Z" }), now)).toBe("Ends today");
    expect(freeWindowLabel(free({}), now)).toMatch(/^Until /);
    expect(freeWindowLabel(free({ status: "upcoming" }), now)).toMatch(/^From /);
    expect(freeWindowLabel(free({ owned: true }), now)).toBe("In your library");
  });
});
