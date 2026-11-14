import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { formatRoute, goGame, installIntent, installRouter, navigate, parseRoute, sameRoute, useRouter, type Route } from "./router";

describe("parseRoute / formatRoute", () => {
  it.each([
    ["home", { page: "home" }],
    ["library", { page: "library" }],
    ["downloads", { page: "downloads" }],
    ["store", { page: "store" }],
    ["settings", { page: "settings" }],
    ["game/Fortnite", { page: "game", appName: "Fortnite" }],
    ["/game/Fortnite", { page: "game", appName: "Fortnite" }],
    ["#/game/Fortnite", { page: "game", appName: "Fortnite" }],
    ["#library", { page: "library" }],
    ["game/Foxglove%2F1A2", { page: "game", appName: "Foxglove/1A2" }],
    ["game/0123456789abcdef0123456789abcdef/", { page: "game", appName: "0123456789abcdef0123456789abcdef" }],
    ["game/Sugar/install", { page: "game", appName: "Sugar" }],
    ["store/p/some-game_2", { page: "store", slug: "some-game_2" }],
  ] as [string, Route][])("%s", (input, route) => {
    expect(parseRoute(input)).toEqual(route);
  });

  it("rejects junk", () => {
    for (const bad of ["", "#", "/", "nope", "game", "game/", "downloads/extra", "library/Fortnite", null, undefined]) expect(parseRoute(bad)).toBeNull();
    for (const bad of ["game/a/b", "game/a/install/x", "game/%E0%A4%A", "store/p", "store/p/", "store/p/a/b", "store/x/y", "store/p/bad.slug", "store/p/%2e%2e"]) expect(parseRoute(bad)).toBeNull();
  });

  it("installIntent only fires for the /install suffix", () => {
    expect(installIntent("game/Sugar/install")).toBe("Sugar");
    expect(installIntent("game/Sugar/install/")).toBe("Sugar");
    expect(installIntent("game/Sugar")).toBeNull();
    expect(installIntent("downloads")).toBeNull();
    expect(installIntent("game/a/b")).toBeNull();
    expect(installIntent(null)).toBeNull();
  });

  it("formats and compares store slugs", () => {
    expect(formatRoute({ page: "store", slug: "x" })).toBe("store/p/x");
    expect(formatRoute({ page: "store" })).toBe("store");
    expect(sameRoute({ page: "store", slug: "x" }, { page: "store", slug: "y" })).toBe(false);
    expect(sameRoute({ page: "store" }, { page: "store" })).toBe(true);
  });

  it("round-trips, percent-encoding odd appNames", () => {
    for (const r of [{ page: "home" }, { page: "game", appName: "a b/c#d" }, { page: "game", appName: "Fortnite" }] as Route[]) {
      expect(parseRoute(formatRoute(r))).toEqual(r);
    }
    expect(formatRoute({ page: "game", appName: "a b" })).toBe("game/a%20b");
    expect(sameRoute({ page: "game", appName: "a" }, { page: "game", appName: "a" })).toBe(true);
    expect(sameRoute({ page: "game", appName: "a" }, { page: "game", appName: "b" })).toBe(false);
    expect(sameRoute({ page: "home" }, { page: "game", appName: "a" })).toBe(false);
  });
});

describe("router store", () => {
  let uninstall: () => void;
  beforeEach(() => {
    history.replaceState(null, "", "/");
    useRouter.setState({ route: { page: "home" }, previous: null });
    uninstall = installRouter();
  });
  afterEach(() => uninstall());

  it("go() changes the route, remembers where we came from, and writes the hash", () => {
    navigate("library");
    expect(useRouter.getState().route).toEqual({ page: "library" });
    expect(location.hash).toBe("#/library");
    goGame("Fortnite");
    expect(useRouter.getState().route).toEqual({ page: "game", appName: "Fortnite" });
    expect(useRouter.getState().previous).toEqual({ page: "library" });
    expect(location.hash).toBe("#/game/Fortnite");
  });

  it("ignores unknown route strings and navigating to where we already are", () => {
    navigate("downloads");
    const before = useRouter.getState();
    navigate("what/is/this");
    navigate("downloads");
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
    expect(useRouter.getState().previous).toBe(before.previous);
  });

  it("follows the browser's hash changes (back/forward, glider:// links)", () => {
    location.hash = "#/settings";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    expect(useRouter.getState().route).toEqual({ page: "settings" });
    location.hash = "#/game/Foo";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    expect(useRouter.getState().route).toEqual({ page: "game", appName: "Foo" });
    location.hash = "#/garbage";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    expect(useRouter.getState().route).toEqual({ page: "home" });
  });
});
