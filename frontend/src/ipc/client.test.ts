import { describe, expect, it, vi } from "vitest";
import { RpcClient, RpcError, type Transport } from "./client";

/** A transport whose outbound messages are recorded and whose inbound messages the test injects. */
function fakeTransport() {
  const sent: Array<{ type: string; id: number; method: string }> = [];
  let handler: (m: unknown) => void = () => undefined;
  const transport: Transport = {
    send: (m) => void sent.push(m as (typeof sent)[number]),
    subscribe: (h) => {
      handler = h;
      return () => undefined;
    },
  };
  return { transport, sent, deliver: (m: unknown) => handler(m) };
}

/** The id of the nth request sent (fails loudly if there is none). */
const idOf = (sent: Array<{ id: number }>, n = 0): number => {
  const m = sent[n];
  if (!m) throw new Error(`request #${n} was not sent`);
  return m.id;
};

const ok = (id: number, result: unknown) => ({ type: "response", id, result, error: null });

describe("RpcClient request ids", () => {
  it("resolves a call with the response carrying its id", async () => {
    const t = fakeTransport();
    const client = new RpcClient(t.transport);
    const p = client.call("app.getSnapshot");
    t.deliver(ok(idOf(t.sent), { hello: 1 }));
    await expect(p).resolves.toEqual({ hello: 1 });
  });

  it("numbers requests consecutively within one page", () => {
    const t = fakeTransport();
    const client = new RpcClient(t.transport);
    void client.call("app.getSnapshot");
    void client.call("library.get");
    expect(idOf(t.sent, 1)).toBe(idOf(t.sent) + 1);
  });

  it("M5: a reloaded page never mistakes an old page's response for its own", async () => {
    // Old page: game.launch (no timeout) is in flight when the renderer dies.
    const oldPage = fakeTransport();
    const oldClient = new RpcClient(oldPage.transport);
    void oldClient.call("game.launch", { appName: "x" } as never).catch(() => undefined);
    const staleId = idOf(oldPage.sent);

    // The page reloads; the new client's first request must not share that id…
    const newPage = fakeTransport();
    const newClient = new RpcClient(newPage.transport);
    const lib = newClient.call("library.get");
    expect(idOf(newPage.sent)).not.toBe(staleId);

    // …so the old LaunchResult that now arrives on the new page resolves nothing.
    let settled = false;
    void lib.then(() => (settled = true), () => (settled = true));
    newPage.deliver(ok(staleId, { ok: true, pid: 1 }));
    await Promise.resolve();
    expect(settled).toBe(false);

    // The real response still works.
    newPage.deliver(ok(idOf(newPage.sent), { revision: 3 }));
    await expect(lib).resolves.toEqual({ revision: 3 });
  });

  it("starts every page's ids at a different (random) base and keeps them valid int32 (the host reads GetInt32)", () => {
    const bases = new Set<number>();
    for (let i = 0; i < 40; i++) {
      const t = fakeTransport();
      const client = new RpcClient(t.transport);
      void client.call("app.getSnapshot");
      const id = idOf(t.sent);
      expect(Number.isInteger(id)).toBe(true);
      expect(id).toBeGreaterThanOrEqual(1);
      expect(id).toBeLessThan(2 ** 31 - 2 ** 20); // room for a million requests in one page
      bases.add(id);
    }
    expect(bases.size).toBeGreaterThan(30);
  });

  it("ignores a response with an unknown id and an event for nobody", () => {
    const t = fakeTransport();
    const client = new RpcClient(t.transport);
    const seen = vi.fn();
    client.on("notice", seen);
    expect(() => t.deliver(ok(123, null))).not.toThrow();
    t.deliver({ type: "event", event: "notice", data: { id: "n" } });
    expect(seen).toHaveBeenCalledTimes(1);
  });

  it("rejects with the backend's GliderError", async () => {
    const t = fakeTransport();
    const client = new RpcClient(t.transport);
    const p = client.call("app.getSnapshot");
    const error = { code: "unknown", title: "Glider couldn't start", message: "boom", fix: "openLogs", detail: null, appName: null };
    t.deliver({ type: "response", id: idOf(t.sent), result: null, error });
    await expect(p).rejects.toMatchObject({ name: "RpcError", error: { title: "Glider couldn't start" } });
    await p.catch((e) => expect(e).toBeInstanceOf(RpcError));
  });
});
