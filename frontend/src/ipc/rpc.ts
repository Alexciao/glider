import { RpcClient, createWebViewTransport, getWebView, isHosted } from "./client";
import type { RpcMethodName, RpcMethods } from "./contract.gen";
import type { MockController } from "./mock";

/**
 * Process-wide RPC client. Inside the Glider host it talks to WebView2; everywhere else (dev,
 * tests, screenshots) the mock backend is loaded on demand so it never touches the hosted bundle.
 */
let client: RpcClient | null = null;
let mock: MockController | null = null;

export function rpc(): RpcClient {
  if (!client) throw new Error("RPC not connected: call connectRpc() first");
  return client;
}

/** Present only in mock mode (dev scenario switcher). */
export function mockController(): MockController | null {
  return mock;
}

/** Tell the native frame which theme the user picked, so Mica and the window chrome tint to match. Host only. */
export function postHostTheme(theme: "dark" | "light" | "system"): void {
  if (client && isHosted()) client.postHost("theme", { theme });
}

export async function connectRpc(search: string = typeof location === "undefined" ? "" : location.search): Promise<void> {
  if (client) return;
  const webview = getWebView();
  if (isHosted() && webview) {
    client = new RpcClient(createWebViewTransport(webview));
    return;
  }
  const m = await import("./mock");
  const created = m.createMockTransport(m.parseMockOptions(search));
  mock = created.controller;
  client = new RpcClient(created.transport);
  // Dev-only data stress (?data=worst|empty|one|huge) is wired before the first snapshot arrives.
  void (await import("../dev/stress")).installStress(search);
}

/** Test seam: install a prepared client. */
export function setRpcForTests(c: RpcClient | null, controller: MockController | null = null): void {
  client = c;
  mock = controller;
}

export type Params<M extends RpcMethodName> = RpcMethods[M]["params"];
