import { afterEach, describe, expect, it, vi } from "vitest";
import { act, renderHook, screen, waitFor, within } from "@testing-library/react";
import type { JobInfo } from "../../ipc/contract.gen";
import { rpc } from "../../ipc/rpc";
import { useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { installCleanStores, mount as mountBase } from "../../test/harness";
import { Dialogs } from "../dialogs/Dialogs";
import { ActiveJobCard } from "./ActiveJobCard";
import { Downloads, READY_MS, useReadyJob } from "./Downloads";

installCleanStores();

// Each mock keeps emitting progress for its jobs after its test ends; into the shared progress store that would
// leak one scenario's phase into the next test's card (job ids repeat across scenarios). So every mount is stopped.
const mounted: { controller: { dispose(): void } }[] = [];
const mount: typeof mountBase = async (...args) => {
  const m = await mountBase(...args);
  mounted.push(m);
  return m;
};
afterEach(() => {
  for (const m of mounted.splice(0)) m.controller.dispose();
});

/** The card of the running job: a labelled region whose heading is the game's title. */
const page = (
  <>
    <Downloads />
    <Dialogs />
  </>
);
/** Under a loaded test run the mock's progress events can take a while to reach a freshly mounted card. */
const SLOW = { timeout: 5000 };
const card = async () => {
  const bar = await screen.findByRole("progressbar", { name: /^(Preparing|Downloading|Installing|Checking|Verifying|Repairing|Moving|Finalizing|Removing|Importing|Starting)/ }, SLOW);
  return bar.closest("section") as HTMLElement;
};

describe("the card, running", () => {
  it("is a labelled region: the title is its heading, the bar names the step and reads percent and time left", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const region = await card();
    const heading = within(region).getByRole("heading", { level: 2 });
    expect(region).toHaveAccessibleName(heading.textContent!);
    const bar = within(region).getByRole("progressbar");
    expect(bar).toHaveAccessibleName(`Downloading ${heading.textContent}`);
    expect(bar.getAttribute("aria-valuetext")).toMatch(/^\d+ percent(, .+ left)?$/);
    expect(Number(bar.getAttribute("aria-valuenow"))).toBeGreaterThan(0);
  });

  it("says each fact once: time left leads, one step line with its bytes and speed, one disclosure; no stepper, no second speed, no labelled Cancel", async () => {
    await mount("downloading", <Downloads />, { freeze: true });
    const region = await card();
    expect(within(region).getByText(/^left$/)).toBeInTheDocument();
    expect(region).toHaveTextContent(/Downloading · [\d.]+ (of [\d.]+ )?(MB|GB)( of [\d.]+ (MB|GB))? · [\d.]+ (MB|KB|GB)\/s/);
    expect(within(region).queryByRole("list", { name: "Steps" })).toBeNull();
    for (const old of [/Disk write/, /elapsed/i, /average/i, /Slow download/, /downloaded$/]) expect(within(region).queryByText(old)).toBeNull();
    expect(within(region).getByRole("button", { name: "Cancel download" })).toBeInTheDocument();
    expect(within(region).queryByRole("button", { name: "Cancel" })).toBeNull();
    // Healthy download: the disclosure is plain "Details".
    expect(within(region).getByRole("button", { name: "Details, show details" })).toHaveAttribute("aria-expanded", "false");
  });

  it("Pause becomes Resume in the same quiet square: one phrase, no bright primary, the same accessible name as the action", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: false });
    const region = await card();
    const pause = within(region).getByRole("button", { name: "Pause" });
    await user.click(pause);
    const resume = await within(region).findByRole("button", { name: "Resume" });
    await waitFor(() => expect(within(region).getByText("Paused")).toBeInTheDocument());
    // As quiet as Pause: the same glass treatment, no primary fill.
    expect(resume.className).toBe(pause.className);
    expect(resume.className).not.toMatch(/bg-primary/);
    expect(region).toHaveTextContent(/Downloading paused · [\d.]+ .*(MB|GB)/);
    expect(region.textContent).not.toMatch(/about|elapsed/i);
    await user.click(resume);
    await waitFor(() => expect(within(region).getByRole("button", { name: "Pause" })).toBeInTheDocument());
  });

  it("a slow drive is named amber on the disclosure; Details explains it inline with the chart against the ceiling, what to do and the task", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true, bottleneck: "disk" });
    const region = await card();
    const trigger = await within(region).findByRole("button", { name: /^Limited by drive [A-Z]:, show details$/ });
    expect(trigger.className).toMatch(/text-warning-text/);
    await user.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");
    expect(within(region).getByRole("heading", { level: 3, name: /^Drive [A-Z]: is the limit$/ })).toBeInTheDocument();
    expect(within(region).getByRole("group", { name: /Download speed over time/ })).toBeInTheDocument();
    expect(region).toHaveTextContent(/Your connection, up to [\d.]+ MB\/s/);
    expect(within(region).getAllByRole("listitem")).toHaveLength(3);
    expect(region).toHaveTextContent("TaskInstall, version");
    // No dialog: the reason is in the card.
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("a limit the player chose is neutral, and Details links to its Settings row", async () => {
    const { user } = await mount("downloading", <Downloads />, { freeze: true });
    const region = await card();
    await act(async () => {
      await rpc().call("settings.update", { ...useApp.getState().snapshot!.settings, speedLimitBytesPerSecond: 8 * 1024 * 1024 });
    });
    const trigger = await within(region).findByRole("button", { name: /^Your .* limit, show details$/ });
    expect(trigger.className).not.toMatch(/text-warning-text/);
    await user.click(trigger);
    await user.click(within(region).getByRole("button", { name: "Change limit" }));
    expect(useRouter.getState().route).toEqual({ page: "settings", focus: "speedLimit" });
  });

  it("Details stays open the way the player left it, and storage that throws changes nothing", async () => {
    const { user, view } = await mount("downloading", <Downloads />, { freeze: true });
    const region = await card();
    await user.click(within(region).getByRole("button", { name: /show details$/ }));
    expect(localStorage.getItem("glider.downloads.details")).toBe("1");
    view.unmount();
    await mount("downloading", <Downloads />, { freeze: true });
    expect(await within(await card()).findByRole("button", { name: /show details$/ })).toHaveAttribute("aria-expanded", "true");
    localStorage.removeItem("glider.downloads.details");
    const spy = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    try {
      await user.click(within(await card()).getByRole("button", { name: /show details$/ }));
      expect(within(await card()).getByRole("button", { name: /show details$/ })).toHaveAttribute("aria-expanded", "false");
    } finally {
      spy.mockRestore();
    }
  });

  it("a verify reads files: the step, its bytes and one read speed; no chart, no download figure, plain Details", async () => {
    const { user } = await mount("frozenVerifying", <Downloads />);
    const region = await card();
    await waitFor(() => expect(region).toHaveTextContent(/Checking files · [\d.]+ .*(MB|GB).* · [\d.]+ (MB|GB)\/s/));
    expect(within(region).queryByText(/Download/)).toBeNull();
    await user.click(within(region).getByRole("button", { name: "Details, show details" }));
    expect(within(region).queryByRole("group", { name: /speed over time/ })).toBeNull();
    expect(region).toHaveTextContent("TaskVerify files");
  });

  it("a repair names how many files and keeps the download chart", async () => {
    const { user } = await mount("frozenRepairing", <Downloads />);
    const region = await card();
    await waitFor(() => expect(region).toHaveTextContent(/Repairing \d+ files? · /), SLOW);
    await user.click(within(region).getByRole("button", { name: /show details$/ }));
    expect(await within(region).findByRole("group", { name: /Download speed over time/ }, SLOW)).toBeInTheDocument();
  });

  it("installing still has a time left and a disk chart", async () => {
    const { user } = await mount("frozenInstalling", <Downloads />);
    const region = await card();
    await waitFor(() => expect(region).toHaveTextContent(/Installing · /), SLOW);
    expect(within(region).getByText(/^left$/)).toBeInTheDocument();
    await user.click(within(region).getByRole("button", { name: /show details$/ }));
    expect(await within(region).findByRole("group", { name: /Disk write speed over time/ }, SLOW)).toBeInTheDocument();
  });

  it.each([
    ["frozenFinalizing", "Almost done"],
    ["frozenRemoving", "Removing files…"],
    ["frozenImporting", "Linking the install…"],
  ] as const)("%s says it in the headline, with no step line", async (scenario, text) => {
    await mount(scenario, <Downloads />);
    const region = await card();
    expect(await within(region).findByText(text, undefined, SLOW)).toBeInTheDocument();
    expect(region.textContent).not.toMatch(/ · /);
  });

  it("cancel confirms first, then the card goes", async () => {
    const { user } = await mount("downloading", page, { freeze: true });
    const region = await card();
    await user.click(within(region).getByRole("button", { name: "Cancel download" }));
    const dialog = await screen.findByRole("alertdialog");
    await user.click(within(dialog).getByRole("button", { name: /^Cancel (install|update)$/ }));
    await waitFor(() => expect(screen.queryByRole("progressbar")).toBeNull());
  });
});

