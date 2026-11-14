import { useEffect } from "react";
import { mockController, rpc } from "../ipc/rpc";
import { useLibrary } from "../state/library";
import { useRouter } from "../state/router";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";

/**
 * Mock-mode only: a tiny handle for Playwright (screenshots/e2e) to put the app into states that
 * would otherwise need long click paths. Never present in the hosted app.
 */
export interface GliderTestHandle {
  ui: typeof useUi;
  app: typeof useApp;
  library: typeof useLibrary;
  router: typeof useRouter;
  rpc: ReturnType<typeof rpc>["call"];
  mock: NonNullable<ReturnType<typeof mockController>>;
}

declare global {
  interface Window {
    __glider?: GliderTestHandle;
  }
}

export function useTestHooks(ready: boolean): void {
  useEffect(() => {
    if (!ready) return;
    const mock = mockController();
    if (!mock) return;
    window.__glider = { ui: useUi, app: useApp, library: useLibrary, router: useRouter, rpc: (...a: Parameters<ReturnType<typeof rpc>["call"]>) => rpc().call(...a), mock } as GliderTestHandle;
    // Fonts + first paint are done: tests wait on this instead of sleeping.
    void document.fonts.ready.then(() => {
      document.documentElement.dataset.ready = "1";
    });
  }, [ready]);
}
