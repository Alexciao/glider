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
import { GameView } from "./library/GameView";
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

const plain = (g: GameSummary) => g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none";

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

describe("Game page", () => {
  it("shows 'Ready to play' and launches through Launching to Playing", async () => {
    const { user, controller } = await mount("returning", (b) => {
      const g = b.find((x) => plain(x) && !x.updateAvailable && x.canRunOffline && x.saveStatus !== "conflict" && x.saveStatus !== "localNewer" && x.saveStatus !== "cloudNewer" && x.appName !== "Fortnite");
      return <GameView appName={g.appName} />;
    });
    expect(screen.getByText("Ready to play")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    await waitFor(() => expect(screen.getByRole("button", { name: /Launching|Preparing/ })).toBeDisabled(), { timeout: 3000 });
    await waitFor(() => expect(screen.getByRole("button", { name: /Playing/ })).toBeInTheDocument(), { timeout: 6000 });
    expect(screen.getByText(/is running$/)).toBeInTheDocument();
    controller.dispose?.();
  });

  it("names the game: Fortnite is the user's v0.1 game, with its update and size", async () => {
    await mount("returning", <GameView appName="Fortnite" />);
    expect(screen.getByRole("heading", { name: "Fortnite" })).toBeInTheDocument();
    expect(screen.getByText("Update available")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Update$/ })).toBeEnabled();
  });

  it("shows playtime, last played and installed size in the facts row", async () => {
    await mount("returning", <GameView appName="Fortnite" />);
    expect(screen.getByText("Playtime")).toBeInTheDocument();
    expect(screen.getByText("842 h 17 min")).toBeInTheDocument();
    expect(screen.getByText("Last played")).toBeInTheDocument();
    expect(screen.getByText("Installed size")).toBeInTheDocument();
    expect(screen.getByText("Developer")).toBeInTheDocument();
  });

  it("a game that isn't in the library says so, with a way back", async () => {
    const { user } = await mount("returning", <GameView appName="nope-nope" />);
    expect(screen.getByText("That game isn't in your library")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Back to library" }));
    expect(useRouter.getState().route).toEqual({ page: "library" });
  });

  it("explains waiting on Epic Games Launcher, for that game only", async () => {
    await mount(
      "eglBusy",
      (b) => <GameView appName={b.snap.queue.jobs.find((j) => j.status === "waiting")!.appName} />,
      { freeze: true },
    );
    expect(screen.getByText(/Epic Games Launcher is updating .*, so Glider will wait/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Waiting/ })).toBeInTheDocument();
  });

  it("shows a failed job's error with its one-click fix, on the right game", async () => {
    await mount("errors", (b) => {
      const j = b.snap.queue.jobs.find((x) => x.status === "failed" && x.error?.code === "insufficientSpace")!;
      return <GameView appName={j.appName} />;
    });
    expect(screen.getByRole("alert")).toHaveTextContent("Not enough disk space");
    expect(within(screen.getByRole("alert")).getByRole("button", { name: "Free up space" })).toBeInTheDocument();
  });

  it("surfaces a launch failure inline with Repair", async () => {
    let title = "";
    const { user, controller } = await mount("returning", (b) => {
      const g = b.find((x) => plain(x) && !x.updateAvailable && x.saveStatus === "inSync" && x.canRunOffline);
      title = g.title;
      return <GameView appName={g.appName} />;
    });
    controller.failNext("launchFailed");
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(`Couldn't start ${title}`);
    expect(within(alert).getByRole("button", { name: "Repair" })).toBeInTheDocument();
  });

  it("a save conflict is resolved inline: keeping one side clears it", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.saveStatus === "conflict").appName} />);
    expect(screen.getByText("Your cloud and local saves both changed")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Keep cloud" }));
    await waitFor(() => expect(screen.queryByText("Your cloud and local saves both changed")).not.toBeInTheDocument());
  });

  it("a conflicting Play is explained with a toast, not a silent failure", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.saveStatus === "conflict").appName} />);
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    await waitFor(() => expect(useNotices.getState().items.some((t) => t.title === "Your saves differ" && t.action === "resolveSaves")).toBe(true));
  });

  it("a third-party game that needs linking asks to activate", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.thirdParty === "ubisoft" && g.installStatus === "installed").appName} />);
    await user.click(screen.getByRole("button", { name: /^Play$/ }));
    await waitFor(() => expect(useNotices.getState().items.some((t) => t.action === "activate")).toBe(true));
  });

  it("favorites toggle optimistically and the backend's delta agrees", async () => {
    const { user } = await mount("returning", <GameView appName="Fortnite" />);
    await user.click(screen.getByRole("button", { name: "More actions" }));
    await user.click(await screen.findByRole("menuitem", { name: "Remove from favorites" }));
    await waitFor(() => expect(useLibrary.getState().games.get("Fortnite")!.favorite).toBe(false));
  });
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

describe("Install dialog", () => {
  it("plans against the chosen drive and blocks the install when it doesn't fit", async () => {
    const { user } = await mount("diskFull", (b) => {
      const g = b.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
      act(() => useUi.getState().openDialog({ kind: "install", appName: g.appName }));
      return <div />;
    });
    expect(await screen.findByText(/^Install /, { selector: "h2" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText(/is free\. Free up space or choose another drive\./)).toBeInTheDocument());
    expect(screen.getByRole("button", { name: "Install" })).toBeDisabled();
    void user;
  });

  it("starts the install into the default folder and goes to Downloads", async () => {
    const { user } = await mount("returning", (b) => {
      const g = b.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none" && x.dlcCount === 0);
      act(() => useUi.getState().openDialog({ kind: "install", appName: g.appName }));
      return <div />;
    });
    const install = await screen.findByRole("button", { name: "Install" });
    await waitFor(() => expect(install).toBeEnabled());
    await user.click(install);
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "install" && j.status !== "completed")).toBe(true));
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
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
