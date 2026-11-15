import { expect, test } from "@playwright/test";

/**
 * Press feedback is the same on the 1st and the Nth click, for controls that open a popover on pointer-down (Radix steals :active
 * from those: the squeeze used to play once and then never again). Reads the computed `scale` while the mouse is held.
 */
const TRIGGERS = ['[aria-label="Sort"]', '[aria-label="Collections"]'];

for (const sel of TRIGGERS) {
  test(`every press of ${sel} squeezes the trigger`, async ({ page }) => {
    await page.goto("/?scenario=returning&theme=dark&backdrop=solid&dev=0");
    await page.waitForSelector("html[data-ready='1']");
    await page.evaluate(() => (window as unknown as { __glider: { ui: { getState(): { go(p: string): void } } } }).__glider.ui.getState().go("library"));
    const trigger = page.locator(sel).first();
    await trigger.waitFor();
    const scales: number[] = [];
    for (let i = 0; i < 3; i++) {
      const box = (await trigger.boundingBox())!;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await page.waitForTimeout(250);
      scales.push(await trigger.evaluate((n) => Number.parseFloat(getComputedStyle(n).scale)));
      await page.mouse.up();
      await page.keyboard.press("Escape");
      await page.waitForTimeout(300);
    }
    for (const s of scales) expect(s).toBeLessThan(1);
    expect(new Set(scales.map((s) => s.toFixed(3))).size).toBe(1);
  });

  test(`reduced motion removes the squeeze of ${sel}`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: "reduce" });
    await page.goto("/?scenario=returning&theme=dark&backdrop=solid&dev=0");
    await page.waitForSelector("html[data-ready='1']");
    await page.evaluate(() => (window as unknown as { __glider: { ui: { getState(): { go(p: string): void } } } }).__glider.ui.getState().go("library"));
    const trigger = page.locator(sel).first();
    const box = (await trigger.boundingBox())!;
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    await page.waitForTimeout(250);
    expect(await trigger.evaluate((n) => getComputedStyle(n).scale)).toBe("none");
    await page.mouse.up();
  });
}
