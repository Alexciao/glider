import { afterEach, describe, expect, it } from "vitest";
import { waitFor } from "@testing-library/react";
import { RpcClient, type Transport } from "../ipc/client";
import type { GameSummary, LibraryDelta } from "../ipc/contract.gen";
import { setRpcForTests } from "../ipc/rpc";
import { worldFor } from "../test/fixtures";
import { detectReadyMoment, startBridge } from "./bridge";
import { useLibrary } from "./library";
import { useRouter } from "./router";
import { useApp } from "./store";
import { snapshotFor } from "../test/fixtures";

/** A scripted host: answers the boot calls and lets the test push events. */
function fakeHost(snapshot: unknown, library: LibraryDelta, onGetLibrary?: () => LibraryDelta) {
  const sent: unknown[] = [];
  let handler: ((m: unknown) => void) | null = null;
  const transport: Transport = {
    send(message) {
      sent.push(message);
      const m = message as { type: string; id?: number; method?: string };
      if (m.type !== "request") return;
      const result =
        m.method === "app.getSnapshot" ? snapshot : m.method === "library.get" ? (onGetLibrary ? onGetLibrary() : library) : { maximized: false, focused: true, visible: true };
      queueMicrotask(() => handler?.({ type: "response", id: m.id, result, error: null }));
    },
    subscribe(h) {
      handler = h;
      return () => (handler = null);
    },
  };
  return { transport, sent, push: (event: string, data: unknown) => handler?.({ type: "event", event, data }) };
}

const emptyLib: LibraryDelta = { revision: 1, reset: true, upserts: [], removes: [] };

let stop: (() => void) | undefined;
afterEach(() => {
  stop?.();
  setRpcForTests(null);
  useApp.setState({ snapshot: null, maxButton: "idle" });
  useLibrary.getState().reset();
  useRouter.setState({ route: { page: "home" }, previous: null });
});

describe("host events", () => {
  it("drives the maximize button's hover/pressed state from the caption event", async () => {
    const host = fakeHost(await snapshotFor("returning"), emptyLib);
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();

    expect(useApp.getState().maxButton).toBe("idle");
    host.push("caption", { button: "max", state: "hover" });
    expect(useApp.getState().maxButton).toBe("hover");
    host.push("caption", { button: "max", state: "down" });
    expect(useApp.getState().maxButton).toBe("down");
    host.push("caption", { button: "max", state: "leave" });
    expect(useApp.getState().maxButton).toBe("idle");
  });

  it("ignores caption events for other buttons", async () => {
    const host = fakeHost(await snapshotFor("returning"), emptyLib);
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    host.push("caption", { button: "close", state: "hover" });
    expect(useApp.getState().maxButton).toBe("idle");
  });

  it("mirrors window state and replaces snapshots pushed by the host", async () => {
    const host = fakeHost(await snapshotFor("returning"), emptyLib);
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    host.push("window", { maximized: true, focused: false, visible: true });
    expect(useApp.getState().window).toEqual({ maximized: true, focused: false, visible: true });

    const next = await snapshotFor("playing");
    host.push("state", next);
    expect(useApp.getState().snapshot?.sessions[0]?.state).toBe("running");
  });

  it("handles the navigate event by changing the route", async () => {
    const host = fakeHost(await snapshotFor("returning"), emptyLib);
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    host.push("navigate", { route: "game/Fortnite" });
    expect(useRouter.getState().route).toEqual({ page: "game", appName: "Fortnite" });
    host.push("navigate", { route: "downloads" });
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
    host.push("navigate", { route: "nonsense/route" }); // unknown routes are ignored
    expect(useRouter.getState().route).toEqual({ page: "downloads" });
  });

  it("posts raw host messages with `type` as the first key", () => {
    const host = fakeHost(null, emptyLib);
    const client = new RpcClient(host.transport);
    client.postHost("theme", { theme: "light" });
    expect(Object.keys(host.sent[0] as object)[0]).toBe("type");
    expect(host.sent[0]).toEqual({ type: "host", op: "theme", theme: "light" });
  });
});

describe("library events", () => {
  it("loads the library at boot and applies deltas in order", async () => {
    const w = await worldFor("returning");
    const full: LibraryDelta = { revision: 20, reset: true, upserts: [...w.games.values()], removes: [] };
    const host = fakeHost(w.snap, full);
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    expect(useLibrary.getState()).toMatchObject({ loaded: true, revision: 20 });
    const g = w.find((x) => x.kind === "game" && !x.favorite);
    host.push("library", { revision: 21, reset: false, upserts: [{ ...g, favorite: true }], removes: [] } satisfies LibraryDelta);
    expect(useLibrary.getState().games.get(g.appName)!.favorite).toBe(true);
    expect(useLibrary.getState().revision).toBe(21);
  });

  it("buffers deltas that arrive during the first load and replays those newer than it", async () => {
    const w = await worldFor("returning");
    const list = [...w.games.values()];
    const g = list.find((x) => x.kind === "game" && !x.favorite) as GameSummary;
    const full: LibraryDelta = { revision: 5, reset: true, upserts: list, removes: [] };
    // The host pushes revisions 4 (older than the load: dropped) and 6 (newer: replayed) before library.get answers.
    let host!: ReturnType<typeof fakeHost>;
    host = fakeHost(w.snap, full, () => {
      host.push("library", { revision: 4, reset: false, upserts: [{ ...g, title: "OLD" }], removes: [] } satisfies LibraryDelta);
      host.push("library", { revision: 6, reset: false, upserts: [{ ...g, favorite: true }], removes: [] } satisfies LibraryDelta);
      return full;
    });
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    expect(useLibrary.getState().revision).toBe(6);
    expect(useLibrary.getState().games.get(g.appName)).toMatchObject({ favorite: true, title: g.title });
  });

  it("refetches the library when a delta leaves a gap", async () => {
    const w = await worldFor("returning");
    const list = [...w.games.values()];
    const g = list.find((x) => x.kind === "game" && !x.favorite) as GameSummary;
    let calls = 0;
    const host = fakeHost(w.snap, { revision: 1, reset: true, upserts: list, removes: [] }, () => {
      calls++;
      return calls === 1
        ? { revision: 1, reset: true, upserts: list, removes: [] }
        : { revision: 9, reset: true, upserts: list.map((x) => (x.appName === g.appName ? { ...x, favorite: true } : x)), removes: [] };
    });
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    expect(calls).toBe(1);
    host.push("library", { revision: 7, reset: false, upserts: [], removes: [] } satisfies LibraryDelta); // 2..6 were lost
    await waitFor(() => expect(useLibrary.getState().revision).toBe(9));
    expect(calls).toBe(2);
    expect(useLibrary.getState().games.get(g.appName)!.favorite).toBe(true);
  });
});

describe("detectReadyMoment", () => {
  it("returns the game whose install/update/import was watched completing; nothing for old news", async () => {
    const prev = await snapshotFor("downloading");
    const job = prev.queue.jobs.find((j) => j.status === "running")!;
    const next = { ...prev, queue: { ...prev.queue, jobs: prev.queue.jobs.map((j) => (j.id === job.id ? { ...j, status: "completed" as const } : j)) } };
    expect(detectReadyMoment(prev, next)).toBe(job.appName);
    expect(detectReadyMoment(null, next)).toBeNull();
    expect(detectReadyMoment(next, next)).toBeNull();
    const verify = { ...next, queue: { ...next.queue, jobs: next.queue.jobs.map((j) => (j.id === job.id ? { ...j, kind: "verify" as const } : j)) } };
    expect(detectReadyMoment(prev, verify)).toBeNull();
  });
});
