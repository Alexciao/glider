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
  mock: { failNext(code: string): void; failActiveJob(code: string): void; emitNotice(n: object): void };
}
const g = <T>(page: Page, fn: (g: G) => T | Promise<T>): Promise<T> =>
  page.evaluate(`(${fn.toString()})(window.__glider)`) as Promise<T>;

const btn = (page: Page, name: string | RegExp) => page.getByRole("button", { name });
const go = (page: Page, p: string, section?: string) =>
  page.evaluate(([pg, sec]) => (window as unknown as { __glider: G }).__glider.ui.getState().go(pg!, sec), [p, section] as const);
/** v2 has one page per game: the v0.1 "library" screens are Fortnite's game page. */
const goGame = (page: Page, appName = "Fortnite") =>
  page.evaluate((a) => (window as unknown as { __glider: G }).__glider.router.getState().go(`game/${a}`), appName);
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
      await g(p, (h) => h.mock.failNext("authFailed"));
      await btn(p, "Get started").click();
      await wait(700);
      await btn(p, /Sign in with Epic/).click();
      await wait(1900);
    },
  },
  { name: "onboarding-3-fresh-install", query: "scenario=firstRunSignedIn", settle: 900 },
  {
    name: "onboarding-3-fresh-insufficient",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(800);
      await btn(p, "Change…").click();
      await wait(900);
      await btn(p, "Change…").click();
      await wait(1600);
    },
  },
  { name: "onboarding-4-found-one", query: "scenario=firstRunSignedIn", settle: 700 },
  { name: "onboarding-4-found-multiple", query: "scenario=firstRunSignedIn", settle: 700 },
  {
    name: "onboarding-4-found-located",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(600);
      await btn(p, /Locate another folder/).click();
      await wait(1300);
    },
  },
  {
    name: "onboarding-4-locate-invalid",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(600);
      await btn(p, /Locate another folder/).click();
      await wait(1300);
      await btn(p, /Locate another folder/).click();
      await wait(1500);
    },
  },
  {
    name: "onboarding-4-linking",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(600);
      await btn(p, "Use this install").click();
      await wait(500);
    },
  },
  {
    name: "onboarding-5-done",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await wait(600);
      await btn(p, "Use this install").click();
      await wait(2800);
    },
  },
  {
    name: "onboarding-5-done-installing",
    query: "scenario=firstRunSignedIn&freeze=1",
    setup: async (p) => {
      await wait(900);
      await btn(p, "Install Fortnite").click();
      await wait(2200);
    },
  },
  { name: "onboarding-eglsignedout-welcome", query: "scenario=firstRun" },

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
      await btn(p, "Pause").click();
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
      await wait(500);
      await btn(p, "Play").click();
      await wait(1100);
    },
  },
  {
    name: "library-error-launch-failed",
    query: "scenario=returning",
    setup: async (p) => {
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
      await go(p, "settings", "general");
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
      await btn(p, "Pause").click();
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

  // -------------------------------------------------------------- settings
  ...(["general", "downloads", "game", "egl", "advanced", "about"] as const).map<ShotState>((s) => ({
    name: `settings-${s}`,
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", s);
    },
    settle: s === "advanced" || s === "about" ? 900 : 400,
  })),
  {
    name: "settings-downloads-custom",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(300);
      await p.getByRole("radio", { name: "Custom" }).first().click();
      await wait(400);
    },
  },
  {
    name: "settings-select-open",
    query: "scenario=returning",
    setup: async (p) => {
      await go(p, "settings", "downloads");
      await wait(300);
      await p.getByRole("combobox", { name: "Preferred CDN" }).click();
      await wait(350);
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

  // --------------------------------------------------------------- dialogs
  {
    name: "dialog-uninstall",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "uninstall" }));
      await wait(450);
    },
  },
  {
    name: "dialog-uninstall-delete",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "uninstall" }));
      await wait(450);
      await p.getByRole("radio", { name: /Uninstall and delete files/ }).click();
      await wait(300);
    },
  },
  {
    name: "dialog-move",
    query: "scenario=returning",
    setup: async (p) => {
      await wait(500);
      await g(p, (h) => h.ui.getState().openDialog({ kind: "move" }));
      await wait(450);
      await btn(p, "Choose…").click();
      await wait(1300);
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
      await g(p, (h) => h.ui.getState().openDialog({ kind: "move" }));
      await wait(450);
      await btn(p, "Choose…").click();
      await wait(1300);
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
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await finishOnboarding(p);
      await wait(500);
      await btn(p, "Install").first().click();
      await wait(1100);
    },
  },
  {
    name: "dialog-install-insufficient",
    query: "scenario=firstRunSignedIn",
    setup: async (p) => {
      await finishOnboarding(p);
      await wait(500);
      await btn(p, "Install").first().click();
      await wait(900);
      await btn(p, "Change…").click();
      await wait(900);
      await btn(p, "Change…").click();
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
