import type { Transport } from "../client";
import { MockBackend } from "./backend";
import type { MockController, MockOptions } from "./options";

export type { MockController, MockOptions, ScenarioInfo, ScenarioName } from "./options";
export { SCENARIO_NAMES, parseMockOptions } from "./options";
export { SCENARIOS } from "./scenarios";

/**
 * In-memory implementation of the whole Glider RPC contract (src/ipc/contract.gen.ts), with scenarios,
 * simulated latency, a serial job queue and 10 Hz progress events. Selected when not hosted in WebView2.
 */
export function createMockTransport(opts: MockOptions = {}): { transport: Transport; controller: MockController } {
  const backend = new MockBackend(opts);
  const handlers = new Set<(message: unknown) => void>();
  backend.setOutput((message) => {
    for (const h of [...handlers]) h(message);
  });
  const transport: Transport = {
    send: (message) => backend.handle(message),
    subscribe: (handler) => {
      handlers.add(handler);
      return () => {
        handlers.delete(handler);
      };
    },
  };
  return { transport, controller: backend.createController() };
}
