import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AppSnapshot, GameSummary } from "../ipc/contract.gen";
import { rpc, setRpcForTests } from "../ipc/rpc";
import type { MockOptions, ScenarioName } from "../ipc/mock";
import { startBridge } from "../state/bridge";
import { useLibrary } from "../state/library";
import { useNotices } from "../state/notices";
import { resetFreeGamesForTests } from "../state/freeGames";
import { resetLibraryPrefsForTests } from "../state/libraryPrefs";
import { resetProgressForTests } from "../state/progress";
import { useStoreTarget } from "../state/storeTarget";
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
  resetFreeGamesForTests();
  resetLibraryPrefsForTests();
  useStoreTarget.setState({ target: null });
  localStorage.clear();
});
afterEach(() => {
  stop?.();
  setRpcForTests(null);
});

describe("Library page", () => {
  it("renders a window of the grid (not all 300 games) and opens a game's page", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    expect(screen.getByRole("heading", { name: "Library" })).toBeInTheDocument();
    const tiles = screen.getAllByRole("listitem");
    expect(tiles.length).toBeGreaterThan(6);
    expect(tiles.length).toBeLessThan(80);
    await user.type(screen.getByRole("textbox", { name: "Search your library" }), "fortnite");
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
    expect(screen.getByText(`No games match "zzzzqqq"`)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Clear search and filters" }));
    expect(screen.getAllByRole("listitem").length).toBeGreaterThan(6);
  });

  it("an empty library is a quiet invitation with no toolbar", async () => {
    await mount("emptyLibrary", <LibraryPage />);
    expect(screen.getByText("Your library is empty")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Sync library" })).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: "Search your library" })).not.toBeInTheDocument();
  });

  it("tiles carry only the title and one badge; the list view carries the favorite star and follows deltas", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    await user.type(screen.getByRole("textbox", { name: "Search your library" }), "fortnite");
    expect(screen.queryAllByLabelText("Favorite")).toHaveLength(0);
    await user.click(screen.getByRole("radio", { name: "List view" }));
    expect(screen.getAllByLabelText("Favorite").length).toBeGreaterThan(0);
    act(() => useLibrary.getState().patchGame("Fortnite", { favorite: false }));
    await waitFor(() => expect(screen.queryAllByLabelText("Favorite")).toHaveLength(0));
  });

  it("remembers the view per account and restores it", async () => {
    const first = await mount("returning", <LibraryPage />);
    await first.user.click(screen.getByRole("radio", { name: "List view" }));
    expect(localStorage.getItem(`glider.library.prefs.v1.${first.booted.snap.auth.account?.accountId}`)).toContain("list");
    first.view.unmount();
    resetLibraryPrefsForTests();
    render(
      <MotionProvider>
        <TooltipProvider>
          <LibraryPage />
        </TooltipProvider>
      </MotionProvider>,
    );
    expect(screen.getByRole("radio", { name: "List view" })).toBeChecked();
  });

  it("the context menu favorites a game", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    await user.type(screen.getByRole("textbox", { name: "Search your library" }), "fortnite");
    const tile = screen.getAllByRole("button", { name: /Fortnite/ })[0]!;
    fireEvent.contextMenu(tile);
    const item = await screen.findByRole("menuitem", { name: /favorites/ });
    const before = useLibrary.getState().games.get("Fortnite")!.favorite;
    await user.click(item);
    await waitFor(() => expect(useLibrary.getState().games.get("Fortnite")!.favorite).toBe(!before));
  });

  it("arrow keys move through the tiles with a single tab stop", async () => {
    const { user } = await mount("returning", <LibraryPage />);
    const tabStops = () => document.querySelectorAll<HTMLElement>("[data-tile-index][tabindex='0']");
    expect(tabStops()).toHaveLength(1);
    tabStops()[0]!.focus();
    await user.keyboard("{ArrowRight}");
    await waitFor(() => expect(document.activeElement?.getAttribute("data-tile-index")).toBe("1"));
    expect(tabStops()).toHaveLength(1);
    await user.keyboard("{ArrowDown}");
    await waitFor(() => expect(Number(document.activeElement?.getAttribute("data-tile-index"))).toBeGreaterThan(1));
  });

  it("a sync error is one quiet line in the header and the cached library stays", async () => {
    await mount("syncError", <LibraryPage />);
    expect(screen.getByRole("button", { name: /Couldn.t sync/ })).toBeInTheDocument();
    expect(screen.getAllByRole("listitem").length).toBeGreaterThan(6);
  });
});

describe("Home", () => {
  it("shows continue playing and updates, and nothing else that is empty", async () => {
    const { user } = await mount("returning", <Home />);
    expect(screen.getByRole("heading", { name: /Welcome back, Marlowe/ })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Continue playing" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Updates" })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Downloads" })).not.toBeInTheDocument();
    await user.click(within(screen.getByRole("region", { name: "Continue playing" })).getAllByRole("button")[0]!);
    expect(useRouter.getState().route.page).toBe("game");
  });

  it("a brand-new library gets the one empty state", async () => {
    await mount("emptyLibrary", <Home />);
    expect(screen.getByText("Your library is empty")).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Continue playing" })).not.toBeInTheDocument();
  });

  it("downloads in progress are compact rows that open the Downloads page", async () => {
    const { user } = await mount("downloading", <Home />);
    const section = screen.getByRole("region", { name: "Downloads" });
    await user.click(within(section).getAllByRole("button")[0]!);
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
  });

  it("free games deep-link to their Store page, and owned ones say so", async () => {
    const { user } = await mount("returning", <Home />);
    const section = await screen.findByRole("region", { name: "Free this week" });
    await waitFor(() => expect(within(section).getAllByRole("button").length).toBeGreaterThan(1));
    expect(within(section).getByText("In your library")).toBeInTheDocument();
    await user.click(within(section).getByRole("button", { name: /Lanternfall/ }));
    expect(useRouter.getState().route).toEqual({ page: "store" });
    expect(useStoreTarget.getState().target).toEqual({ kind: "product", slug: "lanternfall" });
  });

  it("offline, the free games section is simply not drawn", async () => {
    await mount("offline", <Home />);
    expect(screen.queryByRole("region", { name: "Free this week" })).not.toBeInTheDocument();
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
