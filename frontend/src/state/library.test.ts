import { afterEach, beforeEach, describe, expect, it } from "vitest";
import type { GameSummary, LibraryDelta } from "../ipc/contract.gen";
import { setRpcForTests } from "../ipc/rpc";
import { generateCatalog } from "../ipc/mock/catalog";
import { mockClient } from "../test/fixtures";
import { startBridge } from "./bridge";
import { applyLibraryDelta, emptyLibrary, useLibrary, type LibraryState } from "./library";
import { resetProgressForTests } from "./progress";
import { useApp } from "./store";
import { waitFor } from "@testing-library/react";

const NOW = Date.parse("2026-11-14T12:00:00Z");
const games = (n = 20): GameSummary[] => generateCatalog({ seed: 5, count: n, now: NOW, art: false }).map((g) => g.summary);

const reset = (list: GameSummary[], revision = 10): LibraryDelta => ({ revision, reset: true, upserts: list, removes: [] });
const delta = (revision: number, upserts: GameSummary[] = [], removes: string[] = []): LibraryDelta => ({ revision, reset: false, upserts, removes });
const loaded = (list: GameSummary[], revision = 10): LibraryState => applyLibraryDelta(emptyLibrary(), reset(list, revision)).state;

describe("applyLibraryDelta (pure)", () => {
  it("a reset replaces everything and sets the revision, whatever it was", () => {
    const list = games();
    const a = applyLibraryDelta(emptyLibrary(), reset(list, 7));
    expect(a.result).toBe("reset");
    expect(a.state).toMatchObject({ loaded: true, revision: 7 });
    expect(a.state.games.size).toBe(list.length);
    const b = applyLibraryDelta(a.state, reset(list.slice(0, 3), 2)); // even a lower revision (backend restarted)
    expect(b.state.revision).toBe(2);
    expect(b.state.games.size).toBe(3);
  });

  it("a reset keeps the identity of games that did not change", () => {
    const list = games();
    const a = loaded(list);
    const copy = list.map((g) => ({ ...g, art: { ...g.art } }));
    copy[2] = { ...copy[2]!, favorite: !copy[2]!.favorite };
    const b = applyLibraryDelta(a, reset(copy, 11)).state;
    expect(b.games.get(list[0]!.appName)).toBe(a.games.get(list[0]!.appName));
    expect(b.games.get(list[2]!.appName)).not.toBe(a.games.get(list[2]!.appName));
    expect(b.games.get(list[2]!.appName)!.favorite).toBe(!list[2]!.favorite);
  });

  it("applies the next revision: upserts add or replace, removes delete; untouched games keep their object", () => {
    const list = games();
    const a = loaded(list, 10);
    const changed = { ...list[1]!, favorite: !list[1]!.favorite };
    const added = { ...list[0]!, appName: "Brand-New", title: "Brand New" };
    const r = applyLibraryDelta(a, delta(11, [changed, added], [list[3]!.appName]));
    expect(r.result).toBe("applied");
    expect(r.state.revision).toBe(11);
    expect(r.state.games.get(list[1]!.appName)!.favorite).toBe(changed.favorite);
    expect(r.state.games.has("Brand-New")).toBe(true);
    expect(r.state.games.has(list[3]!.appName)).toBe(false);
    expect(r.state.games.get(list[0]!.appName)).toBe(a.games.get(list[0]!.appName));
    expect(r.state.games.size).toBe(list.length);
    expect(a.games.size).toBe(list.length); // the previous state is untouched
    expect(a.games.has(list[3]!.appName)).toBe(true);
  });

  it("an upsert identical to what we have keeps the same object (no pointless re-render)", () => {
    const list = games();
    const a = loaded(list);
    const r = applyLibraryDelta(a, delta(11, [JSON.parse(JSON.stringify(list[4])) as GameSummary]));
    expect(r.state.games.get(list[4]!.appName)).toBe(a.games.get(list[4]!.appName));
  });

  it("ignores a delta that is already applied or older", () => {
    const a = loaded(games(), 10);
    expect(applyLibraryDelta(a, delta(10)).result).toBe("stale");
    expect(applyLibraryDelta(a, delta(4)).result).toBe("stale");
    expect(applyLibraryDelta(a, delta(10)).state).toBe(a);
  });

  it("reports a gap (and leaves the state alone) when a revision was skipped", () => {
    const a = loaded(games(), 10);
    const r = applyLibraryDelta(a, delta(12, [{ ...games()[0]!, favorite: true }]));
    expect(r.result).toBe("gap");
    expect(r.state).toBe(a);
  });

  it("reports a gap for a delta before anything was loaded", () => {
    const r = applyLibraryDelta(emptyLibrary(), delta(3));
    expect(r.result).toBe("gap");
    expect(r.state.loaded).toBe(false);
  });
});

