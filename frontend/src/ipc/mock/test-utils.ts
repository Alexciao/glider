import { vi } from "vitest";
import { RpcClient, RpcError } from "../client";
import type { AppSnapshot, JobInfo, JobProgress, LibraryDelta, Notice, RpcMethodName, RpcMethods, StoreState, WindowInfo } from "../contract.gen";
import { createMockTransport } from "./index";
import type { MockController, MockOptions } from "./options";

type ParamsArg<M extends RpcMethodName> = RpcMethods[M]["params"] extends void ? [] : [params: RpcMethods[M]["params"]];

const live: MockController[] = [];

/** Call inside a test that has `vi.useFakeTimers()` active. */
export function setup(opts: MockOptions = {}) {
  const { transport, controller } = createMockTransport({ latency: 0, ...opts });
  live.push(controller);
  const client = new RpcClient(transport);
  const states: AppSnapshot[] = [];
  const progress: JobProgress[] = [];
  const notices: Notice[] = [];
  const windows: WindowInfo[] = [];
  const deltas: LibraryDelta[] = [];
  const stores: StoreState[] = [];
  client.on("state", (s) => states.push(s));
  client.on("progress", (p) => progress.push(p));
  client.on("notice", (n) => notices.push(n));
  client.on("window", (w) => windows.push(w));
  client.on("library", (d) => deltas.push(d));
  client.on("store", (s) => stores.push(s));

  const advance = async (ms: number): Promise<void> => {
    await vi.advanceTimersByTimeAsync(ms);
  };

  /** Drive the fake clock until the promise settles (or fail after `maxMs` of fake time). */
  async function settle<T>(p: Promise<T>, maxMs = 60_000): Promise<T> {
    let done = false;
    let value: T | undefined;
    let error: unknown;
    p.then(
      (v) => {
        done = true;
        value = v;
      },
      (e: unknown) => {
        done = true;
        error = e;
      },
    );
    for (let t = 0; !done && t <= maxMs; t += 25) await vi.advanceTimersByTimeAsync(25);
    if (!done) throw new Error(`call did not settle within ${maxMs} ms of fake time`);
    if (error !== undefined) throw error;
    return value as T;
  }

  /** Call and settle in one step. */
  const call = <M extends RpcMethodName>(method: M, ...args: ParamsArg<M>): Promise<RpcMethods[M]["result"]> =>
    settle(client.call(method, ...args));

  /** Call and return the RpcError it rejects with. */
  async function callError<M extends RpcMethodName>(method: M, ...args: ParamsArg<M>): Promise<RpcError> {
    try {
      await call(method, ...args);
    } catch (e) {
      if (e instanceof RpcError) return e;
      throw e;
    }
    throw new Error(`${method} unexpectedly succeeded`);
  }

  const snapshot = (): Promise<AppSnapshot> => call("app.getSnapshot");
  const job = async (id: string): Promise<JobInfo | undefined> => (await snapshot()).queue.jobs.find((j) => j.id === id);

  return { client, controller, states, progress, notices, windows, deltas, stores, advance, settle, call, callError, snapshot, job };
}

export function disposeAll(): void {
  for (const c of live.splice(0)) c.dispose();
}
