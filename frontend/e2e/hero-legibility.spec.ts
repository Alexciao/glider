import { mkdirSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, test, type Page } from "@playwright/test";

/**
 * The Library hero puts text over key art we don't control. This measures, from real pixels, the
 * WCAG contrast of every piece of hero text against what is actually painted behind it, for the
 * default scene, the no-art fallback and three worst-case images (busy / bright / flat white), in
 * the states that make the text block tallest, at both window sizes and in both themes.
 *
 * Method: collect each text line's box and colour, repaint with the text transparent, screenshot,
 * and compare the text colour with every pixel in its box. The score is the 2nd-percentile ratio
 * (ignores a stray antialiased edge, still fails on a bright patch behind a word).
 *
 *   HERO_REPORT=/abs/file.json   also write the full table (used by the design critique)
 *   HERO_ENFORCE=1               fail on any text below AA. Off by default: the hero has not been
 *                                reworked yet and the baseline fails 20 of 60 cases (worst 2.09:1).
 */
const AA_TEXT = 4.5;
const AA_LARGE = 3;

const ARTS = ["default", "none", "busy", "bright", "white"] as const;
const CASES = [
  { name: "installed", query: "scenario=returning" },
  { name: "error", query: "scenario=diskFull" },
  { name: "downloading", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1" },
] as const;
const SIZES = [
  { w: 1280, h: 800 },
  { w: 960, h: 600 },
] as const;
const THEMES = ["dark", "light"] as const;

interface Line {
  text: string;
  /** Resolved sRGB text colour. */
  rgb: [number, number, number];
  px: number;
  bold: boolean;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface Measured extends Line {
  /** 2nd-percentile contrast across the line box. */
  ratio: number;
  min: number;
  need: number;
}

/** Text lines inside the hero's content block (falls back to the dark-scoped block of older builds). */
async function collect(page: Page): Promise<Line[]> {
  return page.evaluate(() => {
    const root = document.querySelector<HTMLElement>("main [data-hero]") ?? document.querySelector<HTMLElement>("main [data-theme]");
    if (!root) return [];
    const probe = document.createElement("canvas");
    probe.width = probe.height = 1;
    const ctx = probe.getContext("2d", { willReadFrequently: true })!;
    const toRgb = (color: string): [number, number, number] => {
      ctx.clearRect(0, 0, 1, 1);
      ctx.fillStyle = "#000";
      ctx.fillStyle = color;
      ctx.fillRect(0, 0, 1, 1);
      const d = ctx.getImageData(0, 0, 1, 1).data;
      return [d[0]!, d[1]!, d[2]!];
    };
    const out: { text: string; rgb: [number, number, number]; px: number; bold: boolean; x: number; y: number; w: number; h: number }[] = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const text = (n.textContent ?? "").trim();
      const el = n.parentElement;
      if (!text || !el) continue;
      const cs = getComputedStyle(el);
      if (cs.visibility === "hidden" || Number(cs.opacity) === 0) continue;
      const range = document.createRange();
      range.selectNodeContents(n);
      for (const r of range.getClientRects()) {
        // Skip visually-hidden text (sr-only is clipped to 1px) and anything scrolled out of view.
        if (r.width < 4 || r.height < 6 || r.bottom <= 0 || r.top >= innerHeight) continue;
        out.push({
          text: text.slice(0, 40),
          rgb: toRgb(cs.color),
          px: parseFloat(cs.fontSize),
          bold: Number(cs.fontWeight) >= 600,
          x: Math.floor(r.left),
          y: Math.floor(r.top),
          w: Math.ceil(r.width),
          h: Math.ceil(r.height),
        });
      }
    }
    return out;
  });
}

async function measure(page: Page): Promise<Measured[]> {
  const lines = await collect(page);
  // Same frame, text made invisible: what is left in each box is exactly what the text sits on.
  const style = await page.addStyleTag({
    content: "main *{color:transparent!important;text-shadow:none!important;-webkit-text-fill-color:transparent!important}main svg{visibility:hidden!important}",
  });
  const png = await page.screenshot({ animations: "disabled", caret: "hide" });
  await style.evaluate((el) => el.parentNode?.removeChild(el));
  const stats = await page.evaluate(
    async ({ b64, boxes }) => {
      const blob = await (await fetch(`data:image/png;base64,${b64}`)).blob();
      const bmp = await createImageBitmap(blob);
      const canvas = new OffscreenCanvas(bmp.width, bmp.height);
      const ctx = canvas.getContext("2d", { willReadFrequently: true })!;
      ctx.drawImage(bmp, 0, 0);
      const lin = (c: number) => {
        const s = c / 255;
        return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4;
      };
      const lum = (r: number, g: number, b: number) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
      return boxes.map((b) => {
        const x = Math.max(0, b.x);
        const y = Math.max(0, b.y);
        const w = Math.min(bmp.width - x, b.w);
        const h = Math.min(bmp.height - y, b.h);
        if (w <= 0 || h <= 0) return { ratio: 0, min: 0 };
        const d = ctx.getImageData(x, y, w, h).data;
        const fg = lum(b.rgb[0], b.rgb[1], b.rgb[2]);
        const ratios: number[] = [];
        for (let i = 0; i < d.length; i += 4) {
          const bg = lum(d[i]!, d[i + 1]!, d[i + 2]!);
          ratios.push((Math.max(fg, bg) + 0.05) / (Math.min(fg, bg) + 0.05));
        }
        ratios.sort((p, q) => p - q);
        return { ratio: ratios[Math.floor(ratios.length * 0.02)]!, min: ratios[0]! };
      });
    },
    { b64: png.toString("base64"), boxes: lines },
  );
  return lines.map((l, i) => {
    const large = l.px >= 24 || (l.px >= 18.66 && l.bold);
    return { ...l, ratio: stats[i]!.ratio, min: stats[i]!.min, need: large ? AA_LARGE : AA_TEXT };
  });
}

const report: Record<string, { worst: number; failing: { text: string; ratio: number; need: number; px: number }[] }> = {};
const enforce = process.env.HERO_ENFORCE === "1";

test.describe.configure({ mode: "parallel" });

for (const theme of THEMES) {
  for (const { w, h } of SIZES) {
    for (const art of ARTS) {
      for (const c of CASES) {
        const id = `${theme} ${w}x${h} art=${art} ${c.name}`;
        test(id, async ({ page }) => {
          await page.setViewportSize({ width: w, height: h });
          await page.goto(`/?${c.query}&art=${art}&theme=${theme}&backdrop=solid&dev=0#/game/Fortnite`);
          await page.waitForSelector("html[data-ready='1']");
          // Let the art decode and the entrance settle; measured frames have animations fast-forwarded.
          await page.waitForTimeout(1200);
          const lines = await measure(page);
          expect(lines.length, "hero text was found").toBeGreaterThan(3);
          const failing = lines.filter((l) => l.ratio < l.need);
          report[id] = {
            worst: Math.min(...lines.map((l) => l.ratio / l.need)),
            failing: failing.map((l) => ({ text: l.text, ratio: Number(l.ratio.toFixed(2)), need: l.need, px: l.px })),
          };
          test.info().annotations.push({
            type: "contrast",
            description: failing.length ? failing.map((l) => `"${l.text}" ${l.ratio.toFixed(2)}:1 (needs ${l.need})`).join("; ") : "all text passes",
          });
          if (enforce) expect(failing.map((l) => `"${l.text}" ${l.ratio.toFixed(2)}:1 < ${l.need}:1`), `hero text below AA on ${id}`).toEqual([]);
        });
      }
    }
  }
}

test.afterAll(() => {
  const file = process.env.HERO_REPORT;
  if (!file) return;
  // Workers each write their own slice; the reader merges them.
  const dir = fileURLToPath(new URL(".", `file:///${file.replace(/\\/g, "/")}`));
  mkdirSync(dir, { recursive: true });
  writeFileSync(file.replace(/\.json$/, `.${process.pid}.json`), JSON.stringify(report, null, 1));
});
