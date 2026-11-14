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

/** The paragraph whose whole text matches (the headline is split across styled spans). */
const para = (re: RegExp) => screen.queryAllByText((_, el) => el?.tagName === "P" && re.test(el.textContent ?? ""))[0];

describe("Downloads", () => {
  it("renders live progress with the planned steps and pauses/resumes through the real actions", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: false });
    expect(await screen.findByRole("progressbar", { name: /^(Preparing|Downloading) / })).toBeInTheDocument();
    expect(screen.getByRole("list", { name: "Steps" })).toHaveTextContent("Downloading");
    await user.click(screen.getByRole("button", { name: "Pause" }));
    await waitFor(() => expect(screen.getByText("Paused", { selector: "span" })).toBeInTheDocument());
    await user.click(await screen.findByRole("button", { name: "Resume" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Pause" })).toBeInTheDocument());
  });

  it("says each fact once: one title, a big percent, one headline with time left, grouped figures", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const bar = await screen.findByRole("progressbar", { name: /^Downloading / });
    expect(bar).toBeInTheDocument();
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toContain(bar.getAttribute("aria-label")!.replace(/^Downloading /, ""));
    expect(para(/ of .* (GB|MB) · .* left$/)).toBeTruthy();
    expect(Number(bar.getAttribute("aria-valuenow"))).toBeGreaterThan(0);
    for (const g of ["Progress", "Speed", "Time", "Disk"]) expect(screen.getByRole("region", { name: g })).toBeInTheDocument();
    // The mode lives in the header, not in the figures; nothing reads "—".
    expect(screen.queryByText("Mode", { selector: "dt" })).not.toBeInTheDocument();
    expect(screen.queryByText("—")).not.toBeInTheDocument();
  });

  it("shows the queue across games: running, up next, history, and Pause all", async () => {
    const { user } = await mount("queue", <Downloads />, { freeze: true });
    expect(await screen.findByRole("progressbar", { name: /^Downloading / })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Up next" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "History" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Pause all" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Resume all" })).toBeInTheDocument());
  });

  it("collapses history to the latest few", async () => {
    const { user } = await mount("returning", <Downloads />, { freeze: true });
    const list = await screen.findByRole("heading", { name: "History" });
    const section = list.closest("section")!;
    expect(within(section).getAllByRole("listitem")).toHaveLength(3);
    await user.click(within(section).getByRole("button", { name: /^Show \d+ earlier$/ }));
    expect(within(section).getAllByRole("listitem").length).toBeGreaterThan(3);
  });

  it("switches the download mode from the page header through settings.update", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await user.click(await screen.findByRole("radio", { name: "Full" }));
    await waitFor(() => expect(useApp.getState().snapshot!.settings.downloadMode).toBe("full"));
  });

  it("reorders the queue from the keyboard and tells the backend", async () => {
    const { user } = await mount("queue", <Downloads />, { freeze: true });
    const handles = await screen.findAllByRole("button", { name: /^Reorder / });
    const first = handles[0]!;
    const name = /^Reorder (.+), position/.exec(first.getAttribute("aria-label")!)![1]!;
    first.focus();
    await user.keyboard(" ");
    await user.keyboard("{ArrowDown}");
    await user.keyboard(" ");
    await waitFor(() => {
      const names = screen.getAllByRole("button", { name: /^Reorder / }).map((b) => /^Reorder (.+), position/.exec(b.getAttribute("aria-label")!)![1]);
      expect(names.indexOf(name)).toBe(1);
    });
  });

  it("a verify reads files: no network figures, damaged files shown", async () => {
    await mount("frozenVerifying", <Downloads />);
    expect(await screen.findByRole("progressbar", { name: /^Verifying / })).toBeInTheDocument();
    expect(screen.getByText("Read speed")).toBeInTheDocument();
    expect(screen.getByText("Files checked")).toBeInTheDocument();
    expect(screen.queryByText("Server")).not.toBeInTheDocument();
    expect(screen.queryByText("Write speed")).not.toBeInTheDocument();
    expect(para(/GB of .* GB checked/)).toBeTruthy();
    expect(screen.queryByRole("group", { name: /speed over time/ })).not.toBeInTheDocument();
  });

  it("a repair step shows network and disk for just the bad files, with the speed graph", async () => {
    await mount("frozenRepairing", <Downloads />);
    expect(await screen.findByText("Server")).toBeInTheDocument();
    expect(screen.getByText("Write speed")).toBeInTheDocument();
    expect(screen.getByText(/files? to fix/)).toBeInTheDocument();
    expect(screen.getByRole("group", { name: /Network and disk speed over time/ })).toBeInTheDocument();
  });

  it("confirms before cancelling, then returns to an empty state", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await user.click((await screen.findAllByRole("button", { name: "Cancel" }))[0]!);
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent(/Cancel install of /);
    await user.click(within(dialog).getByRole("button", { name: /Cancel install of / }));
    await waitFor(() => expect(screen.queryByRole("progressbar", { name: /^(Preparing|Downloading) / })).not.toBeInTheDocument());
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
