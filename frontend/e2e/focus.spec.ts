import { expect, test } from "@playwright/test";

/**
 * Keyboard users must always see where focus is. Tab through each page and require a visible outline
 * (or an input's focus-within ring) on every stop. Regression guard for utility classes such as
 * `outline-none` that silently override the global :focus-visible ring.
 */
const PAGES: { name: string; query: string; go?: [string, string?] }[] = [
  { name: "library", query: "scenario=returning" },
  { name: "downloads", query: "scenario=downloading&freeze=1", go: ["downloads"] },
  { name: "settings", query: "scenario=returning", go: ["settings", "startup"] },
  { name: "onboarding", query: "scenario=firstRunSignedIn" },
];

for (const theme of ["dark", "light"] as const) {
  for (const p of PAGES) {
    test(`focus is visible on every Tab stop: ${p.name} (${theme})`, async ({ page }) => {
      await page.goto(`/?${p.query}&theme=${theme}&dev=0${p.name === "library" ? "#/game/Fortnite" : ""}`);
      await page.waitForSelector("html[data-ready='1']");
      if (p.go) await page.evaluate(([pg, sec]) => (window as unknown as { __glider: { ui: { getState(): { go(p: string, s?: string): void } } } }).__glider.ui.getState().go(pg!, sec), p.go);
      await page.waitForTimeout(500);

      const missing: string[] = [];
      for (let i = 0; i < 14; i++) {
        await page.keyboard.press("Tab");
        const info = await page.evaluate(() => {
          const el = document.activeElement as HTMLElement | null;
          if (!el || el === document.body) return null;
          const own = getComputedStyle(el);
          const wrapper = el.closest("[class*='focus-within']") as HTMLElement | null;
          const wrapperRing = wrapper ? getComputedStyle(wrapper).boxShadow !== "none" : false;
          const label = el.getAttribute("aria-label") || el.textContent?.trim().slice(0, 24) || el.tagName;
          // A split button draws one ring around the whole group (each segment stays a tab stop).
          const group = el.closest("[data-split]") as HTMLElement | null;
          const groupRing = group ? getComputedStyle(group).outlineStyle !== "none" && parseFloat(getComputedStyle(group).outlineWidth) > 0 : false;
          return { label, outlined: own.outlineStyle !== "none" && parseFloat(own.outlineWidth) > 0, wrapperRing: wrapperRing || groupRing };
        });
        if (info && !info.outlined && !info.wrapperRing) missing.push(info.label);
      }
      expect(missing, `Tab stops with no visible focus ring: ${missing.join(", ")}`).toEqual([]);
    });
  }
}

/**
 * The download card's controls sit on key art, where the accent ring alone can disappear (blue or purple art, flat white). Each
 * focusable element in the band gets a dark halo under its ring (docs/design-download-card.md §7.6). Measured from pixels: just
 * outside the ring the picture must be dark whatever the art, and the ring itself must still be the accent.
 */
const ARTS: { name: string; query: string; art?: string; paint?: string }[] = [
  { name: "green", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1" },
  { name: "purple", query: "scenario=queue&freeze=1&seek=0.42&timescale=1" },
  { name: "blue", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", paint: "#3b82f6" },
  { name: "white", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", art: "white" },
  { name: "bright", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", art: "bright" },
  { name: "busy", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", art: "busy" },
];
for (const theme of ["dark", "light"] as const) {
  for (const a of ARTS) {
    test(`focus ring over art stays readable: ${a.name} art (${theme})`, async ({ page }) => {
      await page.goto(`/?${a.query}${a.art ? `&art=${a.art}` : ""}&theme=${theme}&backdrop=solid&dev=0`);
      await page.waitForSelector("html[data-ready='1']");
      await page.evaluate(() => (window as unknown as { __glider: { ui: { getState(): { go(p: string): void } } } }).__glider.ui.getState().go("downloads"));
      await page.waitForTimeout(900);
      // A flat colour the accent ring is closest to, to prove the halo does the work (the ring alone would vanish).
      if (a.paint) await page.evaluate((c) => ((document.querySelector("[data-band] > div") as HTMLElement).style.cssText = `position:absolute;inset:0;background:${c}`), a.paint);
      const pause = page.getByRole("button", { name: "Pause", exact: true });
      for (let i = 0; i < 12 && !(await pause.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
      await expect(pause).toBeFocused();
      await page.waitForTimeout(300);
      const style = await pause.evaluate((el) => {
        const cs = getComputedStyle(el);
        return { outline: cs.outlineStyle, width: parseFloat(cs.outlineWidth), offset: parseFloat(cs.outlineOffset), shadow: cs.boxShadow };
      });
      expect(style.outline).toBe("solid");
      expect(style.shadow).toContain("rgba(0, 0, 0, 0.8)");
      const box = (await pause.boundingBox())!;
      const png = await page.screenshot({ animations: "disabled", caret: "hide" });
      const probe = await page.evaluate(
        async ({ b64, box, offset, width }) => {
          const bmp = await createImageBitmap(await (await fetch(`data:image/png;base64,${b64}`)).blob());
          const cv = new OffscreenCanvas(bmp.width, bmp.height);
          const ctx = cv.getContext("2d", { willReadFrequently: true })!;
          ctx.drawImage(bmp, 0, 0);
          const lin = (v: number) => ((v /= 255) <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
          const lum = (r: number, g: number, b: number) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
          // The middle of the top edge: the ring is `offset..offset+width` px outside the box, the halo's outer part just beyond it.
          const x = Math.round(box.x + box.width / 2);
          const at = (d: number) => {
            const p = ctx.getImageData(x, Math.round(box.y - d), 1, 1).data;
            return { r: p[0]!, g: p[1]!, b: p[2]!, l: lum(p[0]!, p[1]!, p[2]!) };
          };
          return { ring: at(offset + width / 2), halo: at(offset + width + 1) };
        },
        { b64: png.toString("base64"), box, offset: style.offset, width: style.width },
      );
      // The ring is still the accent (blue-leaning), and the halo just outside it sets it off by at least the 3:1 non-text contrast.
      expect(probe.ring.b, "ring pixel is the accent").toBeGreaterThan(probe.ring.r);
      const ratio = (Math.max(probe.ring.l, probe.halo.l) + 0.05) / (Math.min(probe.ring.l, probe.halo.l) + 0.05);
      expect(ratio, `ring against its halo is ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(3);
    });
  }
}

/**
 * WebView2 hands the page's last focused control a focus event again whenever Glider gets focus back (Alt+Tab, the native
 * Store view). After a click that must not open the control's tooltip: on the Store tab an open tooltip covered the page.
 * Keyboard focus still opens it.
 */
test("a tooltip opens on keyboard focus, not when a clicked control gets focus back", async ({ page }) => {
  await page.goto("/?scenario=returning&dev=0");
  await page.waitForSelector("html[data-ready='1']");
  const store = page.getByRole("button", { name: "Store", exact: true });
  await store.click();
  await page.mouse.move(700, 400);
  await page.evaluate(() => {
    const el = document.activeElement as HTMLElement;
    el.blur();
    el.focus();
  });
  await page.waitForTimeout(700);
  await expect(page.locator('[role="tooltip"]')).toHaveCount(0);

  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  await expect(page.locator('[role="tooltip"]')).toHaveCount(1, { timeout: 2000 });
});
