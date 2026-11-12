// Motion review: records real compositor frames (CDP screencast, up to 60 fps) around one interaction
// and lays them out as a labelled filmstrip, so a morph can be judged frame by frame.
//
//   node e2e/tools/film.mjs <take> <out.png> [--theme dark] [--ms 700] [--cols 6] [--scale 0.6] [--reduced]
//
// Needs this checkout's e2e dev server (any Playwright run leaves it up; see port.mjs). Takes are below.
import { chromium } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { dirname } from "node:path";
import { E2E_PORT as PORT } from "./port.mjs";
const [take, out, ...rest] = process.argv.slice(2);
const opt = (name, fallback) => {
  const i = rest.indexOf(`--${name}`);
  return i >= 0 ? rest[i + 1] : fallback;
};
const flag = (name) => rest.includes(`--${name}`);
const theme = opt("theme", "dark");
const ms = Number(opt("ms", 700));
const cols = Number(opt("cols", 6));
const scale = Number(opt("scale", 0.6));

const wait = (t) => new Promise((r) => setTimeout(r, t));
const hero = { x: 96, y: 500, w: 560, h: 190 };

/** name → { query, clip, prepare(page), trigger(page) } */
const TAKES = {
  // Play → Launching… → Playing: the label morph and the width reflow.
  launch: {
    query: "scenario=installed",
    clip: hero,
    trigger: (p) => p.getByRole("button", { name: /^Play$/ }).click(),
  },
  // A download finishing while you watch: working → Play plus the one-off ready moment.
  ready: {
    query: "scenario=downloading&seek=0.9992&timescale=40",
    clip: { x: 96, y: 440, w: 620, h: 250 },
    prepare: (p) => p.evaluate(() => window.__glider.mock.loadScenario("downloading", { seek: 0.9992, timeScale: 40, freeze: true })),
    trigger: (p) => p.evaluate(() => window.__glider.mock.loadScenario("downloading", { seek: 0.9992, timeScale: 40, freeze: false })),
  },
  // The flourish on its own.
  moment: {
    query: "scenario=installed",
    clip: hero,
    trigger: (p) => p.evaluate(() => window.__glider.ui.getState().markReady()),
  },
  // Update → Updating: primary to working.
  update: {
    query: "scenario=updateAvailable&timescale=1",
    clip: { x: 96, y: 440, w: 620, h: 250 },
    trigger: (p) => p.getByRole("button", { name: /^Update$/ }).click(),
  },
  // Pause ⇄ Resume on the hero.
  pause: {
    query: "scenario=downloading&seek=0.42&timescale=1",
    clip: { x: 96, y: 440, w: 620, h: 250 },
    trigger: (p) => p.getByRole("button", { name: /^Pause$/ }).click(),
  },
  // A toast arriving.
  toast: {
    query: "scenario=installed",
    clip: { x: 880, y: 640, w: 400, h: 160 },
    prepare: (p) => p.evaluate(() => window.__glider.ui.getState().go("settings")),
    trigger: (p) => p.evaluate(() => window.__glider.mock.emitNotice({ level: "success", title: "Fortnite is ready to play", message: "Version 40.20 is installed.", action: null })),
  },
  // Page switch.
  page: {
    query: "scenario=installed",
    clip: { x: 0, y: 40, w: 1280, h: 760 },
    trigger: (p) => p.getByRole("button", { name: "Downloads" }).click(),
  },
};

const t = TAKES[take];
if (!t || !out) {
  console.error(`usage: film.mjs <${Object.keys(TAKES).join("|")}> <out.png> [--theme dark|light] [--ms 700] [--cols 6] [--scale 0.6] [--reduced]`);
  process.exit(2);
}

const browser = await chromium.launch();
const context = await browser.newContext({
  viewport: { width: 1280, height: 800 },
  deviceScaleFactor: 1,
  reducedMotion: flag("reduced") ? "reduce" : "no-preference",
});
const page = await context.newPage();
await page.goto(`http://localhost:${PORT}/?${t.query}&theme=${theme}&backdrop=solid&dev=0`);
await page.waitForSelector("html[data-ready='1']");
await wait(900);
await t.prepare?.(page);
await wait(500);

const cdp = await context.newCDPSession(page);
const frames = [];
cdp.on("Page.screencastFrame", (f) => {
  frames.push({ data: f.data, ts: f.metadata.timestamp * 1000 });
  void cdp.send("Page.screencastFrameAck", { sessionId: f.sessionId }).catch(() => undefined);
});
await cdp.send("Page.startScreencast", { format: "jpeg", quality: 92, everyNthFrame: 1 });
await wait(120);
const t0 = Date.now();
await t.trigger(page);
await wait(ms);
await cdp.send("Page.stopScreencast");

// Keep the last still frame before the trigger and everything after it.
const before = frames.filter((f) => f.ts <= t0).slice(-1);
const after = frames.filter((f) => f.ts > t0);
const shown = [...before, ...after];
const { x, y, w, h } = t.clip;
const tw = Math.round(w * scale);
const th = Math.round(h * scale);
const fps = after.length > 1 ? Math.round(((after.length - 1) * 1000) / (after[after.length - 1].ts - after[0].ts)) : 0;

const sheet = await context.newPage();
await sheet.setContent(
  `<!doctype html><style>
    body{margin:0;background:#0b0b0c;font:11px/1 ui-monospace,Consolas,monospace;color:#b9b9c2}
    h1{font:600 13px system-ui;margin:0;padding:10px 10px 0;color:#eee}
    .g{display:grid;grid-template-columns:repeat(${cols},${tw}px);gap:6px;padding:10px;width:max-content}
    .f{width:${tw}px;height:${th}px;background-repeat:no-repeat;background-size:${1280 * scale}px ${800 * scale}px;background-position:${-x * scale}px ${-y * scale}px;outline:1px solid #2a2a2e}
    figure{margin:0} figcaption{padding:3px 1px 2px}
  </style><div id="s"><h1>${take} · ${theme}${flag("reduced") ? " · reduced motion" : ""} · ${after.length} frames in ${ms} ms (~${fps} fps while animating)</h1><div class="g">${shown
    .map((f) => `<figure><div class="f" style="background-image:url(data:image/jpeg;base64,${f.data})"></div><figcaption>${f.ts <= t0 ? "before" : `+${Math.round(f.ts - t0)} ms`}</figcaption></figure>`)
    .join("")}</div></div>`,
);
mkdirSync(dirname(out), { recursive: true });
await sheet.locator("#s").screenshot({ path: out });
console.log(`${out}: ${after.length} frames, ~${fps} fps`);
await browser.close();
