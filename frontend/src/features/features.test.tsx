import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AppSnapshot, GameSummary } from "../ipc/contract.gen";
import { rpc, setRpcForTests } from "../ipc/rpc";
import type { MockOptions, ScenarioName } from "../ipc/mock";
import { startBridge } from "../state/bridge";
import { useLibrary } from "../state/library";
import { useNotices } from "../state/notices";
import { resetProgressForTests } from "../state/progress";
import { navigate, useRouter } from "../state/router";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { mockClient } from "../test/fixtures";
import { MotionProvider, TooltipProvider } from "../ui";
import { Shell } from "../app/Shell";
import { Dialogs } from "./dialogs/Dialogs";
import { Downloads } from "./downloads/Downloads";
import { Home } from "./home/Home";
import { LibraryPage } from "./library/LibraryPage";
import { Onboarding } from "./onboarding/Onboarding";

let stop: (() => void) | undefined;

interface Booted {
  snap: AppSnapshot;
  games: ReadonlyMap<string, GameSummary>;
  find: (pred: (g: GameSummary) => boolean) => GameSummary;
}

/** Boot the real stores against a mock scenario, then render what the test asks for. */
async function mount(scenario: ScenarioName, ui: React.ReactElement | ((b: Booted) => React.ReactElement), opts: MockOptions = {}) {
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
      <TooltipProvider>
        {typeof ui === "function" ? ui(booted) : ui}
        <Dialogs />
      </TooltipProvider>
    </MotionProvider>,
  );
  return { user, view, controller, booted };
}

beforeEach(() => {
  useApp.setState({ snapshot: null });
  useLibrary.getState().reset();
  useUi.setState({ dialog: null, errors: {}, pending: {}, readyAt: null, readyApp: null });
  useRouter.setState({ route: { page: "home" }, previous: null });
  useNotices.setState({ items: [] });
  resetProgressForTests();
});
afterEach(() => {
  stop?.();
  setRpcForTests(null);
});

describe("Library page", () => {
  it("lists games, 100 at a time, and opens a game's page", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    expect(screen.getByRole("heading", { name: "Library" })).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(100);
    await user.click(screen.getByRole("button", { name: /Show 100 more/ }));
    expect(screen.getAllByRole("listitem")).toHaveLength(200);
    await user.click(screen.getAllByRole("button", { name: /Fortnite/ })[0]!);
    expect(useRouter.getState().route).toEqual({ page: "game", appName: "Fortnite" });
  });

  it("searches through the in-memory index and says when nothing matches", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    const box = screen.getByRole("textbox", { name: "Search your library" });
    await user.type(box, "fortnite");
    expect(screen.getAllByRole("listitem").some((li) => li.textContent?.includes("Fortnite"))).toBe(true);
    expect(screen.getAllByRole("listitem").length).toBeLessThan(10);
    await user.clear(box);
    await user.type(box, "zzzzqqq");
    expect(screen.getByText("Nothing matches")).toBeInTheDocument();
  });

  it("an empty library is an invitation, a syncing one says so", async () => {
    await mount("emptyLibrary", <LibraryPage />);
    expect(screen.getByText("No games yet")).toBeInTheDocument();
  });

  it("follows library deltas live (a favorite shows its star)", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    await user.type(screen.getByRole("textbox", { name: "Search your library" }), "fortnite");
    expect(screen.getAllByLabelText("Favorite").length).toBeGreaterThan(0);
    act(() => useLibrary.getState().patchGame("Fortnite", { favorite: false }));
    await waitFor(() => expect(screen.queryAllByLabelText("Favorite")).toHaveLength(0));
  });
});

describe("Home", () => {
  it("shows recently played games and a banner for pending updates", async () => {
    const { user } = await mount("returning", <Home />);
    expect(screen.getByRole("heading", { name: /Welcome back, Marlowe/ })).toBeInTheDocument();
    expect(screen.getByText(/waiting\./)).toBeInTheDocument();
    expect(screen.getAllByRole("listitem").length).toBeGreaterThanOrEqual(3);
    await user.click(screen.getAllByRole("listitem")[0]!.querySelector("button")!);
    expect(useRouter.getState().route.page).toBe("game");
  });
});

