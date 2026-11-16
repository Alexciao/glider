import { expect, test, type Page } from "@playwright/test";

/**
 * Press feedback. A plain control squeezes on the 1st and the Nth press alike (Radix steals :active from controls that open a
 * popover on pointer-down, so `pressFeedback.ts` keeps its own pressed state). A trigger that opens a popup is never squeezed:
 * the popup is positioned against the trigger's box, so it followed the squeeze and slid sideways while opening (the "wobble").
 */
const POPUP_TRIGGERS = ['[aria-label="Sort"]', '[aria-label="Collections"]'];
const PLAIN = 'nav[aria-label="Main"] button[aria-label^="Library"]';

async function openLibrary(page: Page) {
  await page.goto("/?scenario=returning&theme=dark&backdrop=solid&dev=0");
  await page.waitForSelector("html[data-ready='1']");
  await page.evaluate(() => (window as unknown as { __glider: { ui: { getState(): { go(p: string): void } } } }).__glider.ui.getState().go("library"));
}

test("every press of a plain control squeezes it", async ({ page }) => {
  await openLibrary(page);
  const control = page.locator(PLAIN).first();
  await control.waitFor();
  const scales: number[] = [];
  for (let i = 0; i < 3; i++) {
    const box = (await control.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.waitForTimeout(250);
    scales.push(await control.evaluate((n) => Number.parseFloat(getComputedStyle(n).scale)));
    await page.mouse.up();
    await page.waitForTimeout(300);
  }
  for (const s of scales) expect(s).toBeLessThan(1);
  expect(new Set(scales.map((s) => s.toFixed(3))).size).toBe(1);
});

for (const sel of POPUP_TRIGGERS) {
  test(`${sel} opens its popup in place: no squeeze, the popup never moves, the trigger stays lit`, async ({ page }) => {
    await openLibrary(page);
    const trigger = page.locator(sel).first();
    await trigger.waitFor();
    const box = (await trigger.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.waitForTimeout(250);
    const lit = await trigger.evaluate((n) => getComputedStyle(n).backgroundColor);
    // Sample the popup's placement every frame from the press until it has settled.
    await page.evaluate(() => {
      const w = window as unknown as { __placements: string[] };
      w.__placements = [];
      const t0 = performance.now();
      const loop = () => {
        const wrap = document.querySelector<HTMLElement>("[data-radix-popper-content-wrapper]");
        if (wrap) w.__placements.push(wrap.style.transform);
        if (performance.now() - t0 < 450) requestAnimationFrame(loop);
      };
      requestAnimationFrame(loop);
    });
    await page.mouse.down();
    await page.waitForTimeout(120);
    expect(await trigger.evaluate((n) => getComputedStyle(n).scale)).toBe("none");
    await page.mouse.up();
    // The pointer heads for the menu: the trigger loses :hover (the page is inert) but keeps its lit background.
    await page.mouse.move(box.x + box.width / 2, box.y + box.height + 40, { steps: 4 });
    await page.waitForTimeout(400);
    const placements = await page.evaluate(() => (window as unknown as { __placements: string[] }).__placements.filter((t) => t && !t.includes("-200%")));
    expect(placements.length).toBeGreaterThan(5);
    expect(new Set(placements).size).toBe(1);
    expect(await trigger.evaluate((n) => getComputedStyle(n).backgroundColor)).toBe(lit);
    await page.keyboard.press("Escape");
  });
}

test("reduced motion removes the squeeze", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await openLibrary(page);
  const control = page.locator(PLAIN).first();
  await control.waitFor();
  const box = (await control.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.waitForTimeout(250);
  expect(await control.evaluate((n) => getComputedStyle(n).scale)).toBe("none");
  await page.mouse.up();
});
