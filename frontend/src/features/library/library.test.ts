import { describe, expect, it } from "vitest";
import type { GameSummary } from "../../ipc/contract.gen";
import { computeRange } from "../../ui";
import { PAGE_FRAME, frameInset, frameWidth } from "../common/frame";
import { GRID_GAP, MIN_TILE, TILE_TEXT, gridLayout, insetFor, listColumns } from "./gridLayout";
import { quickAction, rowStatus } from "./status";

describe("grid layout", () => {
  it("fits whole columns, keeps tiles 3:4 and the row height exact", () => {
    for (const width of [900, 960, 1208, 1500, 2400]) {
      const l = gridLayout(width);
      expect(l.cols).toBeGreaterThanOrEqual(2);
      expect(l.tileW).toBeGreaterThanOrEqual(MIN_TILE);
      expect(l.coverH).toBe(Math.round((l.tileW * 4) / 3));
      expect(l.rowH).toBe(l.coverH + TILE_TEXT + 24);
      const used = l.tileW * l.cols + GRID_GAP * (l.cols - 1);
      expect(l.pad * 2 + used).toBeLessThanOrEqual(width + 1);
    }
  });

  it("uses six columns at the common 1280 window and four at 960", () => {
    expect(gridLayout(1208).cols).toBe(6);
    expect(gridLayout(888).cols).toBe(4);
  });

  it("keeps the grid inside the page frame and centred in it on a wide window", () => {
    for (const width of [1208, 1848, 2488, 3368]) {
      const l = gridLayout(width);
      const used = l.tileW * l.cols + GRID_GAP * (l.cols - 1);
      expect(used).toBeLessThanOrEqual(PAGE_FRAME);
      // The grid's left edge is the frame's (the same x as every page's title), give or take the few px that whole columns leave over.
      expect(l.pad - frameInset(width)).toBeLessThan(l.tileW / 2 + GRID_GAP);
      expect(l.pad + used + (l.pad - 0)).toBeLessThanOrEqual(width + (l.tileW + GRID_GAP));
    }
    expect(gridLayout(2488).cols).toBe(7);
    expect(gridLayout(3368).cols).toBe(7);
  });

  it("the frame: gutters up to 1440 px of content, centred, shared by every page", () => {
    expect(frameWidth(1208)).toBe(1128); // a 1280 window: the content area minus the gutters, as before
    expect(frameInset(1208)).toBe(40);
    expect(frameWidth(2488)).toBe(PAGE_FRAME);
    expect(frameInset(2488)).toBe(Math.floor((2488 - PAGE_FRAME) / 2));
    expect(frameInset(828)).toBe(28); // 900 px window: narrow gutter
    expect(insetFor(3368)).toBe(frameInset(3368));
  });

  it("drops list columns from the right as the window narrows", () => {
    expect(listColumns(1208)).toEqual({ status: true, size: true, played: true });
    expect(listColumns(700)).toEqual({ status: true, size: true, played: false });
    expect(listColumns(600)).toEqual({ status: true, size: false, played: false });
  });
});

describe("row window", () => {
  it("renders only the visible rows plus overscan", () => {
    const r = computeRange(1000, 700, 100, 200, 2);
    expect([r.visibleFirst, r.visibleLast]).toEqual([10, 16]);
    expect([r.first, r.last]).toEqual([8, 18]);
  });

  it("clamps at both ends and handles no rows", () => {
    expect(computeRange(0, 700, 100, 3, 2)).toMatchObject({ first: 0, last: 2 });
    expect(computeRange(99999, 700, 100, 50, 2).last).toBe(49);
    expect(computeRange(0, 700, 100, 0, 2).last).toBeLessThan(computeRange(0, 700, 100, 0, 2).first);
  });
});

const game = (over: Partial<GameSummary>): GameSummary =>
  ({ kind: "game", runState: "idle", activeJobId: null, activeJobKind: null, installStatus: "notInstalled", updateAvailable: false, thirdPartyName: null, ...over }) as GameSummary;

describe("quick action", () => {
  it("is Play for installed (even with an update), Install for owned, Repair for broken", () => {
    expect(quickAction(game({ installStatus: "installed" }))?.kind).toBe("play");
    expect(quickAction(game({ installStatus: "installed", updateAvailable: true }))?.kind).toBe("play");
    expect(quickAction(game({ installStatus: "notInstalled" }))?.kind).toBe("install");
    expect(quickAction(game({ installStatus: "broken" }))?.kind).toBe("repair");
  });

  it("is absent while busy, for third-party-only titles and for add-ons", () => {
    expect(quickAction(game({ installStatus: "installed", runState: "running" }))).toBeNull();
    expect(quickAction(game({ installStatus: "installed", activeJobId: "j1" }))).toBeNull();
    expect(quickAction(game({ installStatus: "notInstallable" }))).toBeNull();
    expect(quickAction(game({ installStatus: "installed", kind: "dlc" }))).toBeNull();
  });

  it("row status names the most important state", () => {
    expect(rowStatus(game({ installStatus: "installed", updateAvailable: true })).text).toBe("Update available");
    expect(rowStatus(game({ installStatus: "installed", runState: "running" })).text).toBe("Running");
    expect(rowStatus(game({ installStatus: "notInstallable", thirdPartyName: "EA app" })).text).toBe("EA app");
  });
});
