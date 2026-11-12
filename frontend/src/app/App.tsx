import { Suspense, lazy } from "react";
import { mockController } from "../ipc/rpc";
import { Dialogs } from "../features/dialogs/Dialogs";
import { MotionProvider, TooltipProvider } from "../ui";
import { useBoot, useRevealWhenPainted, useThemeAndBackdrop } from "./boot";
import { CommandPalette } from "./CommandPalette";
import { Notices } from "./Notices";
import { Shell } from "./Shell";
import { BootFailed, Splash } from "./Splash";
import { useTestHooks } from "./testHooks";

// Dev-only scenario switcher: loaded only when the mock backend is active.
const DevSwitcher = lazy(() => import("../dev/DevSwitcher").then((m) => ({ default: m.DevSwitcher })));

export function App() {
  const { state, retry } = useBoot();
  useThemeAndBackdrop();
  useRevealWhenPainted(state !== "connecting");
  const ready = state === "ready";
  useTestHooks(ready);

  return (
    <MotionProvider>
      <TooltipProvider>
        {ready ? <Shell /> : state === "failed" ? <BootFailed onRetry={retry} /> : <Splash />}
        {ready && (
          <>
            <Dialogs />
            <CommandPalette />
          </>
        )}
        <Notices />
        {ready && mockController() && new URLSearchParams(location.search).get("dev") !== "0" && (
          <Suspense fallback={null}>
            <DevSwitcher />
          </Suspense>
        )}
      </TooltipProvider>
    </MotionProvider>
  );
}
