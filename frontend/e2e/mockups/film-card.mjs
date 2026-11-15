// Filmstrips of the active download card (docs/design-download-card.md): real compositor frames around one interaction.
//   node e2e/mockups/film-card.mjs <take> <out.png> [--theme dark] [--ms 1200] [--cols 3] [--scale 0.45] [--every 1] [--mock <name>]
// Mockups: GLIDER_E2E_PORT=<mockup server port> CARD=a|b|c (see vite.config.mjs).
import { chromium } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { dirname } from "node:path";
import { E2E_PORT as PORT } from "../tools/port.mjs";

const [take, out, ...rest] = process.argv.slice(2);
const opt = (n, f) => (rest.indexOf(`--${n}`) >= 0 ? rest[rest.indexOf(`--${n}`) + 1] : f);
const theme = opt("theme", "dark");
const ms = Number(opt("ms", 1200));
const cols = Number(opt("cols", 3));
const scale = Number(opt("scale", 0.45));
const every = Number(opt("every", 1));
const wait = (t) => new Promise((r) => setTimeout(r, t));

const TAKES = {
  pause: { q: "scenario=downloading&seek=0.42&timescale=1", trigger: (p) => p.getByRole("button", { name: "Pause", exact: true }).click() },
  resume: {
    q: "scenario=downloading&seek=0.42&timescale=1",
    prepare: async (p) => { await p.getByRole("button", { name: "Pause", exact: true }).click(); await wait(1500); },
    trigger: (p) => p.getByRole("button", { name: "Resume", exact: true }).click(),
  },
  // Downloading -> Installing as the last bytes arrive.
  phase: { q: `scenario=downloading&seek=${process.env.SEEK ?? 0.9}&timescale=${process.env.TS ?? 1}`, trigger: () => wait(0) },
};

const t = TAKES[take];
if (!t || !out) throw new Error(`usage: film-card.mjs <${Object.keys(TAKES).join("|")}> <out.png>`);
const browser = await chromium.launch();
const context = await browser.newContext({ viewport: { width: 1280, height: 800 }, deviceScaleFactor: 1 });
const page = await context.newPage();
await page.goto(`http://localhost:${PORT}/?${t.q}&theme=${theme}&backdrop=solid&dev=0${process.env.CARD ? `&card=${process.env.CARD}` : ""}`);
await page.waitForSelector("html[data-ready='1']");
await page.evaluate(() => window.__glider.ui.getState().go("downloads"));
await wait(1200);
await t.prepare?.(page);
const box = await page.locator(".\\@container").first().boundingBox();
const clip = { x: box.x - 8, y: box.y - 8, w: box.width + 16, h: box.height + 16 };

const frames = [];
const gap = Number(opt("gap", 0));
if (gap > 0) {
  // Long takes: one still every `gap` ms instead of the screencast (which only sends changed frames).
  const t0 = Date.now();
  await t.trigger(page);
  while (Date.now() - t0 < ms) {
    const buf = await page.screenshot({ type: "jpeg", quality: 92 });
    frames.push({ data: buf.toString("base64"), ts: Date.now(), rel: Date.now() - t0 });
    await wait(gap);
  }
  const tw = Math.round(clip.w * scale);
  const th = Math.round(clip.h * scale);
  const sheet = await context.newPage();
  await sheet.setContent(
    `<!doctype html><style>
      body{margin:0;background:#0b0b0c;font:11px/1 ui-monospace,Consolas,monospace;color:#b9b9c2}
      h1{font:600 13px system-ui;margin:0;padding:10px 10px 0;color:#eee}
      .g{display:grid;grid-template-columns:repeat(${cols},${tw}px);gap:6px;padding:10px;width:max-content}
      .f{width:${tw}px;height:${th}px;background-repeat:no-repeat;background-size:${1280 * scale}px ${800 * scale}px;background-position:${-clip.x * scale}px ${-clip.y * scale}px;outline:1px solid #2a2a2e}
      figure{margin:0} figcaption{padding:3px 1px 2px}
    </style><div id="s"><h1>${take} · ${theme} · one still every ${gap} ms</h1><div class="g">${frames
      .map((f) => `<figure><div class="f" style="background-image:url(data:image/jpeg;base64,${f.data})"></div><figcaption>+${(f.rel / 1000).toFixed(1)} s</figcaption></figure>`)
      .join("")}</div></div>`,
  );
  mkdirSync(dirname(out), { recursive: true });
  await sheet.locator("#s").screenshot({ path: out });
  console.log(`${out}: ${frames.length} stills`);
  await browser.close();
  process.exit(0);
}
const cdp = await context.newCDPSession(page);
cdp.on("Page.screencastFrame", (f) => {
  frames.push({ data: f.data, ts: f.metadata.timestamp * 1000 });
  void cdp.send("Page.screencastFrameAck", { sessionId: f.sessionId }).catch(() => undefined);
});
await cdp.send("Page.startScreencast", { format: "jpeg", quality: 92, everyNthFrame: 1 });
await wait(150);
const t0 = Date.now();
await t.trigger(page);
await wait(ms);
await cdp.send("Page.stopScreencast");

const before = frames.filter((f) => f.ts <= t0).slice(-1);
const after = frames.filter((f) => f.ts > t0).filter((_, i) => i % every === 0);
const shown = [...before, ...after];
const tw = Math.round(clip.w * scale);
const th = Math.round(clip.h * scale);
const sheet = await context.newPage();
await sheet.setContent(
  `<!doctype html><style>
    body{margin:0;background:#0b0b0c;font:11px/1 ui-monospace,Consolas,monospace;color:#b9b9c2}
    h1{font:600 13px system-ui;margin:0;padding:10px 10px 0;color:#eee}
    .g{display:grid;grid-template-columns:repeat(${cols},${tw}px);gap:6px;padding:10px;width:max-content}
    .f{width:${tw}px;height:${th}px;background-repeat:no-repeat;background-size:${1280 * scale}px ${800 * scale}px;background-position:${-clip.x * scale}px ${-clip.y * scale}px;outline:1px solid #2a2a2e}
    figure{margin:0} figcaption{padding:3px 1px 2px}
  </style><div id="s"><h1>${take} · ${theme} · ${shown.length} frames over ${ms} ms</h1><div class="g">${shown
    .map((f) => `<figure><div class="f" style="background-image:url(data:image/jpeg;base64,${f.data})"></div><figcaption>${f.ts <= t0 ? "before" : `+${Math.round(f.ts - t0)} ms`}</figcaption></figure>`)
    .join("")}</div></div>`,
);
mkdirSync(dirname(out), { recursive: true });
await sheet.locator("#s").screenshot({ path: out });
console.log(`${out}: ${after.length} frames`);
await browser.close();
