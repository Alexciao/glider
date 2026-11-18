import { existsSync, mkdirSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, type Page } from "@playwright/test";

/**
 * The hero action-group candidates (`?actions=a|b|c`, features/game/ActionCandidates.tsx) next to the shipped design, in every
 * state the group has: dark, Mica-simulated, 1280×800, cropped to the hero's lower-left (title, status, the group and room for
 * its menu and a sub-menu). Then one labelled contact sheet per candidate.
 *
 *   ACTIONS_OUT=/abs/dir    where to write (default docs/screenshots/actions-candidates)
 *   ACTIONS_ONLY=a,c        candidates to shoot (current, a, b, c)
 *   ACTIONS_SHEETS=1        only build the sheets from what's already on disk
 */
const OUT = process.env.ACTIONS_OUT
  ? `${process.env.ACTIONS_OUT.replace(/[\\/]+$/, "")}/`
  : fileURLToPath(new URL("../../docs/screenshots/actions-candidates/", import.meta.url));

const CANDIDATES = [
  { id: "current", query: "", title: "Current", blurb: "White split button (Play | chevron) and a separate glass star." },
  { id: "a", query: "&actions=a", title: "A · Dock", blurb: "One glass tray: primary, favorite and more inside it. The primary lights up in accent only when it's THE action." },
  { id: "b", query: "&actions=b", title: "B · Pill + orbs", blurb: "A soft pearl pill, then round glass orbs (favorite, more) of one material. No chevron." },
  { id: "c", query: "&actions=c", title: "C · Command", blurb: "A wide, tall glide-blue command that carries its cost (size, percent) and becomes the progress bar during a job." },
] as const;
type Candidate = (typeof CANDIDATES)[number];

const only = process.env.ACTIONS_ONLY?.split(",").filter(Boolean);
const SHEETS = process.env.ACTIONS_SHEETS === "1";

const WALLPAPER = `radial-gradient(900px 640px at 8% 12%, #ff6a00 0%, transparent 64%),
  radial-gradient(860px 600px at 92% 88%, #00c2a8 0%, transparent 62%),
  radial-gradient(760px 520px at 70% 6%, #e0109a 0%, transparent 60%),
  radial-gradient(700px 520px at 20% 96%, #2f6bff 0%, transparent 60%), #6b2bd6`;
const MICA_TINT = "rgb(32 32 32 / 0.62)";

const PLAYABLE = "g.kind==='game'&&g.installStatus==='installed'&&g.thirdParty==='none'&&!g.updateAvailable&&g.canRunOffline&&g.saveStatus==='inSync'&&g.appName!=='Fortnite'";
const FRESH = "g.kind==='game'&&g.installStatus==='notInstalled'&&g.thirdParty==='none'&&g.dlcCount>0";
const RUNNING = "g.runState==='running'";

const wait = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const goGame = (page: Page, app: string) =>
  page.evaluate((a) => (window as unknown as { __glider: { router: { getState(): { go(r: string): void } } } }).__glider.router.getState().go(`game/${a}`), app);
const pick = async (page: Page, pred: string): Promise<string> => {
  const app = (await page.evaluate(`(() => [...window.__glider.library.getState().games.values()].find((g) => ${pred}))()?.appName ?? null`)) as string | null;
  if (!app) throw new Error(`no game matches ${pred}`);
  return app;
};
/** The game whose job is running (the downloading scenario's job isn't Fortnite's). */
const JOB = Symbol("job");
const jobApp = async (page: Page): Promise<string> => {
  const app = (await page.evaluate(
    `window.__glider.app.getState().snapshot.queue.jobs.find((j) => j.status === "running")?.appName ?? null`,
  )) as string | null;
  if (!app) throw new Error("no running job");
  return app;
};
const open = async (page: Page, pred: string | null | typeof JOB): Promise<string> => {
  const app = pred === JOB ? await jobApp(page) : pred ? await pick(page, pred) : "Fortnite";
  await goGame(page, app);
  await page.locator("[data-hero-actions] [data-kind]").first().waitFor();
  await wait(700);
  return app;
};
const primary = (page: Page) => page.locator("[data-hero-actions] [data-kind]").first();
const more = (page: Page) => page.locator("[data-hero-actions]").getByRole("button", { name: "More actions" });
const favorite = (page: Page) => page.locator("[data-hero-actions]").getByRole("button", { name: "Favorite" });
/** Somewhere over the art with nothing interactive under it, so no hover or tooltip lingers. */
const park = async (page: Page) => {
  const hero = await page.locator("[data-hero]").boundingBox();
  if (hero) await page.mouse.move(hero.x + hero.width - 60, hero.y + 40);
  await wait(450);
};
const setFavorite = async (page: Page, on: boolean) => {
  const b = favorite(page);
  if ((await b.getAttribute("aria-pressed")) !== String(on)) await b.click();
  await park(page);
};

