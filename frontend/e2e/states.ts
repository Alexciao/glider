import type { Page } from "@playwright/test";

/**
 * Every screen/state the screenshot suite captures. A state is a mock scenario (+ options) and an
 * optional script that drives the UI into the state (clicks, or the window.__glider test handle).
 */
export interface ShotState {
  name: string;
  /** Extra mock query params, e.g. "scenario=returning&freeze=1". */
  query: string;
  setup?: (page: Page) => Promise<void>;
  /** Extra settle time after setup, ms (default 350). */
  settle?: number;
  /** Capture mid-animation instead of fast-forwarding CSS animations to their end. */
  live?: boolean;
}

/** Structural subset of window.__glider (src/app/testHooks.ts); kept local so e2e needn't compile the app. */
interface G {
  ui: { getState(): { go(page: string, section?: string): void; openDialog(d: object): void; markReady(appName: string): void } };
  app: { getState(): { snapshot: { settings: Record<string, unknown> } | null } };
  router: { getState(): { go(route: string): void } };
  rpc(method: string, params?: unknown): Promise<unknown>;
  mock: { failNext(code: string): void; failActiveJob(code: string): void; emitNotice(n: object): void; setConnectivity(c: "online" | "offline"): void };
}
const g = <T>(page: Page, fn: (g: G) => T | Promise<T>): Promise<T> =>
  page.evaluate(`(${fn.toString()})(window.__glider)`) as Promise<T>;

const btn = (page: Page, name: string | RegExp) => page.getByRole("button", { name });
const go = (page: Page, p: string, section?: string) =>
  page.evaluate(([pg, sec]) => (window as unknown as { __glider: G }).__glider.ui.getState().go(pg!, sec), [p, section] as const);
/** v2 has one page per game: the v0.1 "library" screens are Fortnite's game page. */
const goGame = (page: Page, appName = "Fortnite") =>
  page.evaluate((a) => (window as unknown as { __glider: G }).__glider.router.getState().go(`game/${a}`), appName);
/** A plain installed game that is ready to play (not Fortnite, which has an update pending), and a fresh one with DLC. */
const PLAYABLE = "g.kind==='game'&&g.installStatus==='installed'&&g.thirdParty==='none'&&!g.updateAvailable&&g.canRunOffline&&g.saveStatus==='inSync'&&g.appName!=='Fortnite'";
const FRESH = "g.kind==='game'&&g.installStatus==='notInstalled'&&g.thirdParty==='none'&&g.dlcCount>0";
/** Opens the page of the first game matching a predicate (source text evaluated in the page) and returns its appName. */
const goPicked = async (page: Page, pred: string): Promise<string> => {
  const app = (await page.evaluate(`(() => [...window.__glider.library.getState().games.values()].find((g) => ${pred}))()?.appName ?? null`)) as string | null;
  if (!app) throw new Error(`no game matches ${pred}`);
  await goGame(page, app);
  return app;
};
const wait = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));
const finishOnboarding = async (page: Page) => {
  await g(page, (h) => h.rpc("onboarding.complete"));
  await wait(400);
};

