import { afterEach, beforeEach } from "vitest";
import { render } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactElement } from "react";
import type { AppSnapshot, GameSummary } from "../ipc/contract.gen";
import { setRpcForTests } from "../ipc/rpc";
import type { MockOptions, ScenarioName } from "../ipc/mock";
import { useImportBatch } from "../features/import/batch";
import { startBridge } from "../state/bridge";
import { useLibrary } from "../state/library";
import { useNotices } from "../state/notices";
import { resetProgressForTests } from "../state/progress";
import { useRouter } from "../state/router";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { MotionProvider, TooltipProvider } from "../ui";
import { mockClient } from "./fixtures";

let stop: (() => void) | undefined;

export interface Booted {
  snap: AppSnapshot;
  games: ReadonlyMap<string, GameSummary>;
  find: (pred: (g: GameSummary) => boolean) => GameSummary;
}

/** Boot the real stores against a mock scenario (latency 0 unless told otherwise), then render what the test asks for. */
export async function mount(scenario: ScenarioName, ui: ReactElement | ((b: Booted) => ReactElement), opts: MockOptions = {}) {
  const { client, controller } = mockClient(scenario, opts);
  setRpcForTests(client, controller);
  stop = await startBridge();
  const booted: Booted = {
    snap: useApp.getState().snapshot!,
    games: useLibrary.getState().games,
    find: (pred) => {
      const g = [...useLibrary.getState().games.values()].find(pred);
      if (!g) throw new Error("no matching game");
      return g;
    },
  };
  const user = userEvent.setup();
  const view = render(
    <MotionProvider>
      <TooltipProvider>{typeof ui === "function" ? ui(booted) : ui}</TooltipProvider>
    </MotionProvider>,
  );
  return { user, view, controller, booted };
}

/** Reset every store between tests. Call once at the top of a test file. */
export function installCleanStores(): void {
  beforeEach(() => {
    useApp.setState({ snapshot: null });
    useLibrary.getState().reset();
    useUi.setState({ dialog: null, errors: {}, pending: {}, readyAt: null, readyApp: null, palette: false, settingsSection: "downloads" });
    useRouter.setState({ route: { page: "home" }, previous: null });
    useNotices.setState({ items: [] });
    useImportBatch.getState().clear();
    resetProgressForTests();
    try {
      localStorage.clear();
    } catch {
      /* jsdom without storage */
    }
  });
  afterEach(() => {
    stop?.();
    setRpcForTests(null);
  });
}