describe("the card, announced", () => {
  it("one polite status: the step, the percent in tens and the time left at those moments, the cause when it changes, never per tick", async () => {
    const { controller } = await mount("downloading", <Downloads />, { freeze: true, bottleneck: "disk" });
    const region = await card();
    const status = within(region).getByRole("status");
    expect(status).toHaveAttribute("aria-live", "polite");
    await waitFor(() => expect(status.textContent).toMatch(/^Downloading .+, \d+0 percent(, .+ left)?\. Limited by drive [A-Z]:\.$/), { timeout: 4000 });
    const first = status.textContent;
    // Progress ticks at 10 Hz in the mock; the sentence stays as it was within the same tenth.
    await new Promise((r) => setTimeout(r, 300));
    expect(status.textContent).toBe(first);
    expect(controller).toBeDefined();
  });
});

describe("holds are in the card, in place", () => {
  it("a queue paused from the tray still gets the page's one notice, and its card says Paused", async () => {
    const { user } = await mount("queue", <Downloads />, { freeze: true });
    await card();
    await act(async () => {
      await rpc().call("queue.pauseAll");
    });
    expect(await screen.findByText("Downloads are paused.")).toBeInTheDocument();
    const region = (await screen.findByRole("progressbar")).closest("section")!;
    expect(within(region).getByText("Paused")).toBeInTheDocument();
    expect(within(region).getByRole("button", { name: "Resume" })).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Resume all" }));
    await waitFor(() => expect(screen.queryByText("Downloads are paused.")).toBeNull());
  });

  it("waiting for the Epic Games Launcher reads as such, and continues on its own", async () => {
    await mount("eglBusy", <Downloads />, { freeze: true });
    const region = (await screen.findByRole("progressbar")).closest("section")!;
    expect(region).toHaveTextContent("Waiting for Epic Games Launcher");
    expect(region).toHaveTextContent(/Glider continues on its own/);
    expect(region.textContent).not.toMatch(/queued/i);
  });
});