const RAW_STATES: ShotState[] = [
  // ------------------------------------------------------------ onboarding
  { name: "onboarding-1-welcome", query: "scenario=firstRun" },
  {
    name: "onboarding-2-signin",
    query: "scenario=firstRun",
    setup: async (p) => {
      await btn(p, "Get started").click();
      await wait(700);
    },
  },
  {
    name: "onboarding-2-signin-waiting",
    query: "scenario=firstRun&latency=1",
    setup: async (p) => {
      await btn(p, "Get started").click();
      await wait(700);
      await btn(p, /Sign in with Epic/).click();
      await wait(350);
    },
  },
  {
    name: "onboarding-2-signin-error",
    query: "scenario=firstRun",
    setup: async (p) => {
      await btn(p, "Get started").click();
      await wait(700);
      await g(p, (h) => h.mock.failNext("authFailed"));
      await btn(p, /Sign in with Epic/).click();
      await wait(1900);
    },
  },
  {
    name: "onboarding-2-signin-offline",
    query: "scenario=firstRun",
    setup: async (p) => {
      await g(p, (h) => h.mock.setConnectivity("offline"));
      await btn(p, "Get started").click();
      await wait(800);
    },
  },
  { name: "onboarding-3-scanning", query: "scenario=firstRunSignedIn&latency=40", settle: 300 },
  { name: "onboarding-3-scan", query: "scenario=firstRunSignedIn", settle: 1100 },
  {
    name: "onboarding-3-scan-rest",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(1100);
      await p.evaluate(() => {
        const el = document.querySelector("main .overflow-y-auto");
        if (el) el.scrollTop = el.scrollHeight;
      });
    },
  },
  {
    name: "onboarding-3-importing",
    query: "scenario=firstRunSignedIn&timescale=2&freeze=1",
    setup: async (p) => {
      await wait(1100);
      await btn(p, /^Import \d+ games$/).click();
      await wait(1200);
    },
  },
  {
    name: "onboarding-3-import-done",
    query: "scenario=firstRunSignedIn&timescale=400",
    setup: async (p) => {
      await wait(1100);
      await btn(p, /^Import \d+ games$/).click();
      await p.getByText(/games imported|of \d+ imported/).waitFor({ timeout: 20_000 });
      await wait(300);
    },
  },
  {
    name: "onboarding-3-import-failed",
    query: "scenario=firstRunSignedIn&timescale=1",
    setup: async (p) => {
      await wait(1100);
      await p.getByRole("checkbox", { name: "Deselect all" }).click();
      await p.getByRole("checkbox").nth(1).click();
      await btn(p, "Import 1 game").click();
      await wait(700);
      await g(p, (h) => h.mock.failActiveJob("filesLocked"));
      await wait(700);
    },
  },
  { name: "onboarding-3-empty", query: "scenario=firstRunSignedIn&games=0", settle: 1100 },
  {
    name: "onboarding-3-locate-invalid",
    query: "scenario=firstRunSignedIn&timescale=400",
    setup: async (p) => {
      await wait(1100);
      await btn(p, /Locate another folder/).click();
      await p.getByText(/imported/).first().waitFor({ timeout: 15_000 });
      await btn(p, /Locate another folder/).click();
      await wait(1500);
    },
  },
  {
    name: "onboarding-4-folders-empty",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(700);
      await g(p, (h) => h.rpc("onboarding.setStep", { step: "folders", skipped: false }));
      await wait(700);
    },
  },
  {
    name: "onboarding-4-folders",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(700);
      await g(p, (h) => h.rpc("onboarding.setStep", { step: "folders", skipped: false }));
      await wait(600);
      await btn(p, "Add a library folder").click();
      await wait(500);
      await btn(p, "Add a folder…").click();
      await wait(500);
      await btn(p, "Add a folder…").click();
      await wait(700);
    },
  },
  {
    name: "onboarding-5-mode",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(700);
      await g(p, (h) => h.rpc("onboarding.setStep", { step: "mode", skipped: false }));
      await wait(700);
    },
  },
  {
    name: "onboarding-6-done",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(700);
      await g(p, (h) => h.rpc("onboarding.setStep", { step: "done", skipped: false }));
      await wait(900);
    },
  },

  // --------------------------------------------------------------- library
  { name: "library-installed", query: "scenario=returning" },
  { name: "library-installed-no-art", query: "scenario=returning&art=none" },
  // Worst-case key art: the hero must stay AA on anything Epic's catalog can serve.
  { name: "library-art-busy", query: "scenario=returning&art=busy" },
  { name: "library-art-bright", query: "scenario=returning&art=bright" },
  { name: "library-art-white", query: "scenario=returning&art=white" },
  { name: "library-art-busy-downloading", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1&art=busy", settle: 900 },
  { name: "library-art-bright-error", query: "scenario=diskFull&art=bright", settle: 700 },
  { name: "library-update-available", query: "scenario=updates" },
  { name: "library-downloading", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", settle: 900 },
  {
    name: "library-downloading-paused",
    query: "scenario=downloading&freeze=1&seek=0.42&timescale=1",
    setup: async (p) => {
      await wait(700);
      await go(p, "downloads");
      await wait(400);
      await p.getByRole("button", { name: "Pause", exact: true }).click();
      await wait(500);
      await goGame(p);
    },
  },
  { name: "library-waiting-for-egl", query: "scenario=eglBusy&freeze=1", settle: 700 },
  { name: "library-error-disk-full", query: "scenario=diskFull", settle: 700 },
  { name: "library-auth-expired", query: "scenario=authExpired" },
  { name: "library-playing", query: "scenario=playing" },
  {
    name: "library-launching",
    query: "scenario=returning&freeze=1",
    setup: async (p) => {
      await goPicked(p, PLAYABLE);
      await wait(500);
      await btn(p, "Play").click();
      await wait(1100);
    },
  },
  {
    name: "library-error-launch-failed",
    query: "scenario=returning",
    setup: async (p) => {
      await goPicked(p, PLAYABLE);
      await wait(500);
      await g(p, (h) => h.mock.failNext("launchFailed"));
      await btn(p, "Play").click();
      await wait(900);
    },
  },
  {
    name: "library-error-network",
    query: "scenario=downloading&freeze=1&seek=0.42&timescale=1",
    setup: async (p) => {
      await wait(800);
      await g(p, (h) => h.mock.failActiveJob("network"));
      await wait(900);
    },
  },
  {
    name: "library-signed-out",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await btn(p, /^Account:/).click();
      await wait(300);
      await p.getByRole("menuitem", { name: "Sign out" }).click();
      await wait(900);
    },
  },
  {
    name: "library-existing-install-hint",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await finishOnboarding(p);
    },
    settle: 900,
  },
  {
    name: "library-not-installed",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await finishOnboarding(p);
    },
    settle: 900,
  },
  {
    name: "library-ready-moment",
    query: "scenario=returning",
    live: true,
    setup: async (p) => {
      await wait(900);
      await g(p, (h) => h.ui.getState().markReady("Fortnite"));
      await wait(650);
    },
    settle: 0,
  },
  {
    name: "library-menu-more",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await btn(p, "More actions").click();
      await wait(350);
    },
  },
  {
    name: "library-menu-account",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await btn(p, /^Account:/).click();
      await wait(350);
    },
  },
  {
    name: "library-command-palette",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Control+k");
      await wait(250);
    },
  },
  {
    name: "library-command-palette-query",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Control+k");
      await p.keyboard.type("set");
      await wait(250);
    },
  },
  {
    name: "library-command-palette-game",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Control+k");
      await p.keyboard.type("fort");
      await wait(250);
    },
  },
  {
    name: "library-command-palette-none",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Control+k");
      await p.keyboard.type("qzqzq");
      await wait(250);
    },
  },
  {
    name: "library-command-palette-offline",
    query: "scenario=offline",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Control+k");
      await p.keyboard.type("check");
      await wait(250);
    },
  },
  {
    name: "toasts",
    query: "scenario=returning",
    live: true,
    setup: async (p) => {
      await wait(700);
      await g(p, (h) => {
        h.mock.emitNotice({ level: "success", title: "Fortnite is ready to play", message: "Version 40.20 installed.", action: null });
        h.mock.emitNotice({ level: "error", title: "Can't reach Epic", message: "Check your internet connection, then retry. Progress is kept.", action: "retry" });
      });
      await wait(900);
    },
    settle: 0,
  },

  {
    name: "focus-library-play",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await goPicked(p, PLAYABLE);
      await wait(300);
      await p.keyboard.press("Tab");
      await btn(p, /^Play$/).focus();
      await wait(200);
    },
  },
  {
    name: "focus-rail",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(600);
      await p.keyboard.press("Tab");
      await btn(p, "Downloads").focus();
      await wait(200);
    },
  },
  {
    name: "focus-settings-switch",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "startup");
      await wait(500);
      await p.keyboard.press("Tab");
      await p.getByRole("switch", { name: /Start with Windows/ }).focus();
      await wait(200);
    },
  },

  // ------------------------------------------------------------- downloads
  { name: "downloads-active", query: "scenario=downloading&freeze=1&seek=0.42&timescale=1", setup: async (p) => go(p, "downloads"), settle: 900 },
  {
    name: "downloads-paused",
    query: "scenario=downloading&freeze=1&seek=0.42&timescale=1",
    setup: async (p) => {
      await go(p, "downloads");
      await wait(500);
      await p.getByRole("button", { name: "Pause", exact: true }).click();
    },
    settle: 700,
  },
  { name: "downloads-waiting-for-egl", query: "scenario=eglBusy&freeze=1", setup: async (p) => go(p, "downloads"), settle: 700 },
  { name: "downloads-history", query: "scenario=returning", setup: async (p) => go(p, "downloads") },
  { name: "downloads-failed-disk-full", query: "scenario=diskFull", setup: async (p) => go(p, "downloads") },
  {
    name: "downloads-empty",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await finishOnboarding(p);
      await go(p, "downloads");
    },
  },
  {
    name: "downloads-queued",
    query: "scenario=updates&freeze=1",
    setup: async (p) => {
      await go(p, "downloads");
      await g(p, (h) => h.rpc("game.update", { appName: "Fortnite" }));
      await wait(500);
      await g(p, (h) => h.rpc("game.verify", { appName: "Fortnite" }));
      await wait(700);
    },
  },
  {
    name: "downloads-verifying",
    query: "scenario=returning&freeze=1",
    setup: async (p) => {
      await go(p, "downloads");
      await g(p, (h) => h.rpc("game.verify", { appName: "Fortnite" }));
      await wait(1800);
    },
  },
  // One state per job phase (frozen mock scenarios), the busy queue, an offline queue, and a game running.
  ...(["Preparing", "Verifying", "Repairing", "Installing", "Finalizing", "Moving", "Removing", "Importing"] as const).map<ShotState>((phase) => ({
    name: `downloads-phase-${phase.toLowerCase()}`,
    query: `scenario=frozen${phase}`,
    setup: async (p) => go(p, "downloads"),
    settle: 900,
  })),
  { name: "downloads-queue", query: "scenario=queue&freeze=1&seek=0.42&timescale=1", setup: async (p) => go(p, "downloads"), settle: 1200 },
  {
    name: "downloads-offline",
    query: "scenario=queue&freeze=1&seek=0.42&timescale=1",
    setup: async (p) => {
      await go(p, "downloads");
      await wait(400);
      await p.evaluate(() => (window as unknown as { __glider: G }).__glider.mock.setConnectivity("offline"));
    },
    settle: 900,
  },
  {
    name: "downloads-game-running",
    query: "scenario=playing",
    setup: async (p) => {
      await go(p, "downloads");
      await g(p, (h) => h.rpc("library.updateAll"));
    },
    settle: 900,
  },

  // -------------------------------------------------------------- settings
  ...(["downloads", "library", "updates", "saves", "egl", "overlay", "links", "appearance", "startup", "account", "advanced", "about"] as const).map<ShotState>((s) => ({
    name: `settings-${s}`,
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", s);
    },
    settle: s === "about" ? 900 : 450,
  })),
  {
    name: "settings-downloads-advanced",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(400);
      await p.locator("#settings-downloads").getByRole("button", { name: "Advanced" }).click();
      await wait(500);
      await p.getByRole("radio", { name: "Custom" }).first().click();
      await wait(400);
    },
  },
  {
    name: "settings-select-open",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(400);
      await p.getByRole("combobox", { name: "Speed limit" }).click();
      await wait(350);
    },
  },
  {
    name: "settings-search",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(400);
      await p.getByRole("searchbox", { name: "Search settings" }).fill("fold");
      await wait(500);
    },
  },
  {
    name: "settings-search-empty",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(400);
      await p.getByRole("searchbox", { name: "Search settings" }).fill("zzzqq");
      await wait(500);
    },
  },
  {
    name: "settings-egl-readiness-blocked",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "egl");
      await wait(400);
      await btn(p, "Check").click();
      await wait(1200);
    },
  },
  {
    name: "settings-egl-import",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "egl");
      await wait(400);
      await btn(p, "Import games…").click();
      await wait(800);
    },
  },
  {
    name: "settings-egl-sync-off-missing",
    query: "scenario=returning",
    setup: async (p) => {
      await g(p, async (h) => {
        const s = h.app.getState().snapshot!.settings;
        await h.rpc("settings.update", { ...s, eglSync: false });
      });
      await go(p, "settings", "egl");
      await wait(300);
    },
  },
  {
    name: "settings-overlay-missing",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await g(p, (h) => h.rpc("onboarding.complete"));
      await wait(500);
      await go(p, "settings", "overlay");
      await wait(500);
    },
  },
  {
    name: "settings-account-expired",
    query: "scenario=authExpired",
    setup: async (p) => {
      await go(p, "settings", "account");
      await wait(500);
    },
  },

  // --------------------------------------------------------------- dialogs
  {
    name: "dialog-uninstall",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "uninstall", appName: "Fortnite" }));
      await wait(450);
    },
  },
  {
    name: "dialog-uninstall-delete",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "uninstall", appName: "Fortnite" }));
      await wait(450);
      await p.getByRole("radio", { name: /Delete the files/ }).click();
      await wait(300);
    },
  },
  {
    name: "dialog-move",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "move", appName: "Fortnite" }));
      await wait(450);
      await btn(p, "Move to").click();
      await p.getByRole("menuitem").first().click();
      await wait(1000);
    },
  },
  {
    name: "dialog-move-egl-warning",
    query: "scenario=returning",
    setup: async (p) => {
      await g(p, async (h) => {
        const s = h.app.getState().snapshot!.settings;
        await h.rpc("settings.update", { ...s, eglSync: false });
      });
      await wait(400);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "move", appName: "Fortnite" }));
      await wait(450);
      await btn(p, "Move to").click();
      await p.getByRole("menuitem").first().click();
      await wait(1000);
    },
  },
  {
    name: "dialog-cancel-download",
    query: "scenario=downloading&freeze=1&seek=0.42&timescale=1",
    setup: async (p) => {
      await go(p, "downloads");
      await wait(500);
      await btn(p, "Cancel").first().click();
      await wait(450);
    },
  },
  {
    name: "dialog-install",
    query: "scenario=returning",
    setup: async (p) => {
      await goPicked(p, FRESH);
      await wait(500);
      await btn(p, /^Install$/).click();
      await wait(1100);
    },
  },
  {
    name: "dialog-install-insufficient",
    query: "scenario=diskFull",
    setup: async (p) => {
      await goPicked(p, FRESH);
      await wait(500);
      await btn(p, /^Install$/).click();
      await wait(1500);
    },
  },
  {
    name: "dialog-error",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) =>
        h.ui.getState().openDialog({
          kind: "error",
          error: {
            code: "legendaryMissing",
            title: "Glider is damaged",
            message: "A required component is missing. Reinstall Glider to fix it.",
            fix: "openLogs",
            detail: "legendary.exe not found at C:\\Users\\alex\\AppData\\Local\\Programs\\Glider\\bin",
            appName: null,
          },
        }),
      );
      await wait(450);
    },
  },

  // ------------------------------------------------- Library + Home (v0.2 UI-1)
  { name: "home-returning", query: "scenario=returning&timescale=1", setup: async (p) => go(p, "home"), settle: 700 },
  { name: "home-downloading", query: "scenario=downloading&freeze=1&timescale=1", setup: async (p) => go(p, "home"), settle: 700 },
  { name: "home-empty-library", query: "scenario=emptyLibrary", setup: async (p) => go(p, "home") },
  { name: "home-offline", query: "scenario=offline", setup: async (p) => go(p, "home") },
  { name: "lib-grid", query: "scenario=returning", setup: async (p) => go(p, "library") },
  { name: "lib-grid-600", query: "scenario=perf600", setup: async (p) => go(p, "library") },
  {
    name: "lib-list",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.getByRole("radio", { name: "List view" }).click();
      await wait(300);
    },
  },
  { name: "lib-empty", query: "scenario=emptyLibrary", setup: async (p) => go(p, "library") },
  { name: "lib-sync-error", query: "scenario=syncError", setup: async (p) => go(p, "library") },
  {
    name: "lib-no-results",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.getByRole("textbox", { name: "Search your library" }).fill("zzzzqq");
      await wait(300);
    },
  },
  {
    name: "lib-context-menu",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.locator("[data-tile-index='3']").click({ button: "right" });
      await wait(300);
    },
  },

  // ------------------------------------------- game page variants (v0.2 UI review)
  { name: "game-installed", query: "scenario=returning", setup: async (p) => void (await goPicked(p, PLAYABLE)) },
  { name: "game-not-installed", query: "scenario=returning", setup: async (p) => void (await goPicked(p, FRESH)) },
  { name: "game-update", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.installStatus==='installed'&&g.updateAvailable&&g.thirdParty==='none'")) },
  { name: "game-broken", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.installStatus==='broken'")) },
  { name: "game-running", query: "scenario=playing", setup: async (p) => goGame(p) },
  { name: "game-third-party", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.installStatus==='installed'&&g.thirdParty!=='none'")) },
  { name: "game-needs-activation", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.installStatus==='installed'&&g.thirdParty==='ubisoft'")) },
  { name: "game-save-conflict", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.saveStatus==='conflict'")) },
  { name: "game-dlc", query: "scenario=returning", setup: async (p) => void (await goPicked(p, "g.kind==='game'&&g.installStatus==='installed'&&g.dlcCount>0&&g.thirdParty==='none'")) },
  { name: "game-loading", query: "scenario=returning&latency=40", setup: async (p) => goGame(p, "Fortnite"), settle: 120 },
  { name: "game-not-found", query: "scenario=returning", setup: async (p) => goGame(p, "NoSuchGame") },
  {
    name: "dialog-save-conflict",
    query: "scenario=returning",
    setup: async (p) => {
      const app = await goPicked(p, "g.saveStatus==='conflict'");
      await wait(400);
      await p.evaluate((a) => (window as unknown as { __glider: G }).__glider.ui.getState().openDialog({ kind: "saveConflict", appName: a }), app);
      await wait(600);
    },
  },
  {
    name: "dialog-collection-new",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.locator("[data-tile-index='3']").click({ button: "right" });
      await wait(300);
      await p.getByRole("menuitem", { name: /collection/i }).first().click();
      await wait(400);
    },
  },

  // ---------------------------------------------------------- library states
  { name: "lib-loading", query: "scenario=returning&latency=60", setup: async (p) => go(p, "library"), settle: 60 },
  { name: "lib-offline", query: "scenario=offline", setup: async (p) => go(p, "library") },
  {
    name: "lib-filter-menu",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.getByRole("combobox", { name: "Filter" }).click();
      await wait(350);
    },
  },
  {
    name: "lib-filter-updates",
    query: "scenario=updates",
    setup: async (p) => {
      await go(p, "library");
      await p.getByRole("combobox", { name: "Filter" }).click();
      await p.getByRole("option", { name: /Updates available/ }).click();
      await wait(400);
    },
  },
  {
    name: "lib-list-installed",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "library");
      await p.getByRole("radio", { name: "List view" }).click();
      await p.getByRole("combobox", { name: "Filter" }).click();
      await p.getByRole("option", { name: /^Installed/ }).click();
      await wait(400);
    },
  },

  // ----------------------------------------------------------------- store
  { name: "store-opening", query: "scenario=returning&latency=40", setup: async (p) => go(p, "store"), settle: 150 },
  { name: "store-loaded", query: "scenario=returning", setup: async (p) => go(p, "store"), settle: 1200 },
  { name: "store-offline", query: "scenario=offline", setup: async (p) => go(p, "store"), settle: 900 },
  { name: "store-signed-out", query: "scenario=authExpired", setup: async (p) => go(p, "store"), settle: 1200 },
];

const NEEDS_GAME_PAGE = /^(library-|focus-library|focus-rail|toasts|dialog-(uninstall|move|error))/;

/**
 * Screenshot states. NOTE (Phase 1): the setups below were written for the v0.1 single-game screens. Scenario names and
 * RPC shapes are updated and game-page states open Fortnite's page, but states that click v0.1 onboarding/install
 * widgets (onboarding-*, dialog-install*, library-existing-install-hint, library-not-installed) and the art-variant
 * options are stale until the UI phases rebuild those screens and re-shoot them.
 */
export const STATES: ShotState[] = RAW_STATES.map((st) => {
  if (!NEEDS_GAME_PAGE.test(st.name)) return st;
  const setup = st.setup;
  return {
    ...st,
    setup: async (p) => {
      await wait(500);
      await goGame(p);
      await wait(350);
      await setup?.(p);
    },
  };
});
