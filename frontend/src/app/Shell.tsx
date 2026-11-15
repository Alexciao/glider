import { Suspense, lazy, useEffect } from "react";
import { useRoute, type Route } from "../state/router";
import { useSnap } from "../state/store";
import { useUi } from "../state/ui";
import { cx } from "../ui";
import { GameView } from "../features/library/GameView";
import { Home } from "../features/home/Home";
import { LibraryPage } from "../features/library/LibraryPage";
import { Onboarding } from "../features/onboarding/Onboarding";
import { Rail } from "./Rail";
import { TitleBar } from "./TitleBar";

// Secondary pages load on demand: the first paint only needs Home / Library / a game.
const Downloads = lazy(() => import("../features/downloads/Downloads").then((m) => ({ default: m.Downloads })));
const Settings = lazy(() => import("../features/settings/Settings").then((m) => ({ default: m.Settings })));
const StorePage = lazy(() => import("../features/store/StorePage").then((m) => ({ default: m.StorePage })));

function PageHost({ route }: { route: Route }) {
  switch (route.page) {
    case "game":
      // Keyed by game so state from one page (scroll, dialogs in flight) never leaks into the next.
      return <GameView key={route.appName} appName={route.appName} />;
    case "home":
      return <Home />;
    case "library":
      return <LibraryPage />;
    case "downloads":
    case "settings":
    case "store":
      return (
        <div key={route.page} className="g-page-in h-full">
          <Suspense fallback={null}>{route.page === "downloads" ? <Downloads /> : route.page === "store" ? <StorePage /> : <Settings />}</Suspense>
        </div>
      );
  }
}

export function useGlobalHotkeys(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
      const ui = useUi.getState();
      const pages: Record<string, "home" | "library" | "downloads" | "store" | "settings"> = {
        "1": "home",
        "2": "library",
        "3": "store",
        "4": "downloads",
        "5": "settings",
        ",": "settings",
      };
      if (e.key.toLowerCase() === "k") {
        e.preventDefault();
        ui.setPalette(!ui.palette);
      } else if (pages[e.key]) {
        e.preventDefault();
        ui.go(pages[e.key]!);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [enabled]);
}

/** Warm the three lazy pages once the first paint is done and the main thread is idle, so their first visit never shows a blank area. */
function usePrefetchPages(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    const load = () => {
      void import("../features/downloads/Downloads");
      void import("../features/settings/Settings");
      void import("../features/store/StorePage");
    };
    const idle = (window as { requestIdleCallback?: (cb: () => void, o?: { timeout: number }) => number }).requestIdleCallback;
    if (idle) {
      const id = idle(load, { timeout: 3000 });
      return () => (window as { cancelIdleCallback?: (id: number) => void }).cancelIdleCallback?.(id);
    }
    const t = window.setTimeout(load, 1200);
    return () => window.clearTimeout(t);
  }, [enabled]);
}

export function Shell() {
  const completed = useSnap((s) => s.onboarding.completed);
  const hold = useUi((s) => s.onboardingHold);
  const route = useRoute();
  // Setup stays up until its own last button is pressed, even though Core already counts it as completed at the last step.
  const onboarded = completed && !hold;
  useEffect(() => {
    if (!completed && !hold) useUi.setState({ onboardingHold: true });
  }, [completed, hold]);
  useGlobalHotkeys(onboarded);
  usePrefetchPages(onboarded);
  return (
    <div className="flex h-full flex-col">
      <TitleBar minimal={!onboarded} />
      <div className="flex min-h-0 flex-1">
        {onboarded && <Rail />}
        <main
          className={cx("relative min-w-0 flex-1 overflow-hidden border-t bg-bg-elevated", onboarded ? "rounded-tl-xl border-l" : "rounded-t-xl")}
        >
          {onboarded ? <PageHost route={route} /> : <Onboarding />}
        </main>
      </div>
    </div>
  );
}
