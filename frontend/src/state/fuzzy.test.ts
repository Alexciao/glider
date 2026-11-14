import { describe, expect, it } from "vitest";
import { fuzzyMatch, fuzzyMatchAny } from "./fuzzy";
import { clearRecents, getRecents, pushRecent } from "./recents";

const score = (q: string, t: string) => fuzzyMatch(q, t)?.score ?? -Infinity;

describe("fuzzyMatch", () => {
  it("ranks whole > prefix > word start > substring > subsequence", () => {
    const title = "Final Fantasy";
    expect(score("final fantasy", title)).toBeGreaterThan(score("final", title));
    expect(score("final", title)).toBeGreaterThan(score("fan", title));
    expect(score("fan", title)).toBeGreaterThan(score("ant", title));
    expect(score("ant", title)).toBeGreaterThan(score("ffy", "Final Fantasy"));
  });

  it("needs every word of the query, in any order", () => {
    expect(fuzzyMatch("fantasy final", "Final Fantasy")).not.toBeNull();
    expect(fuzzyMatch("final zelda", "Final Fantasy")).toBeNull();
  });

  it("ignores case and diacritics, and keeps ranges on the original text", () => {
    const m = fuzzyMatch("brutal", "Brütal Légends")!;
    expect(m.ranges).toEqual([[0, 6]]);
    expect(fuzzyMatch("legends", "Brütal Légends")!.ranges).toEqual([[7, 14]]);
  });

  it("matches initials but not letters scattered across unrelated words", () => {
    expect(fuzzyMatch("ffx", "Final Fantasy X")).not.toBeNull();
    expect(fuzzyMatch("fold", "Check for updates interval frequency schedule")).toBeNull();
  });

  it("tolerates a skipped letter close together", () => {
    expect(fuzzyMatch("dwnload", "Download mode")).not.toBeNull();
  });

  it("an empty query matches everything neutrally", () => {
    expect(fuzzyMatch("  ", "anything")).toEqual({ score: 1, ranges: [] });
  });

  it("prefers the shorter text on a tie", () => {
    expect(score("home", "Home")).toBeGreaterThan(score("home", "Home and other long things to do"));
  });

  it("fuzzyMatchAny: the label wins over keywords, and keywords still find the item", () => {
    const byLabel = fuzzyMatchAny("update", "Update all games", "install")!;
    const byKeyword = fuzzyMatchAny("install", "Update all games", "install")!;
    expect(byLabel.score).toBeGreaterThan(byKeyword.score);
    expect(byKeyword.ranges).toEqual([]);
    expect(fuzzyMatchAny("zzz", "Update all games", "install")).toBeNull();
  });
});

describe("recents", () => {
  it("keeps the newest first, de-duplicated, capped at six", () => {
    clearRecents();
    for (const id of ["a", "b", "c", "d", "e", "f", "g", "c"]) pushRecent(id);
    expect(getRecents()).toEqual(["c", "g", "f", "e", "d", "b"]);
  });

  it("survives unavailable or corrupt storage", () => {
    localStorage.setItem("glider.palette.recent.v1", "{not json");
    expect(getRecents()).toEqual([]);
    const original = Storage.prototype.setItem;
    Storage.prototype.setItem = () => {
      throw new Error("blocked");
    };
    try {
      expect(() => pushRecent("x")).not.toThrow();
    } finally {
      Storage.prototype.setItem = original;
    }
  });
});
