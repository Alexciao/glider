import { Suspense, lazy, useEffect } from "react";
import { useSnap } from "../state/store";
import { useUi, type Page } from "../state/ui";
import { cx } from "../ui";
import { Library } from "../features/library/Library";
import { Onboarding } from "../features/onboarding/Onboarding";
import { Rail } from "./Rail";
import { TitleBar } from "./TitleBar";

// Secondary pages load on demand: the first paint only needs the Library.
const Downloads = lazy(() => import("../features/downloads/Downloads").then((m) => ({ default: m.Downloads })));
const Settings = lazy(() => import("../features/settings/Settings").then((m) => ({ default: m.Settings })));

function PageHost({ page }: { page: Page }) {
  if (page === "library") return <Library />;
  return (
    <div key={page} className="g-page-in h-full">
      <Suspense fallback={null}>{page === "downloads" ? <Downloads /> : <Settings />}</Suspense>
    </div>
  );
}

export function useGlobalHotkeys(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey) return;
      const ui = useUi.getState();
      const pages: Record<string, Page> = { "1": "library", "2": "downloads", "3": "settings", ",": "settings" };
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

export function Shell() {
  const onboarded = useSnap((s) => s.onboarding.completed);
  const page = useUi((s) => s.page);
  useGlobalHotkeys(onboarded);
  return (
    <div className="flex h-full flex-col">
      <TitleBar minimal={!onboarded} />
      <div className="flex min-h-0 flex-1">
        {onboarded && <Rail />}
        <main
          className={cx("relative min-w-0 flex-1 overflow-hidden border-t bg-bg-elevated", onboarded ? "rounded-tl-xl border-l" : "rounded-t-xl")}
        >
          {onboarded ? <PageHost page={page} /> : <Onboarding />}
        </main>
      </div>
    </div>
  );
}
