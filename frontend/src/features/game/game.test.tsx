import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { AppSnapshot, GameSummary } from "../../ipc/contract.gen";
import { rpc, setRpcForTests } from "../../ipc/rpc";
import type { MockOptions, ScenarioName } from "../../ipc/mock";
import { startBridge } from "../../state/bridge";
import { useLaunchNotes } from "../../state/launch";
import { useLibrary } from "../../state/library";
import { useNotices } from "../../state/notices";
import { resetProgressForTests } from "../../state/progress";
import { useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { useUi } from "../../state/ui";
import { mockClient } from "../../test/fixtures";
import { MotionProvider, TooltipProvider } from "../../ui";
import { Dialogs } from "../dialogs/Dialogs";
import { GameActions } from "./GameActions";
import { GameView } from "./GameView";

let stop: (() => void) | undefined;

interface Booted {
  snap: AppSnapshot;
  find: (pred: (g: GameSummary) => boolean) => GameSummary;
}

async function mount(scenario: ScenarioName, ui: React.ReactElement | ((b: Booted) => React.ReactElement), opts: MockOptions = {}) {
  const { client, controller } = mockClient(scenario, opts);
  setRpcForTests(client, controller);
  stop = await startBridge();
  const booted: Booted = {
    snap: useApp.getState().snapshot!,
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

const idleInstalled = (g: GameSummary) =>
  g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none" && !g.updateAvailable && g.canRunOffline && g.saveStatus === "inSync" && g.appName !== "Fortnite";
const fresh = (g: GameSummary) => g.kind === "game" && g.installStatus === "notInstalled" && g.thirdParty === "none";

beforeEach(() => {
  useApp.setState({ snapshot: null });
  useLibrary.getState().reset();
  useUi.setState({ dialog: null, errors: {}, pending: {}, readyAt: null, readyApp: null });
  useRouter.setState({ route: { page: "home" }, previous: null });
  useNotices.setState({ items: [] });
  useLaunchNotes.setState({ notes: {} });
  resetProgressForTests();
});
afterEach(() => {
  stop?.();
  setRpcForTests(null);
});

describe("Game page: hero and the one primary action", () => {
  it("drops the menu's cancel while Core refuses it (a running uninstall)", async () => {
    const { user } = await mount("queue", (b) => {
      const running = b.snap.queue.jobs.find((j) => j.status === "running")!;
      const game = b.find((g) => g.appName === running.appName);
      const job = { ...running, kind: "uninstall" as const, cancellable: false };
      return (
        <GameActions
          snap={b.snap}
          game={game}
          detail={null}
          moment={false}
          action={{ kind: "cancel", label: "Cancel", tone: "quiet", busy: false, disabled: false, faceKey: "cancel", intent: "none", status: { tone: "accent", text: "Uninstalling" }, job }}
        />
      );
    });
    const group = screen.getByRole("group");
    await user.click(within(group).getByRole("button", { name: "More actions" }));
    expect(screen.queryByRole("menuitem", { name: /^Cancel / })).not.toBeInTheDocument();
  });

  it("disables the primary Cancel while Core refuses it", async () => {
    await mount("queue", (b) => {
      const running = b.snap.queue.jobs.find((j) => j.status === "running")!;
      const job = { ...running, kind: "uninstall" as const, cancellable: false };
      return (
        <GameActions
          snap={b.snap}
          game={b.find((g) => g.appName === running.appName)}
          detail={null}
          moment={false}
          action={{ kind: "cancel", label: "Cancel", tone: "quiet", busy: false, disabled: false, faceKey: "cancel", intent: "cancel", status: { tone: "accent", text: "Uninstalling" }, job }}
        />
      );
    });
    expect(within(screen.getByRole("group")).getByRole("button", { name: "Cancel" })).toBeDisabled();
  });

  it("shows no status line for a ready game, one split button and the facts, with no version repeated", async () => {
    await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    // The Play button already says it: the "Ready to play" line only appears for the one-off ready moment.
    expect(screen.queryByText("Ready to play")).not.toBeInTheDocument();
    const group = screen.getByRole("group");
    expect(within(group).getByRole("button", { name: "Play" })).toBeEnabled();
    expect(within(group).getByRole("button", { name: "More actions" })).toBeEnabled();
    expect(screen.getByText("Version")).toBeInTheDocument();
    expect(screen.getAllByText(/Version/)).toHaveLength(1);
    expect(screen.getByText("Playtime")).toBeInTheDocument();
  });

  it("names Fortnite, the v0.1 game, with its update as the primary action and Play in the menu", async () => {
    const { user } = await mount("returning", <GameView appName="Fortnite" />);
    expect(screen.getByRole("heading", { name: "Fortnite" })).toBeInTheDocument();
    expect(screen.getByText("Update available")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Update" })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: "More actions" }));
    expect(await screen.findByRole("menuitem", { name: "Play without updating" })).toBeInTheDocument();
  });

  it("Play goes through Launching to Playing, then offers Close game", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />, { latency: 1 });
    await user.click(screen.getByRole("button", { name: "Play" }));
    const launching = await screen.findByRole("button", { name: /Launching/ });
    expect(launching).toHaveAttribute("aria-busy", "true");
    await waitFor(() => expect(screen.getByRole("button", { name: /Close game/ })).toBeEnabled(), { timeout: 8000 });
    expect(screen.getByText(/^Playing for /)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Close game/ }));
    await waitFor(() => expect(screen.getByRole("button", { name: /^Play/ })).toBeInTheDocument(), { timeout: 4000 });
  });

  it("warms the launch command when Play is hovered", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    let warmed = 0;
    const original = rpc().call.bind(rpc());
    const spy = rpc();
    spy.call = ((m: string, p?: unknown) => {
      if (m === "game.prepareLaunch") warmed++;
      return (original as (m: string, p?: unknown) => Promise<unknown>)(m, p);
    }) as typeof spy.call;
    await user.hover(screen.getByRole("button", { name: "Play" }));
    expect(warmed).toBeGreaterThan(0);
  });

  it("a launch failure shows inline with its fix", async () => {
    const { user, controller } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    controller.failNext("launchFailed");
    await user.click(screen.getByRole("button", { name: "Play" }));
    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent(/Couldn't start/);
    expect(within(alert).getByRole("button", { name: "Repair" })).toBeInTheDocument();
  });

  it("a running install: Pause replaces the label, progress is one bar, and Pause becomes Resume", async () => {
    const { user } = await mount("downloading", (b) => <GameView appName={b.snap.queue.jobs.find((j) => j.status === "running")!.appName} />, { freeze: false });
    expect(await screen.findByRole("button", { name: "Pause" })).toBeEnabled();
    expect(screen.getAllByRole("progressbar")).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Pause" }));
    expect(await screen.findByRole("button", { name: "Resume" })).toBeEnabled();
    expect(screen.getByText("Install paused")).toBeInTheDocument();
  });

  it("a running move cannot pause, so its primary is Cancel", async () => {
    await mount("frozenMoving", (b) => <GameView appName={b.snap.queue.jobs.find((j) => j.status === "running")!.appName} />);
    expect(await screen.findByRole("button", { name: "Cancel" })).toBeEnabled();
    expect(screen.queryByRole("button", { name: "Pause" })).not.toBeInTheDocument();
    expect(screen.getByText("Moving")).toBeInTheDocument();
  });

  it("explains waiting on Epic Games Launcher, with Cancel, for that game only", async () => {
    await mount("eglBusy", (b) => <GameView appName={b.snap.queue.jobs.find((j) => j.status === "waiting")!.appName} />, { freeze: true });
    expect(screen.getByText(/Epic Games Launcher is updating .*, so Glider will wait/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Cancel" })).toBeEnabled();
  });

  it("shows a failed job's error with its one-click fix, on the right game", async () => {
    await mount("errors", (b) => <GameView appName={b.snap.queue.jobs.find((x) => x.status === "failed" && x.error?.code === "insufficientSpace")!.appName} />);
    expect(screen.getByRole("alert")).toHaveTextContent("Not enough disk space");
    expect(within(screen.getByRole("alert")).getByRole("button", { name: "Free up space" })).toBeInTheDocument();
  });

  it("offline: Install says so, and Play needs-internet games say why", async () => {
    await mount("offline", (b) => <GameView appName={b.find((g) => g.installStatus === "installed" && g.thirdParty === "none" && !g.canRunOffline).appName} />);
    expect(screen.getByText("Needs an internet connection")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Play" })).toBeDisabled();
  });

  it("a game that isn't in the library says so, with a way back", async () => {
    const { user } = await mount("returning", <GameView appName="nope-nope" />);
    expect(screen.getByText("That game isn't in your library")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Back to library" }));
    expect(useRouter.getState().route).toEqual({ page: "library" });
  });

  it("the menu has Verify files, Move, Open install folder, Create shortcut, Add to collection, Hide and Uninstall", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    await user.click(screen.getByRole("button", { name: "More actions" }));
    for (const name of ["Verify files", "Move…", "Open install folder", "Create shortcut", "Add to collection", "Hide from library", "Uninstall…"]) {
      expect(await screen.findByRole("menuitem", { name })).toBeInTheDocument();
    }
  });

  it("favorites toggle optimistically and the backend's delta agrees", async () => {
    const { user } = await mount("returning", <GameView appName="Fortnite" />);
    await user.click(screen.getByRole("button", { name: "Favorite" }));
    await waitFor(() => expect(useLibrary.getState().games.get("Fortnite")!.favorite).toBe(false));
  });
});

describe("Game page: only what the game has", () => {
  it("a game without DLC or cloud saves shows neither section, not an empty block", async () => {
    await mount("returning", (b) => <GameView appName={b.find((g) => g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none" && g.dlcCount === 0 && !g.cloudSaves).appName} />);
    await screen.findByText("Version");
    expect(screen.queryByRole("heading", { name: /DLC/ })).not.toBeInTheDocument();
    expect(screen.queryByText("Cloud saves")).not.toBeInTheDocument();
  });

  it("Game settings are collapsed, and writing launch arguments reaches the backend", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    const toggle = await screen.findByRole("button", { name: "Game settings" });
    expect(toggle).toHaveAttribute("aria-expanded", "false");
    await user.click(toggle);
    const args = screen.getByRole("textbox", { name: "Launch arguments" });
    await user.type(args, "-windowed");
    await user.tab();
    const app = [...useLibrary.getState().games.values()].find(idleInstalled)!.appName;
    await waitFor(async () => expect((await rpc().call("game.getDetail", { appName: app })).settings.launchArgs).toBe("-windowed"));
  });

  it("DLC: installing one queues an install job for that DLC (Core's Modify makes one job per DLC)", async () => {
    let host!: GameSummary;
    const { user } = await mount("returning", (b) => {
      const games = useLibrary.getState().games;
      host = b.find((g) => g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none" && g.dlcCount > 0 && [...games.values()].some((d) => d.baseAppName === g.appName && d.installStatus === "notInstalled"));
      return <GameView appName={host.appName} />;
    });
    const install = await screen.findAllByRole("button", { name: /^Install / });
    await user.click(install[0]!);
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "install" && j.appName !== host.appName && j.status !== "completed")).toBe(true));
  });

  it("Cloud saves: the switch in Game settings turns them off for this game only", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find(idleInstalled).appName} />);
    await user.click(await screen.findByRole("button", { name: "Game settings" }));
    const sw = await screen.findByRole("switch", { name: "Sync cloud saves" });
    expect(sw).toBeChecked();
    await user.click(sw);
    await waitFor(() => expect(screen.getByText("Off")).toBeInTheDocument());
  });

  it("Cloud saves: Sync now syncs and reports in sync", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.saveStatus === "localNewer").appName} />);
    expect(await screen.findByText("This PC is newer")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Sync now" }));
    expect(await screen.findByText("In sync")).toBeInTheDocument();
  });
});

