import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, waitFor } from "@testing-library/react";
import type { StoreBounds } from "../../ipc/contract.gen";
import { rpc, setRpcForTests } from "../../ipc/rpc";
import { startBridge } from "../../state/bridge";
import { useLibrary } from "../../state/library";
import { navigate, storeRoute, useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { mockClient } from "../../test/fixtures";
import { MotionProvider, TooltipProvider } from "../../ui";
import { StorePage } from "./StorePage";
import { roundBounds, sameBounds, useNativeBounds } from "./useNativeBounds";

let stop: (() => void) | undefined;

async function mountStore(hold = false) {
  const { client, controller } = mockClient("returning");
  setRpcForTests(client, controller);
  stop = await startBridge();
  const original = rpc().call.bind(rpc()) as (m: string, p: unknown) => Promise<unknown>;
  const call = vi.spyOn(rpc(), "call");
  let release = () => {};
  if (hold) {
    // The host takes a moment to create the native view: keep store.* pending so the opening state can be seen.
    const gate = new Promise<void>((r) => {
      release = r;
    });
    call.mockImplementation(((method: string, p: unknown) => (method.startsWith("store.") ? gate.then(() => original(method, p)) : original(method, p))) as never);
  }
  const view = render(
    <MotionProvider>
      <TooltipProvider>
        <StorePage />
      </TooltipProvider>
    </MotionProvider>,
  );
  return { call, view, release, original };
}

type Spy = { mock: { calls: unknown[][] } };
const callsTo = (call: Spy, method: string) => call.mock.calls.filter((c) => c[0] === method);

beforeEach(() => {
  useApp.setState({ snapshot: null });
  useLibrary.getState().reset();
  useRouter.setState({ route: { page: "store" }, previous: null });
});
afterEach(() => {
  stop?.();
  setRpcForTests(null);
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
});

describe("bounds", () => {
  it("rounds to whole CSS pixels and compares exactly", () => {
    expect(roundBounds({ left: 72.4, top: 40.6, width: 999.5, height: 700.2 })).toEqual({ x: 72, y: 41, width: 1000, height: 700 });
    expect(roundBounds({ left: 0, top: 0, width: -3, height: 2 })).toEqual({ x: 0, y: 0, width: 0, height: 2 });
    const a: StoreBounds = { x: 1, y: 2, width: 3, height: 4 };
    expect(sameBounds(a, { ...a })).toBe(true);
    expect(sameBounds(a, { ...a, height: 5 })).toBe(false);
    expect(sameBounds(null, a)).toBe(false);
  });

  it("reports resizes once per frame and never repeats an unchanged rectangle", () => {
    let rect = { left: 72, top: 80, width: 900, height: 600 };
    const callbacks: ResizeObserverCallback[] = [];
    vi.stubGlobal(
      "ResizeObserver",
      class {
        constructor(cb: ResizeObserverCallback) {
          callbacks.push(cb);
        }
        observe() {}
        disconnect() {}
        unobserve() {}
      },
    );
    const frames: FrameRequestCallback[] = [];
    vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => frames.push(cb));
    vi.stubGlobal("cancelAnimationFrame", () => {});
    const seen: StoreBounds[] = [];
    const el = document.createElement("div");
    el.getBoundingClientRect = () => ({ ...rect, right: 0, bottom: 0, x: rect.left, y: rect.top, toJSON: () => ({}) }) as DOMRect;
    function Probe() {
      useNativeBounds({ current: el }, (b) => seen.push(b));
      return null;
    }
    render(<Probe />);
    const flush = () => act(() => frames.splice(0).forEach((f) => f(0)));

    flush();
    expect(seen).toEqual([{ x: 72, y: 80, width: 900, height: 600 }]);

    callbacks[0]!([], {} as ResizeObserver); // observer fires, nothing changed
    flush();
    expect(seen).toHaveLength(1);

    rect = { ...rect, width: 700 };
    callbacks[0]!([], {} as ResizeObserver);
    callbacks[0]!([], {} as ResizeObserver); // coalesced
    expect(frames).toHaveLength(1);
    flush();
    expect(seen.at(-1)).toEqual({ x: 72, y: 80, width: 700, height: 600 });
  });
});

describe("StorePage", () => {
  it("shows the native view over the placeholder on enter and hides it on leave", async () => {
    const { call, view } = await mountStore();
    await waitFor(() => expect(callsTo(call, "store.show")).toHaveLength(1));
    expect(callsTo(call, "store.show")[0]![1]).toMatchObject({ target: null, bounds: { x: 0, y: 0 } });

    view.unmount();
    await waitFor(() => expect(callsTo(call, "store.hide")).toHaveLength(1));
  });

  it("opens the typed target from the route and follows route changes without remounting", async () => {
    useRouter.setState({ route: storeRoute({ kind: "product", slug: "knightica-3be45c" }), previous: null });
    const { call } = await mountStore();
    await waitFor(() => expect(callsTo(call, "store.show")).toHaveLength(1));
    expect(callsTo(call, "store.show")[0]![1]).toMatchObject({ target: { kind: "product", slug: "knightica-3be45c" } });

    act(() => navigate(storeRoute({ kind: "freeGames", slug: null })));
    await waitFor(() => expect(callsTo(call, "store.show")).toHaveLength(2));
    expect(callsTo(call, "store.show")[1]![1]).toMatchObject({ target: { kind: "freeGames" } });
  });

  it("draws the opening state, then the host once the page is ready", async () => {
    const { release } = await mountStore(true);
    expect(screen.getByText(/Opening the Epic Games Store/)).toBeInTheDocument();
    release();
    expect(await screen.findByText("store.epicgames.com", {}, { timeout: 4000 })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByText(/Opening the Epic Games Store/)).not.toBeInTheDocument(), { timeout: 4000 });
  });

  it("covers the native view while a dialog is open and uncovers it after", async () => {
    const { call } = await mountStore();
    await waitFor(() => expect(callsTo(call, "store.show")).toHaveLength(1));
    await screen.findByText("store.epicgames.com", {}, { timeout: 4000 });

    const dialog = document.createElement("div");
    dialog.setAttribute("role", "dialog");
    act(() => {
      document.body.appendChild(dialog);
    });
    await waitFor(() => expect(callsTo(call, "store.setCovered").at(-1)![1]).toEqual({ covered: true }));

    act(() => {
      dialog.remove();
    });
    await waitFor(() => expect(callsTo(call, "store.setCovered").at(-1)![1]).toEqual({ covered: false }));
  });

  it("offers retry and the browser when the page can't load", async () => {
    const { call, original } = await mountStore();
    await screen.findByText("store.epicgames.com", {}, { timeout: 4000 });
    call.mockImplementation(((method: string, p: unknown) =>
      method === "store.show" ? Promise.reject(new Error("boom")) : original(method, p)) as never);
    act(() => navigate(storeRoute({ kind: "wishlist", slug: null })));
    expect(await screen.findByText("The Store didn't load")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /Open in browser/ }).length).toBeGreaterThan(0);
  });
});
