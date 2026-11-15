// WCAG contrast of every piece of text in the D card's art band against the pixels actually painted behind it (method of
// e2e/hero-legibility.spec.ts: collect text boxes, repaint with text transparent, compare per pixel, 2nd percentile).
// Semi-transparent text (white/80) is composited over each pixel before the ratio is taken.
//   GLIDER_E2E_PORT=5988 node e2e/mockups/contrast-card.mjs [out.json]
import { writeFileSync } from "node:fs";
import { chromium } from "@playwright/test";
import { E2E_PORT } from "../tools/port.mjs";

const DL = "scenario=downloading&freeze=1&seek=0.42&timescale=1";
const CASES = [
  ["green (Scarlet Sanctum II)", DL],
  ["purple (Vault & Thorned Co.)", "scenario=queue&freeze=1&seek=0.42&timescale=1"],
  ["flat white", `${DL}&art=white`],
  ["bright", `${DL}&art=bright`],
  ["busy", `${DL}&art=busy`],
  ["no wide art (blurred cover)", `${DL}&nowide=1`],
  ["no art (hue gradient)", `${DL}&art=none`],
  ["held, long lead", "scenario=queue&freeze=1&seek=0.42&timescale=1&hold=offline"],
  ["ready, green", `${DL}&done=1`],
  ["worst-case long title, white art", `${DL}&art=white&data=worst`],
  ["offline lead, white art", "scenario=queue&freeze=1&seek=0.42&timescale=1&hold=offline&art=white"],
];
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

const browser = await chromium.launch();
const rows = [];
for (const theme of ["dark", "light"]) {
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
  for (const [name, q] of CASES) {
    await page.goto(`http://localhost:${E2E_PORT}/?${q}&theme=${theme}&backdrop=solid&dev=0&card=d`);
    await page.waitForSelector("html[data-ready='1']");
    await page.evaluate(() => window.__glider.ui.getState().go("downloads"));
    await wait(1600);
    const lines = await page.evaluate(() => {
      const root = document.querySelector("[data-band]");
      if (!root) return [];
      const c = document.createElement("canvas");
      c.width = c.height = 1;
      const ctx = c.getContext("2d", { willReadFrequently: true });
      const rgba = (color) => {
        ctx.clearRect(0, 0, 1, 1);
        ctx.fillStyle = color;
        ctx.fillRect(0, 0, 1, 1);
        const d = ctx.getImageData(0, 0, 1, 1).data;
        return [d[0], d[1], d[2], d[3] / 255];
      };
      const out = [];
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        const text = (n.textContent ?? "").trim();
        const el = n.parentElement;
        if (!text || !el) continue;
        const cs = getComputedStyle(el);
        const r = document.createRange();
        r.selectNodeContents(n);
        // A truncated line's text rect runs past its ellipsis: clip to what the element actually shows.
        const vis = el.getBoundingClientRect();
        for (const raw of r.getClientRects()) {
          const left = Math.max(raw.left, vis.left), right = Math.min(raw.right, vis.right);
          const b = { left, top: raw.top, width: right - left, height: raw.height };
          if (b.width < 4 || b.height < 6) continue;
          out.push({ text: text.slice(0, 36), c: rgba(cs.color), px: parseFloat(cs.fontSize), bold: Number(cs.fontWeight) >= 600, x: Math.floor(b.left), y: Math.floor(b.top), w: Math.ceil(b.width), h: Math.ceil(b.height) });
        }
      }
      return out;
    });
    const style = await page.addStyleTag({ content: "[data-band] *{color:transparent!important;-webkit-text-fill-color:transparent!important}[data-band] svg{visibility:hidden!important}" });
    const png = await page.screenshot({ animations: "disabled", caret: "hide" });
    await style.evaluate((el) => el.remove());
    const stats = await page.evaluate(
      async ({ b64, boxes }) => {
        const bmp = await createImageBitmap(await (await fetch(`data:image/png;base64,${b64}`)).blob());
        const cv = new OffscreenCanvas(bmp.width, bmp.height);
        const ctx = cv.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(bmp, 0, 0);
        const lin = (v) => ((v /= 255) <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
        const lum = (r, g, b) => 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
        return boxes.map((b) => {
          const d = ctx.getImageData(b.x, b.y, b.w, b.h).data;
          const [fr, fg, fb, fa] = b.c;
          const rs = [];
          for (let i = 0; i < d.length; i += 4) {
            const br = d[i], bg = d[i + 1], bb = d[i + 2];
            const L1 = lum(fa * fr + (1 - fa) * br, fa * fg + (1 - fa) * bg, fa * fb + (1 - fa) * bb);
            const L2 = lum(br, bg, bb);
            rs.push((Math.max(L1, L2) + 0.05) / (Math.min(L1, L2) + 0.05));
          }
          rs.sort((p, q) => p - q);
          return { p2: rs[Math.floor(rs.length * 0.02)], min: rs[0] };
        });
      },
      { b64: png.toString("base64"), boxes: lines },
    );
    for (const [i, l] of lines.entries()) {
      const large = l.px >= 24 || (l.px >= 18.66 && l.bold);
      rows.push({ theme, art: name, text: l.text, px: l.px, need: large ? 3 : 4.5, ratio: Number(stats[i].p2.toFixed(2)) });
    }
  }
  await page.close();
}
await browser.close();
const fails = rows.filter((r) => r.ratio < r.need);
const worst = [...rows].sort((a, b) => a.ratio / a.need - b.ratio / b.need).filter((r, i, all) => all.findIndex((x) => x.art === r.art && x.text === r.text) === i).slice(0, 10);
console.log(`${rows.length} text boxes, ${fails.length} below AA`);
for (const r of worst) console.log(`${r.ratio.toFixed(2)} (need ${r.need})  ${r.theme}  ${r.art}  "${r.text}" ${r.px}px`);
if (process.argv[2]) writeFileSync(process.argv[2], JSON.stringify(rows, null, 1));
