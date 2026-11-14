import type {
  GliderError,
  RpcEventMap,
  RpcEventName,
  RpcMethodName,
  RpcMethods,
} from "./contract.gen";

/**
 * Typed JSON-RPC client over a message transport.
 *  - Real app: WebView2 (`window.chrome.webview`), messages are already-parsed JSON objects.
 *  - Dev/tests/screenshots: the mock backend (src/ipc/mock), selected when WebView2 is absent.
 */

export interface Transport {
  send(message: unknown): void;
  subscribe(handler: (message: unknown) => void): () => void;
}

/** Rejection type for every failed call. `error` is the backend's human-readable GliderError. */
export class RpcError extends Error {
  readonly error: GliderError;
  constructor(error: GliderError) {
    super(error.message);
    this.name = "RpcError";
    this.error = error;
  }
}

type ParamsArg<M extends RpcMethodName> = RpcMethods[M]["params"] extends void
  ? []
  : [params: RpcMethods[M]["params"]];

type Listener<E extends RpcEventName> = (data: RpcEventMap[E]) => void;

interface Pending {
  resolve: (v: unknown) => void;
  reject: (e: unknown) => void;
  timer: ReturnType<typeof setTimeout> | undefined;
}

/** Methods that legitimately wait on the user (login window, folder dialog, a game starting or a third-party launcher) get no timeout. */
const NO_TIMEOUT = new Set<RpcMethodName>(["auth.signIn", "app.pickFolder", "game.launch", "game.activate", "game.installPrerequisites", "saves.resolve", "saves.sync", "egl.quickScan"]);
const DEFAULT_TIMEOUT_MS = 30_000;

export class RpcClient {
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Map<string, Set<(data: unknown) => void>>();
  private readonly transport: Transport;

  constructor(transport: Transport) {
    this.transport = transport;
    transport.subscribe((m) => this.onMessage(m));
  }

  call<M extends RpcMethodName>(method: M, ...args: ParamsArg<M>): Promise<RpcMethods[M]["result"]> {
    const id = this.nextId++;
    const params = args.length > 0 ? args[0] : null;
    return new Promise((resolve, reject) => {
      const timer = NO_TIMEOUT.has(method)
        ? undefined
        : setTimeout(() => {
            this.pending.delete(id);
            reject(
              new RpcError({
                code: "timeout",
                title: "Timed out",
                message: "Glider's backend didn't respond in time. Try again.",
                fix: "retry",
                detail: method,
                appName: null,
              }),
            );
          }, DEFAULT_TIMEOUT_MS);
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, reject, timer });
      this.transport.send({ type: "request", id, method, params });
    });
  }

  /**
   * Optional raw web→host message outside the RPC protocol (e.g. {type:"host", op:"theme"}).
   * `type` must stay the first key: the host sniffs it before parsing.
   */
  postHost(op: "theme" | "chrome", fields: Record<string, unknown> = {}): void {
    this.transport.send({ type: "host", op, ...fields });
  }

  on<E extends RpcEventName>(event: E, listener: Listener<E>): () => void {
    let set = this.listeners.get(event);
    if (!set) {
      set = new Set();
      this.listeners.set(event, set);
    }
    const l = listener as (data: unknown) => void;
    set.add(l);
    return () => set.delete(l);
  }

  private onMessage(raw: unknown): void {
    const msg = (typeof raw === "string" ? JSON.parse(raw) : raw) as
      | { type: "response"; id: number; result: unknown; error: GliderError | null }
      | { type: "event"; event: string; data: unknown };
    if (msg.type === "response") {
      const p = this.pending.get(msg.id);
      if (!p) return;
      this.pending.delete(msg.id);
      if (p.timer) clearTimeout(p.timer);
      if (msg.error) p.reject(new RpcError(msg.error));
      else p.resolve(msg.result);
    } else if (msg.type === "event") {
      this.listeners.get(msg.event)?.forEach((l) => l(msg.data));
    }
  }
}

interface WebView2 {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (e: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebView2 };
  }
}

export function getWebView(): WebView2 | undefined {
  return typeof window !== "undefined" ? window.chrome?.webview : undefined;
}

export function createWebViewTransport(webview: WebView2): Transport {
  return {
    send: (message) => webview.postMessage(message),
    subscribe: (handler) => {
      const l = (e: { data: unknown }) => handler(e.data);
      webview.addEventListener("message", l);
      return () => webview.removeEventListener("message", l);
    },
  };
}

/** True inside the real Glider host. */
export const isHosted = (): boolean => getWebView() !== undefined;
