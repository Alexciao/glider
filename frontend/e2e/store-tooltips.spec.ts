import { expect, test } from "@playwright/test";
/**
 * The Store is a native WebView2 window above the page: a tooltip reaching into its rectangle is drawn under it. While it is on
 * screen, the rail's tooltips open below their item (inside the rail) and the toolbar's never flip down into it.
 */
test("tooltips stay out of the Store's native view", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/?scenario=returning&dev=0");
  await page.waitForSelector("html[data-ready='1']");
  await page.getByRole("button", { name: "Store", exact: true }).click();
  const host = page.locator("[data-store-host]");
  await expect(host).toBeVisible();
  const hostBox = (await host.boundingBox())!;
  for (const name of ["Library", "Settings"]) {
    await page.getByRole("button", { name: new RegExp("^" + name) }).first().hover();
    const tip = page.locator("[data-radix-popper-content-wrapper]").filter({ has: page.locator('[role="tooltip"]') });
    await expect(tip).toHaveCount(1, { timeout: 2000 });
    const box = (await tip.boundingBox())!;
    expect(box.x + box.width).toBeLessThanOrEqual(hostBox.x);
    await page.keyboard.press("Escape"); await page.mouse.move(40, 760);
    await expect(tip).toHaveCount(0, { timeout: 2000 });
  }
  await page.getByRole("button", { name: "Reload", exact: true }).hover();
  const tip = page.locator("[data-radix-popper-content-wrapper]").filter({ has: page.locator('[role="tooltip"]') });
  await expect(tip).toHaveCount(1, { timeout: 2000 });
  const box = (await tip.boundingBox())!;
  expect(box.y + box.height).toBeLessThanOrEqual(hostBox.y);
});
