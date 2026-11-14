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
          return { label, outlined: own.outlineStyle !== "none" && parseFloat(own.outlineWidth) > 0, wrapperRing };
        });
        if (info && !info.outlined && !info.wrapperRing) missing.push(info.label);
      }
      expect(missing, `Tab stops with no visible focus ring: ${missing.join(", ")}`).toEqual([]);
    });
  }
}