/** Open a game's page and leave it at rest (favorite set as asked, mouse parked). */
const rest =
  (pred: string | null | typeof JOB, fav?: boolean) =>
  async (p: Page): Promise<void> => {
    await open(p, pred);
    if (fav === undefined) await park(p);
    else await setFavorite(p, fav);
  };

interface Shot {
  file: string;
  label: string;
  query: string;
  setup: (page: Page) => Promise<void>;
  live?: boolean;
}

const DL = "scenario=downloading&freeze=1&seek=0.42&timescale=1";
const SHOTS: Shot[] = [
  { file: "01-play", label: "Play", query: "scenario=returning", setup: rest(PLAYABLE, false) },
  {
    file: "02-play-hover",
    label: "Play, hovered",
    query: "scenario=returning",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await setFavorite(p, false);
      await primary(p).hover();
      await wait(300);
    },
  },
  {
    file: "03-play-focus",
    label: "Play, keyboard focus",
    query: "scenario=returning",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await setFavorite(p, false);
      await p.keyboard.press("Shift");
      await primary(p).focus();
      await wait(250);
    },
  },
  {
    file: "04-menu",
    label: "Menu open",
    query: "scenario=returning",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await setFavorite(p, false);
      await more(p).click();
      await wait(400);
    },
  },
  {
    file: "05-submenu",
    label: "Menu + Create shortcut",
    query: "scenario=returning",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await setFavorite(p, false);
      await more(p).click();
      await wait(350);
      await p.getByRole("menuitem", { name: "Create shortcut" }).hover();
      await wait(500);
    },
  },
  { file: "06a-favorite-off", label: "Favorite off", query: "scenario=returning", setup: rest(PLAYABLE, false) },
  { file: "06b-favorite-on", label: "Favorite on", query: "scenario=returning", setup: rest(PLAYABLE, true) },
  { file: "07a-update", label: "Update available", query: "scenario=updates", setup: rest(null) },
  {
    file: "07b-update-menu",
    label: "Update + menu (Play without updating)",
    query: "scenario=updates",
    setup: async (p) => {
      await open(p, null);
      await more(p).click();
      await wait(400);
    },
  },
  { file: "08-install", label: "Install (not installed)", query: "scenario=returning", setup: rest(FRESH) },
  {
    file: "09-launching",
    label: "Launching",
    query: "scenario=returning&freeze=1",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await setFavorite(p, false);
      await primary(p).click();
      await park(p);
      await wait(700);
    },
  },
  { file: "10-running", label: "Running (Close game)", query: "scenario=playing", setup: rest(RUNNING) },
  { file: "11-downloading", label: "Downloading (Pause + progress)", query: DL, setup: rest(JOB) },
  {
    file: "12-paused",
    label: "Paused (Resume)",
    query: DL,
    setup: async (p) => {
      await open(p, JOB);
      await wait(300);
      await primary(p).click();
      await park(p);
      await wait(600);
    },
  },
  {
    file: "13-signed-out",
    label: "Signed out (Sign in)",
    query: "scenario=returning",
    setup: async (p) => {
      await open(p, PLAYABLE);
      await p.getByRole("button", { name: /^Account:/ }).click();
      await wait(300);
      await p.getByRole("menuitem", { name: "Sign out" }).click();
      await wait(900);
      await park(p);
    },
  },
  {
    file: "14-ready-moment",
    label: "Ready moment (sheen frozen mid-pass)",
    query: "scenario=returning",
    live: true,
    setup: async (p) => {
      const app = await open(p, PLAYABLE);
      await park(p);
      await p.evaluate((a) => (window as unknown as { __glider: { ui: { getState(): { markReady(a: string): void } } } }).__glider.ui.getState().markReady(a), app);
      await wait(200);
      // Freeze the flourish at a readable frame (live timing made the sheen land anywhere from off-face to gone).
      await p.evaluate(() => {
        for (const a of document.getAnimations()) {
          const name = (a as CSSAnimation).animationName;
          if (name !== "g-sheen" && name !== "g-ready-ring") continue;
          a.pause();
          a.currentTime = name === "g-sheen" ? 270 : 110;
        }
      });
    },
  },
];

