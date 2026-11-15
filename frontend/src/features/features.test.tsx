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
import { navigate, useRouter } from "../state/router";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { mockClient } from "../test/fixtures";
import { MotionProvider, TooltipProvider } from "../ui";
import { Shell } from "../app/Shell";
import { Dialogs } from "./dialogs/Dialogs";
import { ActiveJobCard } from "./downloads/ActiveJobCard";
import { Downloads } from "./downloads/Downloads";
import { QueueRow } from "./downloads/QueueRow";
import { railDotTone } from "../app/Rail";
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
  useUi.setState({ dialog: null, errors: {}, pending: {}, readyAt: null, readyApp: null, onboardingHold: false });
  useRouter.setState({ route: { page: "home" }, previous: null });
  useNotices.setState({ items: [] });
  resetProgressForTests();
  resetFreeGamesForTests();
  resetLibraryPrefsForTests();
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
    expect(screen.getByRole("heading", { name: "Home" })).toBeInTheDocument(); // screen readers only: no greeting
    expect(screen.queryByText(/Welcome back/)).not.toBeInTheDocument();
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
    expect(useRouter.getState().route).toEqual({ page: "store", target: { kind: "product", slug: "lanternfall" } });
  });

  it("offline, the free games section is simply not drawn", async () => {
    await mount("offline", <Home />);
    expect(screen.queryByRole("region", { name: "Free this week" })).not.toBeInTheDocument();
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

  it("the lit step names the phase, so the card header does not repeat it as a state label", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const header = (await screen.findAllByRole("heading", { level: 2 }))[0]!.closest("header")!;
    expect(within(screen.getByRole("list", { name: "Steps" })).getByText("Downloading")).toBeInTheDocument();
    expect(within(header).queryByText("Downloading")).not.toBeInTheDocument();
  });

  it("says each fact once: one title, a big percent with the time left, the bytes of this phase, live speeds as the legend, no caption", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const bar = await screen.findByRole("progressbar", { name: /^Downloading / });
    expect(bar).toBeInTheDocument();
    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toContain(bar.getAttribute("aria-label")!.replace(/^Downloading /, ""));
    // The slot right of the bar holds only the bytes, named; the time left sits beside the percentage.
    expect(para(/ of .* (GB|MB) downloaded$/)).toBeTruthy();
    expect(screen.getByText(/ left$/)).toBeInTheDocument();
    expect(Number(bar.getAttribute("aria-valuenow"))).toBeGreaterThan(0);
    // The two live speeds are the chart's legend: label under value, no stats table, no legend list.
    const speeds = screen.getByLabelText("Speed");
    expect(within(speeds).getByText("Download")).toBeInTheDocument();
    expect(within(speeds).getByText("Disk write")).toBeInTheDocument();
    expect(screen.queryByRole("list", { name: "Series" })).not.toBeInTheDocument();
    for (const g of ["Progress", "Disk", "Time"]) expect(screen.queryByRole("region", { name: g })).not.toBeInTheDocument();
    // No caption: files, size on disk, server and elapsed are not facts a player acts on.
    for (const t of [/files/i, /on disk/i, /Akamai|Fastly|Cloudflare/, /^Server$/]) expect(screen.queryByText(t)).not.toBeInTheDocument();
    expect(screen.queryByText("—")).not.toBeInTheDocument();
  });

  it("puts the stepper below the bar, so it reads as the bar's timeline", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const bar = await screen.findByRole("progressbar", { name: /^Downloading / });
    const steps = screen.getByRole("list", { name: "Steps" });
    expect(bar.compareDocumentPosition(steps) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("the header is the title only: no mode control, no limit button, no Pause all; a paused queue gets one quiet notice with Resume all", async () => {
    const { user } = await mount("queue", <Downloads />, { freeze: true });
    expect(await screen.findByRole("progressbar", { name: /^Downloading / })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Up next" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "History" })).toBeInTheDocument();
    expect(screen.queryByRole("radio", { name: "Full" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Limit / })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Pause all" })).not.toBeInTheDocument();
    expect(screen.queryByText("Downloads are paused.")).not.toBeInTheDocument();
    await act(async () => {
      await rpc().call("queue.pauseAll");
    });
    expect(await screen.findByText("Downloads are paused.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Resume all" }));
    await waitFor(() => expect(screen.queryByText("Downloads are paused.")).not.toBeInTheDocument());
  });

  it("collapses history to the latest few", async () => {
    const { user } = await mount("returning", <Downloads />, { freeze: true });
    const list = await screen.findByRole("heading", { name: "History" });
    const section = list.closest("section")!;
    expect(within(section).getAllByRole("listitem")).toHaveLength(3);
    await user.click(within(section).getByRole("button", { name: /^Show \d+ earlier$/ }));
    expect(within(section).getAllByRole("listitem").length).toBeGreaterThan(3);
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

  it("a verify reads files: one 'Reading' speed, no network or write figure, no chart", async () => {
    await mount("frozenVerifying", <Downloads />);
    expect(await screen.findByRole("progressbar", { name: /^Verifying / })).toBeInTheDocument();
    const speeds = screen.getByLabelText("Speed");
    expect(within(speeds).getByText("Reading")).toBeInTheDocument();
    expect(within(speeds).queryByText("Download")).not.toBeInTheDocument();
    expect(within(speeds).queryByText("Disk write")).not.toBeInTheDocument();
    expect(screen.queryByText("Files checked")).not.toBeInTheDocument();
    expect(para(/GB of .* GB checked$/)).toBeTruthy();
    expect(screen.queryByRole("group", { name: /speed over time/ })).not.toBeInTheDocument();
  });

  it("a repair step shows download and disk write for just the bad files, with the speed graph", async () => {
    await mount("frozenRepairing", <Downloads />);
    const speeds = await screen.findByLabelText("Speed");
    expect(within(speeds).getByText("Download")).toBeInTheDocument();
    expect(within(speeds).getByText("Disk write")).toBeInTheDocument();
    expect(screen.getByText(/files? to fix/)).toBeInTheDocument();
    expect(screen.getByRole("group", { name: /Download and disk speed over time/ })).toBeInTheDocument();
  });

  it("confirms before cancelling, then returns to an empty state", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await user.click((await screen.findAllByRole("button", { name: "Cancel" }))[0]!);
    const dialog = await screen.findByRole("alertdialog");
    expect(dialog).toHaveTextContent(/Cancel install of /);
    // The destructive button doesn't repeat the title.
    await user.click(within(dialog).getByRole("button", { name: "Cancel install" }));
    await waitFor(() => expect(screen.queryByRole("progressbar", { name: /^(Preparing|Downloading) / })).not.toBeInTheDocument());
  });

  it("offers Cancel only where Core accepts it (a running uninstall, a move recording its new folder)", async () => {
    let next = "";
    await mount(
      "queue",
      (b) => {
        const running = b.snap.queue.jobs.find((j) => j.status === "running")!;
        const queued = b.snap.queue.jobs.find((j) => j.status === "queued")!;
        next = queued.title;
        return (
          <>
            <ActiveJobCard job={{ ...running, cancellable: false }} />
            <QueueRow job={{ ...queued, cancellable: false }} />
            <QueueRow job={queued} />
          </>
        );
      },
      { freeze: true },
    );
    expect(await screen.findByRole("progressbar", { name: /^Downloading / })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancel" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: `Cancel ${next}` })).toHaveLength(1);
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

describe("Downloads: Couldn't finish", () => {
  const row = (name: RegExp | string) => screen.getByText(name).closest("li")!;

  it("sits below Up next and above History, one row per failure, with no callout, no red label and no x", async () => {
    await mount("errors", <Downloads />);
    const section = (await screen.findByRole("region", { name: "Couldn't finish" })) as HTMLElement;
    expect(within(section).getAllByRole("listitem").length).toBeGreaterThanOrEqual(5);
    expect(screen.queryByText("All caught up")).not.toBeInTheDocument();
    expect(screen.queryByText("Nothing downloading")).not.toBeInTheDocument();
    expect(screen.queryByText("Failed", { exact: true })).not.toBeInTheDocument();
    expect(within(section).queryByRole("alert")).not.toBeInTheDocument();
    const history = screen.getByRole("heading", { name: "History" }).closest("section")!;
    expect(section.compareDocumentPosition(history) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    // History lists no failure.
    expect(within(history).queryByText(/failed/i)).not.toBeInTheDocument();
    // Each row says what failed and when, in one line.
    expect(within(section).getAllByText(/^(Update|Install|Verify|Move) failed · /).length).toBeGreaterThanOrEqual(5);
  });

  it("a space failure compares the need with the drive's free space: 'Free up space' leads, 'Try again' stays beside it", async () => {
    await mount("errors", <Downloads />);
    const li = row(/^Needs 4\.8 GB, 921\.6 MB free on [A-Z]:$/);
    expect(within(li).getByRole("button", { name: "Free up space" })).toBeEnabled();
    expect(within(li).getByRole("button", { name: "Try again" })).toBeEnabled();
  });

  it("Try again while still short fails again at once, with the same row and no download started", async () => {
    const { user } = await mount("errors", <Downloads />, { freeze: true });
    const li = row(/^Needs 4\.8 GB/);
    await user.click(within(li).getByRole("button", { name: "Try again" }));
    await waitFor(() => expect(within(row(/^Needs 4\.8 GB/)).getByRole("button", { name: "Free up space" })).toBeInTheDocument());
    expect(screen.queryByRole("progressbar")).not.toBeInTheDocument();
  });

  it("once space is freed (even outside Glider) the row says so and 'Try again' becomes the one button", async () => {
    const { controller } = await mount("errors", <Downloads />, { freeze: true });
    const drive = useApp.getState().snapshot!.queue.jobs.find((j) => j.error?.code === "insufficientSpace")!.error!.drive!;
    await act(async () => {
      controller.setFreeSpace(drive[0]!, 60 * 1024 ** 3);
      window.dispatchEvent(new Event("focus")); // the user comes back from Explorer
    });
    const li = (await screen.findByText("There's enough space now.")).closest("li")!;
    expect(within(li).getByRole("button", { name: "Try again" })).toBeInTheDocument();
    expect(within(li).queryByRole("button", { name: "Free up space" })).not.toBeInTheDocument();
  });

  it("a row's × dismisses it; Clear all in History leaves the failures", async () => {
    const { user } = await mount("errors", <Downloads />);
    const section = (await screen.findByRole("region", { name: "Couldn't finish" })) as HTMLElement;
    const before = within(section).getAllByRole("listitem").length;
    await user.click(screen.getByRole("button", { name: "Clear all" }));
    await waitFor(() => expect(screen.queryByRole("heading", { name: "History" })).not.toBeInTheDocument());
    expect(within(screen.getByRole("region", { name: "Couldn't finish" })).getAllByRole("listitem")).toHaveLength(before);
    await user.click(within(section).getAllByRole("button", { name: /^Dismiss / })[0]!);
    await waitFor(() => expect(within(screen.getByRole("region", { name: "Couldn't finish" })).getAllByRole("listitem")).toHaveLength(before - 1));
  });

  it("the bulk update offer does not count the update that sits in Couldn't finish", async () => {
    await mount("errors", <Downloads />);
    const snap = useApp.getState().snapshot!;
    const failedUpdates = new Set(snap.queue.jobs.filter((j) => j.status === "failed" && j.kind === "update").map((j) => j.appName)).size;
    const offered = Number(/Update (\d+) games?/.exec((await screen.findByRole("button", { name: /^Update \d+ games?$/ })).textContent!)![1]);
    expect(offered).toBe(snap.library.updatesAvailable - failedUpdates);
  });

  it("a failed job turns the rail's Downloads dot red, over running and paused", () => {
    const job = (status: string) => ({ status }) as never;
    expect(railDotTone([job("running")])).toBe("accent");
    expect(railDotTone([job("paused")])).toBe("warning");
    expect(railDotTone([job("failed"), job("running")])).toBe("danger");
    expect(railDotTone([job("completed")])).toBeNull();
    expect(railDotTone([])).toBeNull();
  });
});

describe("Downloads: the speed status", () => {
  it("is quiet when nothing holds the speed back", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    await screen.findByRole("progressbar", { name: /^Downloading / });
    expect(screen.queryByRole("button", { name: /Slow download|Speed limit|mode$/ })).not.toBeInTheDocument();
  });

  it("a slow drive is 'Slow download'; hover gives one line, a click opens the full reason with numbers and advice", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true, bottleneck: "disk" });
    const button = await screen.findByRole("button", { name: "Slow download" });
    await user.hover(button);
    expect(await screen.findByRole("tooltip")).toHaveTextContent(/Drive [A-Z]: writes about .* MB\/s, slower than your connection\./);
    await user.click(button);
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Your drive is limiting this download");
    expect(within(dialog).getByRole("region", { name: "Measured" })).toHaveTextContent(/writes about/);
    expect(within(dialog).getByRole("region", { name: "What you can do" })).toBeInTheDocument();
    // No corner x: the dialog has its own Close.
    expect(within(dialog).getAllByRole("button")).toHaveLength(1);
    expect(within(dialog).getByRole("button", { name: "Close" })).toBeInTheDocument();
  });

  it("a limit the user chose is a muted label that links to its Settings row", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    await screen.findByRole("progressbar", { name: /^Downloading / });
    await act(async () => {
      await rpc().call("settings.update", { ...useApp.getState().snapshot!.settings, speedLimitBytesPerSecond: 8 * 1024 * 1024 });
    });
    await user.click(await screen.findByRole("button", { name: "Speed limit" }));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Held to your speed limit");
    await user.click(within(dialog).getByRole("button", { name: "Change speed limit" }));
    expect(useRouter.getState().route).toEqual({ page: "settings", focus: "speedLimit" });
  });
});

describe("Downloads: empty", () => {
  it("is an invitation without a card around it", async () => {
    await mount("emptyLibrary", <Downloads />);
    expect(screen.getByText("Nothing downloading")).toBeInTheDocument();
    expect(screen.queryByText("All caught up")).not.toBeInTheDocument();
  });
});

describe("Shell routing", () => {
  it("renders the page for the route, including a game, and follows navigate()", async () => {
    await mount("returning", <Shell />);
    expect(screen.getByRole("heading", { name: "Home" })).toBeInTheDocument();
    act(() => navigate("library"));
    expect(await screen.findByRole("heading", { name: "Library" })).toBeInTheDocument();
    act(() => navigate("game/Fortnite"));
    expect(await screen.findByRole("heading", { name: "Fortnite" })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^Library/ })).toHaveLength(2); // the rail item (with its dot said in words) and the back link
    act(() => navigate("downloads"));
    expect(await screen.findByRole("heading", { name: "Downloads" })).toBeInTheDocument();
    act(() => navigate("store"));
    expect(await screen.findByRole("button", { name: "Open in browser" })).toBeInTheDocument(); // the Store toolbar
    act(() => navigate("settings"));
    expect(await screen.findByRole("heading", { name: "Settings" })).toBeInTheDocument();
  });

  it("the title-bar activity pill appears where the job is not already on the page, and hides on Downloads", async () => {
    await mount("downloading", <Shell />, { freeze: true });
    act(() => navigate("library"));
    expect(await screen.findByRole("button", { name: /Open downloads$/ })).toBeInTheDocument();
    act(() => navigate("downloads"));
    await screen.findByRole("heading", { name: "Downloads" });
    await waitFor(() => expect(screen.queryByRole("button", { name: /Open downloads$/ })).not.toBeInTheDocument());
  });

  it("shows onboarding instead of the app until it is completed", async () => {
    await mount("firstRun", <Shell />);
    expect(screen.queryByRole("navigation", { name: "Main" })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /Your whole Epic library/ })).toBeInTheDocument();
  });

  it("keeps setup on screen at its last step even though Core counts it completed there, until the last button is pressed", async () => {
    const { user } = await mount("firstRunSignedIn", <Shell />);
    await rpc().call("onboarding.setStep", { step: "done", skipped: false });
    expect(useApp.getState().snapshot!.onboarding.completed).toBe(true); // Core completes at the last step
    expect(await screen.findByText("You're all set")).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Main" })).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Go to your library" }));
    expect(await screen.findByRole("heading", { name: "Library" })).toBeInTheDocument();
    expect(useUi.getState().onboardingHold).toBe(false);
  });
});
