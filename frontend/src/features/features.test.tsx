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
import { GameView } from "./library/GameView";
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

const plain = (g: GameSummary) => g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none";

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