/**
 * The crop: from just left of the hero and a little above the title to 480 px down; taller when an open menu (or sub-menu)
 * reaches further, so a menu is never cut by the crop.
 */
async function clipFor(page: Page) {
  const hero = (await page.locator("[data-hero]").boundingBox())!;
  const row = (await page.locator("[data-hero-actions]").boundingBox())!;
  const y = Math.max(0, Math.round(row.y - 116));
  const menuBottom = await page.evaluate(() =>
    Math.max(0, ...[...document.querySelectorAll("[role=menu]")].map((m) => m.getBoundingClientRect().bottom)),
  );
  const height = Math.min(800 - y, Math.max(480, Math.ceil(menuBottom + 16 - y)));
  return { x: Math.round(hero.x) - 16, y, width: 780, height };
}

if (!SHEETS) {
  for (const c of CANDIDATES) {
    if (only && !only.includes(c.id)) continue;
    test.describe(`actions ${c.id}`, () => {
      test.use({ viewport: { width: 1280, height: 800 } });
      mkdirSync(`${OUT}${c.id}`, { recursive: true });
      for (const shot of SHOTS) {
        test(shot.file, async ({ page }) => {
          const errors: string[] = [];
          page.on("pageerror", (e) => errors.push(e.message));
          await page.goto(`/?${shot.query}&theme=dark&backdrop=mica&dev=0${c.query}`);
          await page.waitForSelector("html[data-ready='1']", { timeout: 20_000 });
          await page.addStyleTag({ content: `html{background:linear-gradient(${MICA_TINT},${MICA_TINT}),${WALLPAPER} !important}` });
          await wait(500);
          await shot.setup(page);
          await page.screenshot({ path: `${OUT}${c.id}/${shot.file}.png`, clip: await clipFor(page), animations: shot.live ? "allow" : "disabled", caret: "hide" });
          if (errors.length) throw new Error(`Page errors:\n${errors.join("\n")}`);
        });
      }
    });
  }
} else {
  test.describe("actions sheets", () => {
    for (const c of CANDIDATES) {
      if (only && !only.includes(c.id)) continue;
      test(`sheet ${c.id}`, async ({ page }) => sheet(page, c));
    }
  });
}

async function sheet(page: Page, c: Candidate) {
  const tiles = SHOTS.map((s) => ({ s, path: `${OUT}${c.id}/${s.file}.png` })).filter((t) => existsSync(t.path));
  const esc = (t: string) => t.replace(/&/g, "&amp;").replace(/</g, "&lt;");
  const html = `<!doctype html><meta charset="utf-8"><style>
    body{margin:0;background:#0d0d10;color:#d7d7de;font:13px/1.35 "Segoe UI",system-ui,sans-serif}
    .w{padding:28px 28px 32px;width:max-content}
    h1{margin:0;font-size:26px;font-weight:600;letter-spacing:-.02em;color:#f4f4f6}
    p{margin:6px 0 22px;color:#9a9aa6;font-size:14px}
    .g{display:grid;grid-template-columns:repeat(3,780px);gap:22px 18px}
    figure{margin:0} img{display:block;width:780px;height:auto;border-radius:6px;outline:1px solid #26262c}
    figcaption{padding:7px 2px 0;color:#c9c9d1} figcaption b{color:#7d7d89;font-weight:500;margin-right:6px}
  </style><div class="w"><h1>${esc(c.title)}</h1><p>${esc(c.blurb)}</p><div class="g">${tiles
    .map(
      ({ s, path }) =>
        `<figure><img src="data:image/png;base64,${readFileSync(path).toString("base64")}"><figcaption><b>${s.file.slice(0, 3).replace(/-$/, "")}</b>${esc(s.label)}</figcaption></figure>`,
    )
    .join("")}</div></div>`;
  await page.setViewportSize({ width: 2600, height: 1200 });
  await page.setContent(html, { waitUntil: "load" });
  await page.locator(".w").screenshot({ path: `${OUT}sheet-${c.id}.png` });
}
