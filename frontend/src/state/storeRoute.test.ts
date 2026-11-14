import { describe, expect, it } from "vitest";
import { formatRoute, parseRoute, sameRoute, storeRoute, type Route } from "./router";

describe("store routes", () => {
  it.each([
    ["store", { page: "store" }],
    ["store/product/knightica-3be45c", { page: "store", target: { kind: "product", slug: "knightica-3be45c" } }],
    ["#/store/bundle/lisa-the-definitive-edition", { page: "store", target: { kind: "bundle", slug: "lisa-the-definitive-edition" } }],
    ["store/product/Alien-Isolation", { page: "store", target: { kind: "product", slug: "alien-isolation" } }],
    ["store/free-games", { page: "store", target: { kind: "freeGames", slug: null } }],
    ["store/wishlist", { page: "store", target: { kind: "wishlist", slug: null } }],
    ["store/browse", { page: "store", target: { kind: "browse", slug: null } }],
  ] as [string, Route][])("parses %s", (input, route) => {
    expect(parseRoute(input)).toEqual(route);
  });

  it("rejects raw URLs, bad slugs and extra segments", () => {
    for (const bad of [
      "store/product",
      "store/product/",
      "store/product/../../x",
      "store/product/a b",
      "store/product/https%3A%2F%2Fevil.test",
      "store/product/-x",
      "store/product/a/b",
      "store/free-games/x",
      "store/unknown",
      "store/https://evil.test",
    ]) {
      expect(parseRoute(bad)).toBeNull();
    }
  });

  it("round-trips", () => {
    for (const r of [
      { page: "store" },
      storeRoute({ kind: "product", slug: "knightica-3be45c" }),
      storeRoute({ kind: "bundle", slug: "x1" }),
      storeRoute({ kind: "freeGames", slug: null }),
      storeRoute({ kind: "wishlist", slug: null }),
    ] as Route[]) {
      expect(parseRoute(formatRoute(r))).toEqual(r);
    }
    expect(formatRoute(storeRoute(null))).toBe("store");
    // kinds without a page of their own fall back to the Store home
    expect(formatRoute(storeRoute({ kind: "home", slug: null }))).toBe("store");
  });

  it("treats different targets as different routes", () => {
    expect(sameRoute(storeRoute({ kind: "product", slug: "a" }), storeRoute({ kind: "product", slug: "b" }))).toBe(false);
    expect(sameRoute(storeRoute({ kind: "product", slug: "a" }), storeRoute({ kind: "product", slug: "a" }))).toBe(true);
    expect(sameRoute({ page: "store" }, storeRoute({ kind: "freeGames", slug: null }))).toBe(false);
  });
});