describe("Downloads", () => {
  it("renders live progress with the planned steps and pauses/resumes through the real actions", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: false });
    expect(await screen.findByRole("heading", { name: /^Installing / })).toBeInTheDocument();
    expect(screen.getByRole("list", { name: "Steps" })).toHaveTextContent("Downloading");
    await user.click(screen.getByRole("button", { name: "Pause" }));
    await waitFor(() => expect(screen.getByText("Paused", { selector: "span" })).toBeInTheDocument());
    await user.click(await screen.findByRole("button", { name: "Resume" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Pause" })).toBeInTheDocument());
  });

  it("shows the queue across games: running, up next, history, and Pause all", async () => {
    const { user } = await mount("queue", <Downloads />, { freeze: true });
    expect(await screen.findByRole("heading", { name: /^Installing / })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Up next" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "History" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Pause all" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Resume all" })).toBeInTheDocument());
  });

  it("a verify reads files: no network figures, damaged files shown", async () => {
    await mount("frozenVerifying", <Downloads />);
    expect(await screen.findByRole("heading", { name: /^Verifying / })).toBeInTheDocument();
    expect(screen.getByText("Disk read")).toBeInTheDocument();
    expect(screen.getByText("Damaged files")).toBeInTheDocument();
    expect(screen.queryByText("Download speed")).not.toBeInTheDocument();
    expect(screen.getByText(/of [\d,]+ files/)).toBeInTheDocument();
  });

  it("a repair step shows network and disk for just the bad files", async () => {
    await mount("frozenRepairing", <Downloads />);
    expect(await screen.findByText("Download speed")).toBeInTheDocument();
    expect(screen.getByText("Disk write")).toBeInTheDocument();
  });

  it("confirms before cancelling, then returns to an empty state", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await user.click((await screen.findAllByRole("button", { name: "Cancel" }))[0]!);
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent(/Cancel install of /);
    await user.click(within(dialog).getByRole("button", { name: /Cancel install of / }));
    await waitFor(() => expect(screen.queryByRole("progressbar", { name: /^Installing / })).not.toBeInTheDocument());
  });

  it("is an invitation, not a void, when there is nothing to show", async () => {
    await mount("emptyLibrary", <Downloads />);
    expect(screen.getByText("Nothing downloading")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Browse your library" })).toBeInTheDocument();
  });

  it("offers to update everything when updates are waiting", async () => {
    await mount("updates", <Downloads />);
    await act(async () => {
      await rpc().call("queue.clearFinished");
    });
    expect(await screen.findByText("Nothing downloading")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Update \d+ games$/ })).toBeInTheDocument();
  });
});

