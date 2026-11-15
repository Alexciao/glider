import { writeFileSync } from "node:fs";
import { expect, test, type Page } from "@playwright/test";

/**
 * The download card's band puts text over key art Glider doesn't control (docs/design-download-card.md §7.3). This measures,
 * from real pixels, the WCAG contrast of every piece of band text against what is actually painted behind it: the real wide art
 * (green and purple), flat white, bright and busy worst cases, the blurred-cover and hue-gradient fallbacks, the longest held
 * lead, a 36-character title and the Ready state, in both themes at 1280×800 and at the 900 px window minimum. Enforced: any text
 * below AA fails.
 *
 * Method (as hero-legibility.spec.ts): collect each text box (clipped to what a truncated line shows), repaint with the text
 * transparent, and take the 2nd-percentile ratio over the box, compositing semi-transparent text over each pixel first.
 *
 *   CARD_REPORT=/abs/file.json   also write the full table
 */
const QUEUE = "scenario=queue&freeze=1&seek=0.42&timescale=1";
const DL = "scenario=downloading&freeze=1&seek=0.42&timescale=1";
const offline = async (page: Page) => {
  await page.evaluate(() => (window as unknown as { __glider: { mock: { setConnectivity(c: string): void } } }).__glider.mock.setConnectivity("offline"));
  await page.waitForTimeout(700);
};

const CASES: { name: string; query: string; setup?: (page: Page) => Promise<void> }[] = [
  { name: "green art (Scarlet Sanctum II)", query: DL },
  { name: "purple art (Vault & Thorned Co.)", query: QUEUE },
  { name: "flat white", query: `${DL}&art=white` },
  { name: "bright", query: `${DL}&art=bright` },
  { name: "busy", query: `${DL}&art=busy` },
  { name: "no wide art (blurred cover)", query: `${DL}&art=noWide` },
  { name: "no art (hue gradient)", query: `${DL}&art=none` },
  { name: "longest held lead on white", query: `${QUEUE}&art=white`, setup: offline },
  { name: "held by a game", query: QUEUE, setup: offline },
  { name: "36-character title on white", query: `${DL}&art=white&data=worst` },
  { name: "ready", query: "scenario=downloading&seek=0.97&timescale=40" },
];
const SIZES = [
  { w: 1280, h: 800 },
  { w: 900, h: 700 },
] as const;
const THEMES = ["dark", "light"] as const;
const AA_TEXT = 4.5;
const AA_LARGE = 3;

interface Line {
  text: string;
  rgba: [number, number, number, number];
  px: number;
  bold: boolean;
  x: number;
  y: number;
  w: number;
  h: number;
}

async function collect(page: Page): Promise<Line[]> {
  return page.evaluate(() => {
    const root = document.querySelector<HTMLElement>("[data-band]");
    if (!root) return [];
    const probe = document.createElement("canvas");
    probe.width = probe.height = 1;
    const ctx = probe.getContext("2d", { willReadFrequently: true })!;
    const rgba = (color: string): [number, number, number, number] => {
      ctx.clearRect(0, 0, 1, 1);
      ctx.fillStyle = color;
      ctx.fillRect(0, 0, 1, 1);
      const d = ctx.getImageData(0, 0, 1, 1).data;
      return [d[0]!, d[1]!, d[2]!, d[3]! / 255];
    };
    const out: { text: string; rgba: [number, number, number, number]; px: number; bold: boolean; x: number; y: number; w: number; h: number }[] = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      const text = (n.textContent ?? "").trim();
      const el = n.parentElement;
      if (!text || !el) continue;
      const cs = getComputedStyle(el);
      const range = document.createRange();
      range.selectNodeContents(n);
      // A truncated line's text rect runs past its ellipsis: clip to what the element actually shows.
      const vis = el.getBoundingClientRect();
      for (const raw of range.getClientRects()) {
        const left = Math.max(raw.left, vis.left);
        const right = Math.min(raw.right, vis.right);
        const w = right - left;
        if (w < 4 || raw.height < 6) continue;
        out.push({ text: text.slice(0, 36), rgba: rgba(cs.color), px: parseFloat(cs.fontSize), bold: Number(cs.fontWeight) >= 600, x: Math.floor(left), y: Math.floor(raw.top), w: Math.ceil(w), h: Math.ceil(raw.height) });
      }
    }
    return out;
  });
}

