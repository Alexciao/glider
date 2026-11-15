import { expect, test } from "@playwright/test";

/**
 * The shared page frame (src/features/common/frame.ts): at any window size every page's title has the same left edge, and on a
 * wide window that edge is the centred frame's, not the content area's.
 */
type G = { __glider: { ui: { getState(): { go(page: string): void } } } };
const SIZES = [
  { w: 900, h: 640 },
  { w: 1280, h: 800 },
  { w: 1920, h: 1080 },
  { w: 2560, h: 1440 },
  { w: 3440, h: 1440 },
];
const RAIL = 72;

for (const { w, h } of SIZES) {
  test(`page titles share one left edge at ${w}×${h}`, async ({ page }) => {
    await page.setViewportSize({ width: w, height: h });
    await page.goto("/?scenario=returning&theme=dark&backdrop=solid&dev=0");
    await page.waitForSelector("html[data-ready='1']");
    const lefts: Record<string, number> = {};
    for (const [name, nav, selector] of [
      ["library", "library", "h1:has-text('Library')"],
      ["downloads", "downloads", "h1:has-text('Downloads')"],
      ["settings", "settings", "h1:has-text('Settings')"],
    ] as const) {
      await page.evaluate((p) => (window as unknown as G).__glider.ui.getState().go(p), nav);
      const title = page.locator(selector).first();
      await title.waitFor();
      lefts[name] = (await title.boundingBox())!.x;
    }
    const gutter = w - RAIL >= 1028 ? 40 : 28;
    const spare = Math.max(0, Math.floor((w - RAIL - gutter * 2 - 1440) / 2));
    const expected = RAIL + gutter + spare;
    const xs = Object.values(lefts);
    expect(Math.max(...xs) - Math.min(...xs), `titles at ${JSON.stringify(lefts)}`).toBeLessThanOrEqual(1);
    // The rail is 73 px with its hairline and the scroll gutter (~9 px) shifts a centred frame by half of it: within 6 px of the arithmetic.
    expect(Math.abs(xs[0]! - expected), `title at ${xs[0]}, frame edge ${expected}`).toBeLessThanOrEqual(6);
  });
}