describe("ready to play", () => {
  const completed = (over: Partial<JobInfo> = {}): JobInfo =>
    ({
      id: "j1",
      appName: "Fortnite",
      title: "Fortnite",
      kind: "install",
      status: "completed",
      phases: [],
      phase: null,
      progress: null,
      error: null,
      createdAt: "2026-01-01T10:00:00Z",
      startedAt: "2026-01-01T10:00:00Z",
      finishedAt: "2026-01-01T10:28:00Z",
      targetPath: "D:\\Epic Games",
      fromVersion: null,
      toVersion: "1",
      blockedReason: null,
      verify: null,
      outcome: null,
      cancellable: false,
      ...over,
    }) as JobInfo;

  it("a finished install: Ready to play, how long it took, a labelled Play that launches the game", async () => {
    const { user } = await mount("returning", <ActiveJobCard job={completed()} />);
    const region = await screen.findByRole("region", { name: "Fortnite" });
    expect(within(region).getByText("Ready to play")).toBeInTheDocument();
    expect(region).toHaveTextContent("Installed in 28 min.");
    const call = vi.spyOn(rpc(), "call");
    await user.click(within(region).getByRole("button", { name: "Play" }));
    expect(call.mock.calls.some((c) => c[0] === "game.launch" && (c[1] as { appName: string }).appName === "Fortnite")).toBe(true);
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("Fortnite: Ready to play."));
  });

  it("a verify says what it found and offers no Play", async () => {
    await mount("returning", <ActiveJobCard job={completed({ kind: "verify", verify: { filesChecked: 10, filesBad: 0, filesFixed: 0, verified: true, bytesToFix: 0 } })} />);
    const region = await screen.findByRole("region", { name: "Fortnite" });
    expect(within(region).getByText("All files are fine")).toBeInTheDocument();
    expect(within(region).queryByRole("button", { name: "Play" })).toBeNull();
  });

  const job = (id: string, status: JobInfo["status"], over: Partial<JobInfo> = {}): JobInfo => completed({ id, status, ...over });

  it("the page keeps a job that finished while it was open until the next one starts, the page is left or the timeout passes", () => {
    vi.useFakeTimers();
    try {
      const running = job("a", "running");
      const done = job("a", "completed");
      const { result, rerender } = renderHook(({ jobs, current }: { jobs: JobInfo[]; current: JobInfo | undefined }) => useReadyJob(jobs, current), { initialProps: { jobs: [running], current: running as JobInfo | undefined } });
      expect(result.current).toBeUndefined();
      rerender({ jobs: [done], current: undefined });
      expect(result.current?.id).toBe("a");
      // The timeout lets it go.
      act(() => void vi.advanceTimersByTime(READY_MS + 1));
      expect(result.current).toBeUndefined();

      // The next job starting takes the card's place straight away.
      const next = job("b", "queued");
      const again = renderHook(({ jobs, current }: { jobs: JobInfo[]; current: JobInfo | undefined }) => useReadyJob(jobs, current), { initialProps: { jobs: [running, next], current: running as JobInfo | undefined } });
      again.rerender({ jobs: [done, next], current: next });
      expect(again.result.current).toBeUndefined();

      // A failure is not "ready", and neither is an uninstall.
      const failed = renderHook(({ jobs, current }: { jobs: JobInfo[]; current: JobInfo | undefined }) => useReadyJob(jobs, current), { initialProps: { jobs: [running], current: running as JobInfo | undefined } });
      failed.rerender({ jobs: [job("a", "failed")], current: undefined });
      expect(failed.result.current).toBeUndefined();
      const gone = renderHook(({ jobs, current }: { jobs: JobInfo[]; current: JobInfo | undefined }) => useReadyJob(jobs, current), { initialProps: { jobs: [job("a", "running", { kind: "uninstall" })], current: job("a", "running", { kind: "uninstall" }) as JobInfo | undefined } });
      gone.rerender({ jobs: [job("a", "completed", { kind: "uninstall" })], current: undefined });
      expect(gone.result.current).toBeUndefined();

      // Clearing History while it shows takes it away too.
      const cleared = renderHook(({ jobs, current }: { jobs: JobInfo[]; current: JobInfo | undefined }) => useReadyJob(jobs, current), { initialProps: { jobs: [running], current: running as JobInfo | undefined } });
      cleared.rerender({ jobs: [done], current: undefined });
      expect(cleared.result.current?.id).toBe("a");
      cleared.rerender({ jobs: [], current: undefined });
      expect(cleared.result.current).toBeUndefined();
    } finally {
      vi.useRealTimers();
    }
  });
});
