import { RpcClient } from "../ipc/client";
import type { AppSnapshot } from "../ipc/contract.gen";
import { createMockTransport, type MockController, type MockOptions, type ScenarioName } from "../ipc/mock";

/** A real snapshot straight from the mock backend, so tests exercise the same shapes the UI gets. */
export async function snapshotFor(scenario: ScenarioName, opts: MockOptions = {}): Promise<AppSnapshot> {
  const { transport } = createMockTransport({ scenario, latency: 0, freeze: true, ...opts });
  return new RpcClient(transport).call("app.getSnapshot");
}

export function mockClient(scenario: ScenarioName, opts: MockOptions = {}): { client: RpcClient; controller: MockController } {
  const { transport, controller } = createMockTransport({ scenario, latency: 0, ...opts });
  return { client: new RpcClient(transport), controller };
}