describe("Save conflict", () => {
  const conflict = (b: Booted) => <GameView appName={b.find((g) => g.saveStatus === "conflict").appName} />;

  it("the Cloud saves row is the only surface: Resolve opens the side-by-side dialog, nothing is pre-selected", async () => {
    const { user } = await mount("returning", conflict);
    expect(await screen.findByText("Saves differ")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Resolve…" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("On this PC")).toBeInTheDocument();
    expect(within(dialog).getByText("In the cloud")).toBeInTheDocument();
    expect(within(dialog).getByText(/backed up first/)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Keep local" })).not.toHaveFocus();
    expect(within(dialog).getByRole("button", { name: "Keep cloud" })).not.toHaveFocus();
    expect(dialog).toHaveFocus();
  });

  it("keeping one side resolves the conflict and closes the dialog", async () => {
    const { user } = await mount("returning", conflict);
    await user.click(await screen.findByRole("button", { name: "Resolve…" }));
    await user.click(await screen.findByRole("button", { name: "Keep cloud" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await waitFor(() => expect(screen.queryByText("Saves differ")).not.toBeInTheDocument());
  });

  it("Play on a conflict opens the dialog with 'and play', and keeping a side launches the game", async () => {
    const { user } = await mount("returning", conflict);
    await user.click(await screen.findByRole("button", { name: "Play" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Keep local and play" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Close game" })).toBeEnabled(), { timeout: 8000 });
  });

  it("Cancel leaves everything as it was", async () => {
    const { user } = await mount("returning", conflict);
    await user.click(await screen.findByRole("button", { name: "Resolve…" }));
    await user.click(await screen.findByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.getByText("Saves differ")).toBeInTheDocument();
  });
});

describe("Third-party launcher outcomes", () => {
  it("needs linking: Play explains it and Link account links the game", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.thirdParty === "ubisoft" && g.installStatus === "installed").appName} />);
    await user.click(screen.getByRole("button", { name: "Play" }));
    expect(await screen.findByText("Link your Ubisoft Connect account")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Link account" }));
    await waitFor(() => expect(screen.queryByText("Link your Ubisoft Connect account")).not.toBeInTheDocument(), { timeout: 5000 });
  });

  it("a game Epic doesn't install plays through its launcher and says what happened", async () => {
    const { user } = await mount("returning", (b) => <GameView appName={b.find((g) => g.installStatus === "notInstallable" && g.thirdParty !== "none").appName} />);
    expect(screen.getByText(/^Plays through /)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Play" }));
    expect(await screen.findByText(/^Opened /)).toBeInTheDocument();
    expect(screen.getByText(/can't track play time/)).toBeInTheDocument();
  });
});

describe("Install dialog", () => {
  const open = (b: Booted, pred: (g: GameSummary) => boolean = fresh) => {
    const g = b.find(pred);
    act(() => useUi.getState().openDialog({ kind: "install", appName: g.appName }));
    return <div />;
  };

  it("leads with the totals and the free-space check, then where, then components and DLC", async () => {
    await mount("returning", (b) => open(b, (g) => fresh(g) && g.dlcCount > 0));
    expect(await screen.findByText("Fits, with", { exact: false })).toBeInTheDocument();
    expect(screen.getByText("Download")).toBeInTheDocument();
    expect(screen.getByText("Install size")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Install to" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Install" })).toBeEnabled();
  });

  it("a game with no DLC and one component shows neither list", async () => {
    await mount("returning", (b) => open(b, (g) => fresh(g) && g.dlcCount === 0));
    await screen.findByText("Fits, with", { exact: false });
    expect(screen.queryByText("DLC")).not.toBeInTheDocument();
  });

  it("blocks the install when it doesn't fit and says how much is missing", async () => {
    await mount("diskFull", (b) => open(b));
    expect(await screen.findByText(/more on D:\. Choose another folder or free up space\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Install" })).toBeDisabled();
  });

  it("toggling a DLC re-plans: the totals come from Core", async () => {
    const { user } = await mount("returning", (b) => open(b, (g) => fresh(g) && g.dlcCount > 0));
    const disk = async () => (await screen.findByText("Install size")).nextElementSibling!.textContent;
    const before = await disk();
    const boxes = (await screen.findAllByRole("checkbox")).filter((c) => !(c as HTMLButtonElement).disabled);
    await user.click(boxes[0]!);
    await waitFor(async () => expect(await disk()).not.toBe(before), { timeout: 3000 });
  });

  it("offers no components: Core can't plan or change them yet", async () => {
    await mount("returning", (b) => open(b, (g) => fresh(g) && g.dlcCount > 0 && g.appName !== "Fortnite"));
    await screen.findByText("Fits, with", { exact: false });
    expect(screen.queryByText("Components")).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /\(required\)/ })).not.toBeInTheDocument();
  });

  it("starts the install into the chosen folder and goes to Downloads", async () => {
    const { user } = await mount("returning", (b) => open(b, (g) => fresh(g) && g.dlcCount === 0));
    const install = await screen.findByRole("button", { name: "Install" });
    await waitFor(() => expect(install).toBeEnabled());
    await user.click(install);
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "install" && j.status !== "completed")).toBe(true));
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
  });

  it("other library folders show their free space in one short menu, with Choose another folder…", async () => {
    const { user } = await mount("returning", (b) => open(b));
    await user.click(await screen.findByRole("button", { name: "Install to" }));
    expect((await screen.findAllByRole("menuitem")).length).toBeGreaterThan(2);
    expect(screen.getByRole("menuitem", { name: "Choose another folder…" })).toBeInTheDocument();
  });
});

