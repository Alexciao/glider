import { RpcClient } from "../ipc/client";
import type { AppSnapshot, GameSummary } from "../ipc/contract.gen";
import { createMockTransport, type MockController, type MockOptions, type ScenarioName } from "../ipc/mock";

/** A real snapshot straight from the mock backend, so tests exercise the same shapes the UI gets. */
export async function snapshotFor(scenario: ScenarioName, opts: MockOptions = {}): Promise<AppSnapshot> {
  const { transport } = createMockTransport({ scenario, latency: 0, freeze: true, ...opts });
  return new RpcClient(transport).call("app.getSnapshot");
}

export interface World {
  snap: AppSnapshot;
  games: Map<string, GameSummary>;
  /** First game matching `pred`; throws when the scenario has none (a test bug). */
  find(pred: (g: GameSummary) => boolean): GameSummary;
  get(appName: string): GameSummary;
}

/** Snapshot + library from the same mock instance. */
export async function worldFor(scenario: ScenarioName, opts: MockOptions = {}): Promise<World> {
  const { transport } = createMockTransport({ scenario, latency: 0, freeze: true, ...opts });
  const client = new RpcClient(transport);
  const [snap, lib] = await Promise.all([client.call("app.getSnapshot"), client.call("library.get")]);
  const games = new Map(lib.upserts.map((g) => [g.appName, g]));
  return {
    snap,
    games,
    find(pred) {
      const g = [...games.values()].find(pred);
      if (!g) throw new Error(`no game in scenario ${scenario} matches`);
      return g;
    },
    get(appName) {
      const g = games.get(appName);
      if (!g) throw new Error(`no game ${appName}`);
      return g;
    },
  };
}

export function mockClient(scenario: ScenarioName, opts: MockOptions = {}): { client: RpcClient; controller: MockController } {
  const { transport, controller } = createMockTransport({ scenario, latency: 0, ...opts });
  return { client: new RpcClient(transport), controller };
}
