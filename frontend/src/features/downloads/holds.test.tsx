import { describe, expect, it, vi } from "vitest";
import { act, screen, waitFor, within } from "@testing-library/react";
import type { JobInfo, QueueState } from "../../ipc/contract.gen";
import { rpc } from "../../ipc/rpc";
import { pushNotice, toast, useNotices } from "../../state/notices";
import { useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { installCleanStores, mount } from "../../test/harness";
import { Notices } from "../../app/Notices";
import { Downloads } from "./Downloads";
import { queueNotice } from "./QueueNotice";

installCleanStores();

const queue = (over: Partial<QueueState> = {}): QueueState => ({ jobs: [], paused: false, effectiveMode: "balanced", modeReason: null, holdReason: null, ...over });
const job = (status: JobInfo["status"]): JobInfo => ({ id: "j", appName: "a", title: "A", kind: "update", status }) as JobInfo;

describe("the queue notice says why nothing is moving", () => {
  it("is silent while nothing holds the queue, or when there is no job to hold", () => {
    expect(queueNotice(queue(), false, job("queued"))).toBeNull();
    expect(queueNotice(queue({ holdReason: "Paused while Fortnite is running" }), false, undefined)).toBeNull();
  });

  it("a game running: the hold's reason, what happens next, and a Change link", () => {
    expect(queueNotice(queue({ holdReason: "Paused while Fortnite is running" }), false, job("queued"))).toEqual({
      lead: "Paused while Fortnite is running.",
      rest: "Glider continues when it closes.",
      change: true,
    });
  });

  it("no connection: the offline words, and no setting to change", () => {
    expect(queueNotice(queue({ holdReason: "Waiting for an internet connection" }), true, job("queued"))).toEqual({
      lead: "You're offline.",
      rest: "Downloads continue when you're back.",
    });
  });

  it("a waiting card already says its own reason, so the page doesn't repeat it", () => {
    expect(queueNotice(queue({ holdReason: "Paused while Fortnite is running" }), false, job("waiting"))).toBeNull();
  });

  it("a paused queue says so and offers Resume all, over any hold", () => {
    expect(queueNotice(queue({ paused: true, holdReason: "Paused while Fortnite is running" }), false, job("queued"))).toEqual({ lead: "Downloads are paused.", resume: true });
  });
});

describe("Downloads while a game runs, and offline", () => {
  it("a queued update under 'While a game is running: Pause' shows the reason and a Change link to that setting", async () => {
    const { user, booted } = await mount("playing", <Downloads />);
    const other = booted.find((g) => g.installStatus === "installed" && g.updateAvailable && g.thirdParty === "none" && g.appName !== "Fortnite");
    await act(async () => {
      await rpc().call("game.update", { appName: other.appName });
    });
    const lead = await screen.findByText("Paused while Fortnite is running.");
    const status = lead.closest("[role='status']") as HTMLElement;
    expect(status).toHaveTextContent("Paused while Fortnite is running. Glider continues when it closes. Change");
    await user.click(within(status).getByRole("button", { name: "Change" }));
    expect(useRouter.getState().route).toEqual({ page: "settings", focus: "whilePlaying" });
  });

  it("offline, a queued install says it will continue when the connection is back (and names no setting)", async () => {
    const { booted } = await mount("offline", <Downloads />);
    const game = booted.find((g) => g.kind === "game" && g.installStatus === "notInstalled" && g.thirdParty === "none");
    await act(async () => {
      await rpc().call("game.install", { appName: game.appName, basePath: "D:\\Games", components: null, dlc: [], priority: false });
    });
    const status = (await screen.findByText("You're offline.")).closest("[role='status']") as HTMLElement;
    expect(status).toHaveTextContent("You're offline. Downloads continue when you're back.");
    expect(within(status).queryByRole("button")).toBeNull();
    expect(useApp.getState().snapshot!.queue.holdReason).toBe("Waiting for an internet connection");
  });
});

describe("free space after coming back from Explorer", () => {
  it("is read on focus and once more right after Core's 10 s drive cache expires", async () => {
    await mount("errors", <Downloads />);
    await screen.findByRole("region", { name: "Couldn't finish" });
    const call = vi.spyOn(rpc(), "call");
    const reads = () => call.mock.calls.filter((c) => c[0] === "app.getSnapshot").length;
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      act(() => void window.dispatchEvent(new Event("focus")));
      expect(reads()).toBe(1);
      await act(async () => {
        await vi.advanceTimersByTimeAsync(9_000);
      });
      expect(reads()).toBe(1); // the cache still holds the old number
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2_000);
      });
      expect(reads()).toBe(2);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("toasts", () => {
  const network = { level: "error" as const, title: "Can't reach Epic", message: "Check your internet connection. Progress is kept.", action: "retry" as const, appName: null };

  it("identical failures merge into one toast that counts them", async () => {
    await mount("returning", <Notices />);
    act(() => {
      for (const id of ["a", "b", "c"]) pushNotice({ ...network, id: `n-${id}`, jobId: `job-${id}` });
    });
    expect(useNotices.getState().items).toHaveLength(1);
    expect(await screen.findByText("Can't reach Epic · 3 downloads")).toBeInTheDocument();
    // A fix button would only fit one of the three.
    expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
  });

  it("different messages stay separate, and local confirmations never merge", async () => {
    await mount("returning", <Notices />);
    act(() => {
      pushNotice({ ...network, id: "n-1", jobId: "j1" });
      pushNotice({ ...network, id: "n-2", jobId: "j2", message: "Something else." });
    });
    expect(useNotices.getState().items.map((t) => t.merged)).toEqual([undefined, undefined]);
    act(() => {
      toast({ level: "success", title: "Copied", duration: 0 });
      toast({ level: "success", title: "Copied", duration: 0 });
    });
    expect(useNotices.getState().items.filter((t) => t.title === "Copied")).toHaveLength(2);
  });

  it("Downloads lists a failure in place, so its toast is not shown there (and is again once the page is left)", async () => {
    const { view } = await mount("returning", <Downloads />);
    act(() => pushNotice({ ...network, id: "n-1", jobId: "j1" }));
    expect(useNotices.getState().items).toHaveLength(0);
    // A notice without a job (a library sync, say) is not about a row on this page.
    act(() => pushNotice({ ...network, id: "n-2", jobId: null, title: "Sync failed" }));
    expect(useNotices.getState().items).toHaveLength(1);
    view.unmount();
    act(() => pushNotice({ ...network, id: "n-3", jobId: "j3" }));
    expect(useNotices.getState().items.map((t) => t.id)).toEqual(["n-2", "n-3"]);
  });

  it("the connection coming back dismisses 'Can't reach Epic'", async () => {
    const { controller } = await mount("returning", <Notices />);
    act(() => controller.setConnectivity("offline"));
    await waitFor(() => expect(useApp.getState().snapshot!.connectivity).toBe("offline"));
    act(() => {
      pushNotice({ ...network, id: "n-1", jobId: "j1" });
      pushNotice({ ...network, id: "n-2", jobId: null, title: "Something else" });
    });
    act(() => controller.setConnectivity("online"));
    await waitFor(() => expect(useApp.getState().snapshot!.connectivity).toBe("online"));
    expect(useNotices.getState().items.map((t) => t.title)).toEqual(["Something else"]);
  });
});