describe("Move and uninstall dialogs", () => {
  it("Move lands in the folder name Core gave the game, never base\\AppName", async () => {
    const { user } = await mount("returning", (b) => {
      const g = b.find(idleInstalled);
      act(() => useUi.getState().openDialog({ kind: "move", appName: g.appName }));
      return <div />;
    });
    const move = await screen.findByRole("button", { name: "Move to" });
    await user.click(move);
    await user.click((await screen.findAllByRole("menuitem"))[0]!);
    const lands = await screen.findByText("Lands in");
    const g = [...useLibrary.getState().games.values()].find(idleInstalled)!;
    const detailFolder = g.installPath!.split("\\").pop()!;
    expect(lands.parentElement).toHaveTextContent(new RegExp(`\\\\${detailFolder}$`));
    await waitFor(() => expect(screen.getByRole("button", { name: "Move" })).toBeEnabled());
  });

  it("Uninstall offers keep or delete, names the real folder, and deleting is the danger action", async () => {
    const { user } = await mount("returning", (b) => {
      const g = b.find((x) => idleInstalled(x) && !x.sharedWithEgl);
      act(() => useUi.getState().openDialog({ kind: "uninstall", appName: g.appName }));
      return <div />;
    });
    const dialog = await screen.findByRole("alertdialog");
    expect(within(dialog).getByRole("radio", { name: /Keep the files/ })).toBeInTheDocument();
    expect(within(dialog).getByRole("radio", { name: /Delete the files/ })).toBeChecked();
    const g = [...useLibrary.getState().games.values()].find((x) => idleInstalled(x) && !x.sharedWithEgl)!;
    expect(dialog).toHaveTextContent(g.installPath!.split("\\").pop()!);
    await user.click(within(dialog).getByRole("button", { name: "Uninstall and delete files" }));
    await waitFor(() => expect(useApp.getState().snapshot!.queue.jobs.some((j) => j.kind === "uninstall")).toBe(true));
  });
});