describe("Shell routing", () => {
  it("renders the page for the route, including a game, and follows navigate()", async () => {
    await mount("returning", <Shell />);
    expect(screen.getByRole("heading", { name: /Welcome back/ })).toBeInTheDocument();
    act(() => navigate("library"));
    expect(await screen.findByRole("heading", { name: "Library" })).toBeInTheDocument();
    act(() => navigate("game/Fortnite"));
    expect(await screen.findByRole("heading", { name: "Fortnite" })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Library" })).toHaveLength(2); // the rail item and the back link
    act(() => navigate("downloads"));
    expect(await screen.findByRole("heading", { name: "Downloads" })).toBeInTheDocument();
    act(() => navigate("store"));
    expect(await screen.findByRole("heading", { name: "Store" })).toBeInTheDocument();
    act(() => navigate("settings"));
    expect(await screen.findByRole("heading", { name: "Settings" })).toBeInTheDocument();
  });

  it("shows onboarding instead of the app until it is completed", async () => {
    await mount("firstRun", <Shell />);
    expect(screen.queryByRole("navigation", { name: "Main" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /Your whole Epic library/ })).toBeInTheDocument();
  });
});

describe("Onboarding", () => {
  it("starts at Welcome on a fresh machine, persists the step, then asks for sign-in", async () => {
    const { user } = await mount("firstRun", <Onboarding />);
    expect(screen.getByRole("heading", { name: /Your whole Epic library, one launcher/ })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Get started" }));
    expect(await screen.findByRole("button", { name: /Sign in with Epic/ })).toBeInTheDocument();
    expect(useApp.getState().snapshot!.onboarding.step).toBe("signIn");
  });

  it("a closed sign-in window shows a calm message with Copy diagnostic logs", async () => {
    const { user, controller } = await mount("firstRun", <Onboarding />);
    await user.click(screen.getByRole("button", { name: "Get started" }));
    const signIn = await screen.findByRole("button", { name: "Sign in with Epic" });
    controller.failNext("authCancelled");
    await user.click(signIn);
    expect(await screen.findByText("Sign-in was cancelled")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Copy diagnostic logs" })).toBeInTheDocument();
  });

  it("scan: lists every kind of install, selectable ones first selected, others disabled with the backend's reason", async () => {
    await mount("firstRunSignedIn", <Onboarding />);
    expect(await screen.findByText("We found games on this PC")).toBeInTheDocument();
    const boxes = screen.getAllByRole("checkbox");
    expect(boxes.length).toBeGreaterThan(8);
    expect(boxes.filter((b) => (b as HTMLInputElement).disabled).length).toBeGreaterThanOrEqual(4);
    expect(boxes.filter((b) => (b as HTMLInputElement).checked).length).toBeGreaterThanOrEqual(5);
    expect(screen.getAllByText("Folder missing").length).toBeGreaterThan(0);
    expect(screen.getByText(/Epic Games Launcher is installing or updating this right now/)).toBeInTheDocument();
    expect(screen.getByText(/isn't in the signed-in Epic account's library/)).toBeInTheDocument();
  });

  it("walks Scan → Folders → Mode → Done and completes onboarding", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />);
    await screen.findByText("We found games on this PC");
    await user.click(screen.getByRole("button", { name: "Select none" }));
    await user.click(screen.getAllByRole("checkbox").find((b) => !(b as HTMLInputElement).disabled)!);
    await user.click(screen.getByRole("button", { name: "Import 1 game" }));
    await waitFor(() => expect(screen.getByText("Imported")).toBeInTheDocument(), { timeout: 8000 });
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("Where should games go?")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled(); // no library folder yet
    await user.click(screen.getByRole("button", { name: "Add a folder…" }));
    expect(await screen.findByText("Default")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("How should downloads behave?")).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: "Full speed" }));
    await waitFor(() => expect(useApp.getState().snapshot!.settings.downloadMode).toBe("full"));
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("You're all set")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Open Glider" }));
    await waitFor(() => expect(useApp.getState().snapshot!.onboarding.completed).toBe(true));
    expect(useRouter.getState().route).toEqual({ page: "home" });
    expect(useApp.getState().snapshot!.onboarding.step).toBe("done");
  });

  it("an empty scan still lets the user continue", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />, { games: 0 });
    expect(await screen.findByText(/no installs to import/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Continue" }));
    expect(await screen.findByText("Where should games go?")).toBeInTheDocument();
  });

  it("locate another folder: an invalid one explains why", async () => {
    const { user } = await mount("firstRunSignedIn", <Onboarding />);
    await screen.findByText("We found games on this PC");
    await user.click(screen.getByRole("button", { name: /Locate another folder/ })); // first pick = a valid game folder
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "import" && j.status !== "completed")).toBe(true), { timeout: 4000 });
    await user.click(screen.getByRole("button", { name: /Locate another folder/ })); // second pick = not a game
    expect(await screen.findByText("Not a game folder")).toBeInTheDocument();
  });
});
