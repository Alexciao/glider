import { afterEach, describe, expect, it } from "vitest";
import { RpcClient, type Transport } from "../ipc/client";
import { setRpcForTests } from "../ipc/rpc";
import { snapshotFor } from "../test/fixtures";
import { startBridge } from "./bridge";
import { useApp } from "./store";

/** A scripted host: answers the two boot calls and lets the test push events. */
function fakeHost(snapshot: unknown) {
  const sent: unknown[] = [];
  let handler: ((m: unknown) => void) | null = null;
  const transport: Transport = {
    send(message) {
      sent.push(message);
      const m = message as { type: string; id?: number; method?: string };
      if (m.type !== "request") return;
      const result = m.method === "app.getSnapshot" ? snapshot : { maximized: false, focused: true };
      queueMicrotask(() => handler?.({ type: "response", id: m.id, result, error: null }));
    },
    subscribe(h) {
      handler = h;
      return () => (handler = null);
    },
  };
  return { transport, sent, push: (event: string, data: unknown) => handler?.({ type: "event", event, data }) };
}

let stop: (() => void) | undefined;
afterEach(() => {
  stop?.();
  setRpcForTests(null);
  useApp.setState({ snapshot: null, maxButton: "idle" });
});

describe("host events", () => {
  it("drives the maximize button's hover/pressed state from the caption event", async () => {
    const host = fakeHost(await snapshotFor("installed"));
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
    const host = fakeHost(await snapshotFor("installed"));
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    host.push("caption", { button: "close", state: "hover" });
    expect(useApp.getState().maxButton).toBe("idle");
  });

  it("mirrors window state and replaces snapshots pushed by the host", async () => {
    const host = fakeHost(await snapshotFor("installed"));
    setRpcForTests(new RpcClient(host.transport));
    stop = await startBridge();
    host.push("window", { maximized: true, focused: false });
    expect(useApp.getState().window).toEqual({ maximized: true, focused: false });

    const next = await snapshotFor("playing");
    host.push("state", next);
    expect(useApp.getState().snapshot?.game.runState).toBe("running");
  });

  it("posts raw host messages with `type` as the first key", () => {
    const host = fakeHost(null);
    const client = new RpcClient(host.transport);
    client.postHost("theme", { theme: "light" });
    expect(Object.keys(host.sent[0] as object)[0]).toBe("type");
    expect(host.sent[0]).toEqual({ type: "host", op: "theme", theme: "light" });
  });
});