async function measure(page: Page) {
  const lines = await collect(page);
  const style = await page.addStyleTag({ content: "[data-band] *{color:transparent!important;-webkit-text-fill-color:transparent!important}[data-band] svg{visibility:hidden!important}" });
  const png = await page.screenshot({ animations: "disabled", caret: "hide" });
  await style.evaluate((el) => el.parentNode?.removeChild(el));
  const stats = await page.evaluate(
    async ({ b64, boxes }) => {
      const bmp = await createImageBitmap(await (await fetch(`data:image/png;base64,${b64}`)).blob());
      const cv = new OffscreenCanvas(bmp.width, bmp.height);
      const ctx = cv.getContext("2d", { willReadFrequently: true })!;
      ctx.drawImage(bmp, 0, 0);
      const lin = (v: number) => ((v /= 255) <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
      const lum = (r: number, g: number, b: number) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
      return boxes.map((b) => {
        const d = ctx.getImageData(b.x, b.y, b.w, b.h).data;
        const [fr, fg, fb, fa] = b.rgba;
        const rs: number[] = [];
        for (let i = 0; i < d.length; i += 4) {
          const br = d[i]!, bg = d[i + 1]!, bb = d[i + 2]!;
          const L1 = lum(fa * fr + (1 - fa) * br, fa * fg + (1 - fa) * bg, fa * fb + (1 - fa) * bb);
          const L2 = lum(br, bg, bb);
          rs.push((Math.max(L1, L2) + 0.05) / (Math.min(L1, L2) + 0.05));
        }
        rs.sort((p, q) => p - q);
        return rs[Math.floor(rs.length * 0.02)]!;
      });
    },
    { b64: png.toString("base64"), boxes: lines },
  );
  return lines.map((l, i) => ({ ...l, ratio: stats[i]!, need: l.px >= 24 || (l.px >= 18.66 && l.bold) ? AA_LARGE : AA_TEXT }));
}

const rows: { id: string; text: string; px: number; need: number; ratio: number }[] = [];

test.describe.configure({ mode: "parallel" });
for (const theme of THEMES) {
  for (const { w, h } of SIZES) {
    for (const c of CASES) {
      const id = `${theme} ${w}x${h} ${c.name}`;
      test(id, async ({ page }) => {
        await page.setViewportSize({ width: w, height: h });
        await page.goto(`/?${c.query}&theme=${theme}&backdrop=solid&dev=0`);
        await page.waitForSelector("html[data-ready='1']");
        await page.evaluate(() => (window as unknown as { __glider: { ui: { getState(): { go(p: string): void } } } }).__glider.ui.getState().go("downloads"));
        await page.waitForTimeout(900);
        await c.setup?.(page);
        // The Ready case waits for the job to finish; every other case is frozen.
        if (c.name === "ready") await page.getByText("Ready to play").first().waitFor({ timeout: 15_000 });
        await page.waitForTimeout(900);
        const lines = await measure(page);
        expect(lines.length, "band text was found").toBeGreaterThan(2);
        for (const l of lines) rows.push({ id, text: l.text, px: l.px, need: l.need, ratio: Number(l.ratio.toFixed(2)) });
        const failing = lines.filter((l) => l.ratio < l.need).map((l) => `"${l.text}" ${l.px}px ${l.ratio.toFixed(2)}:1 < ${l.need}:1`);
        expect(failing, `band text below AA on ${id}`).toEqual([]);
      });
    }
  }
}

test.afterAll(() => {
  if (process.env.CARD_REPORT) writeFileSync(process.env.CARD_REPORT.replace(/\.json$/, `.${process.pid}.json`), JSON.stringify(rows, null, 1));
});