describe("library store", () => {
  beforeEach(() => useLibrary.getState().reset());

  it("applies deltas and exposes the result; patchGame is an optimistic edit the next delta overwrites", () => {
    const list = games();
    const s = useLibrary.getState();
    expect(s.apply(reset(list, 5))).toBe("reset");
    const g = list[0]!;
    s.patchGame(g.appName, { favorite: true });
    expect(useLibrary.getState().games.get(g.appName)!.favorite).toBe(true);
    expect(useLibrary.getState().apply(delta(6, [{ ...g, favorite: false }]))).toBe("applied");
    expect(useLibrary.getState().games.get(g.appName)!.favorite).toBe(false);
    expect(useLibrary.getState().apply(delta(8))).toBe("gap");
    expect(useLibrary.getState().revision).toBe(6);
  });
});

describe("gap recovery against the mock backend", () => {
  let stop: (() => void) | undefined;
  afterEach(() => {
    stop?.();
    setRpcForTests(null);
    resetProgressForTests();
    useApp.setState({ snapshot: null });
  });

  async function boot(scenario: Parameters<typeof mockClient>[0] = "returning") {
    const { client, controller } = mockClient(scenario);
    setRpcForTests(client, controller);
    stop = await startBridge();
    return { client, controller };
  }

  it("loads the whole library at boot, then follows deltas", async () => {
    const { client } = await boot();
    const lib = useLibrary.getState();
    expect(lib.loaded).toBe(true);
    expect(lib.games.size).toBeGreaterThan(330);
    const target = [...lib.games.values()].find((g) => g.kind === "game" && !g.favorite)!;
    await client.call("library.setFavorite", { appName: target.appName, value: true });
    await waitFor(() => expect(useLibrary.getState().games.get(target.appName)!.favorite).toBe(true));
    expect(useLibrary.getState().revision).toBe(useApp.getState().snapshot!.library.revision);
  });

  it("recovers from a dropped delta by refetching with library.get", async () => {
    const { client, controller } = await boot();
    const [a, b] = [...useLibrary.getState().games.values()].filter((g) => g.kind === "game" && !g.favorite);
    controller.dropNextLibraryDelta();
    await client.call("library.setFavorite", { appName: a!.appName, value: true }); // lost on the wire
    await new Promise((r) => setTimeout(r, 20));
    expect(useLibrary.getState().games.get(a!.appName)!.favorite).toBe(false);
    await client.call("library.setFavorite", { appName: b!.appName, value: true }); // arrives with a gap
    await waitFor(() => expect(useLibrary.getState().games.get(a!.appName)!.favorite).toBe(true));
    expect(useLibrary.getState().games.get(b!.appName)!.favorite).toBe(true);
    const server = await client.call("library.get");
    expect(useLibrary.getState().revision).toBe(server.revision);
  });

  it("follows a scenario reload (reset delta, lower or higher revision) and sign-in syncs", async () => {
    const { controller } = await boot("firstRun");
    expect(useLibrary.getState().games.size).toBe(0);
    controller.loadScenario("returning");
    await waitFor(() => expect(useLibrary.getState().games.size).toBeGreaterThan(330));
    controller.loadScenario("emptyLibrary");
    await waitFor(() => expect(useLibrary.getState().games.size).toBe(0));
    expect(useLibrary.getState().loaded).toBe(true);
  });
});
