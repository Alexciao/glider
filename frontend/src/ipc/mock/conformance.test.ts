import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AppSnapshot, GameSummary, JobInfo, RpcMethodName, RpcMethods } from "../contract.gen";
import { RpcError } from "../client";
import table from "../../../../tests/conformance/scenarios.json";
import type { MockOptions } from "./options";
import { disposeAll, setup } from "./test-utils";

/**
 * The mock's half of the mock <-> Core conformance suite (review v0.2 M7). tests/conformance/scenarios.json is the shared table; the
 * Core half is tests/Glider.Core.Tests/Contracts/ConformanceTests.cs. Both drive the same JSON requests through their wire
 * protocol and check the same results, queue states and error codes, so a change on one side that the other doesn't follow fails.
 * The table's header describes the steps and the worlds.
 */

interface Then {
  jobs?: string[];
  finished?: string[];
  paused?: boolean;
  reasons?: Record<string, string>;
  cancellable?: Record<string, boolean>;
}
interface Step {
  call?: string;
  params?: unknown;
  error?: string;
  errorTitle?: string;
  notError?: string;
  result?: unknown;
  save?: string | Record<string, string>;
  env?: "failRunning" | "eglBusy" | "startGame" | "connectivity" | "setting";
  code?: string;
  game?: string;
  busy?: boolean;
  value?: string;
  whilePlaying?: string;
  then?: Then;
}
interface Scenario {
  name: string;
  world: "library" | "egl" | "onboarding";
  steps: Step[];
}
const scenarios = (table as unknown as { scenarios: Scenario[] }).scenarios;

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  disposeAll();
  vi.useRealTimers();
});

// Jobs must stay where a step leaves them: frozen simulation (and a tiny time scale where Play needs the clock to run) keeps the
// simulated jobs from finishing on their own, as Core's held jobs do.
const mockOptions = (s: Scenario): MockOptions => {
  const plays = s.steps.some((x) => x.env === "startGame");
  const base: Partial<Record<Scenario["world"], MockOptions>> = {
    library: { scenario: "returning" },
    egl: { scenario: "firstRunSignedIn" },
    onboarding: { scenario: "firstRun" },
  };
  return { latency: 0, timeScale: 0.001, freeze: !plays, ...base[s.world] };
};

const UNFINISHED = ["queued", "running", "paused", "waiting"];

type Rpc = <M extends RpcMethodName>(method: M, ...args: RpcMethods[M]["params"] extends void ? [] : [params: RpcMethods[M]["params"]]) => Promise<RpcMethods[M]["result"]>;

/** Drive the fake clock in small steps until the promise settles (a call costs a few ms of simulated time, not a tick). */
async function quick<T>(p: Promise<T>, maxMs = 60_000): Promise<T> {
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
  for (let t = 0; !done && t <= maxMs; t += 5) await vi.advanceTimersByTimeAsync(5);
  if (!done) throw new Error(`call did not settle within ${maxMs} ms of fake time`);
  if (error !== undefined) throw error;
  return value as T;
}

async function pickRoles(rpc: Rpc): Promise<Record<string, string>> {
  const lib = (await rpc("library.get")).upserts;
  const games = lib.filter((g) => g.kind === "game" && !g.hidden && g.thirdParty === "none");
  const idle = (g: GameSummary) => g.runState === "idle" && g.activeJobId === null;
  const installed = games.filter((g) => g.installStatus === "installed" && idle(g) && g.saveStatus !== "conflict");
  const dlcOf = (g: GameSummary) => lib.find((d) => d.kind === "dlc" && d.baseAppName === g.appName && d.installStatus === "notInstalled");
  const hasInstalledDlc = (g: GameSummary) => lib.some((d) => d.kind === "dlc" && d.baseAppName === g.appName && d.installStatus === "installed");
  const a = installed.find((g) => g.canRunOffline && dlcOf(g) !== undefined && !hasInstalledDlc(g));
  const b = installed.find((g) => g.appName !== a?.appName && g.saveStatus === "notSupported" && g.dlcCount === 0);
  const fresh = games
    .filter((g) => g.installStatus === "notInstalled" && g.dlcCount === 0)
    .sort((p, q) => (p.installSizeBytes ?? Infinity) - (q.installSizeBytes ?? Infinity));
  if (!a || !b || fresh.length < 4) throw new Error("the mock's library doesn't have the games the conformance world needs");
  const [c, d, e, f] = fresh;
  return { a: a.appName, b: b.appName, c: c!.appName, d: d!.appName, e: e!.appName, f: f!.appName, x: dlcOf(a)!.appName };
}

const get = (value: unknown, path: string): unknown => path.split(".").reduce<unknown>((v, k) => (v as Record<string, unknown> | undefined)?.[k], value);

/** Subset match: objects by key, arrays by length and element, {"$in": [...]} = any of, {"$same": label} = a saved value. */
function mismatch(expected: unknown, actual: unknown, saved: Map<string, string>, path = "result"): string | null {
  if (expected !== null && typeof expected === "object" && !Array.isArray(expected)) {
    const o = expected as Record<string, unknown>;
    if ("$in" in o) return (o.$in as unknown[]).includes(actual) ? null : `${path}: ${JSON.stringify(actual)} is none of ${JSON.stringify(o.$in)}`;
    if ("$same" in o) return saved.get(o.$same as string) === actual ? null : `${path}: ${JSON.stringify(actual)} is not the saved ${String(o.$same)}`;
    if (actual === null || typeof actual !== "object") return `${path}: expected an object, got ${JSON.stringify(actual)}`;
    for (const [k, v] of Object.entries(o)) {
      const m = mismatch(v, (actual as Record<string, unknown>)[k], saved, `${path}.${k}`);
      if (m) return m;
    }
    return null;
  }
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual) || actual.length !== expected.length) return `${path}: expected ${expected.length} items, got ${JSON.stringify(actual)}`;
    for (let i = 0; i < expected.length; i++) {
      const m = mismatch(expected[i], actual[i], saved, `${path}[${i}]`);
      if (m) return m;
    }
    return null;
  }
  return expected === actual ? null : `${path}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`;
}

async function run(s: Scenario): Promise<void> {
  const h = setup(mockOptions(s));
  const rpc: Rpc = (method, ...args) => quick((h.client.call as (m: string, ...a: unknown[]) => Promise<never>)(method, ...args));
  const snapshot = (): Promise<AppSnapshot> => rpc("app.getSnapshot");
  const roles = s.world === "library" ? await pickRoles(rpc) : {};
  const saved = new Map<string, string>();
  const labelOf = (id: string): string => [...saved].find(([, v]) => v === id)?.[0] ?? "?";
  const label = (j: JobInfo): string => `${labelOf(j.id)}:${j.status}`;

  const folderOf = async (role: string): Promise<string> => {
    const lib = (await rpc("library.get")).upserts;
    const path = lib.find((g) => g.appName === roles[role])?.installPath;
    if (!path) throw new Error(`role ${role} has no install path`);
    return path;
  };

  async function resolve(v: unknown): Promise<unknown> {
    if (typeof v === "string") {
      let m: RegExpExecArray | null;
      if ((m = /^@(\w+)$/.exec(v))) {
        if (m[1] === "base") {
          const drives = (await snapshot()).storage.drives;
          const best = [...drives].sort((p, q) => q.freeBytes - p.freeBytes)[0]!;
          return `${best.root}Games`;
        }
        return roles[m[1]!] ?? v;
      }
      if ((m = /^@(parent|inside|other)\((\w+)\)$/.exec(v))) {
        const path = await folderOf(m[2]!);
        if (m[1] === "parent") return path.replace(/\\[^\\]*$/, "");
        if (m[1] === "inside") return `${path}\\inside`;
        return "Q:\\Moved";
      }
      if ((m = /^@egl:(\w+)$/.exec(v))) {
        const hit = (await snapshot()).egl.installs.find((i) => i.status === m![1]);
        if (!hit) throw new Error(`the mock has no ${m[1]} EGL install`);
        return hit.id;
      }
      if ((m = /^\$(\w+)$/.exec(v))) {
        const id = saved.get(m[1]!);
        if (id === undefined) throw new Error(`no saved value ${m[1]}`);
        return id;
      }
      return v;
    }
    if (Array.isArray(v)) return Promise.all(v.map(resolve));
    if (v !== null && typeof v === "object") {
      return Object.fromEntries(await Promise.all(Object.entries(v).map(async ([k, x]) => [k, await resolve(x)] as const)));
    }
    return v;
  }

  /** The lane runs on its own: look until the expectation holds (fake time), then report what was seen last. */
  async function check(t: Then, where: string): Promise<void> {
    let last = "";
    for (let waited = 0; waited <= 30_000; waited += 100) {
      const snap: AppSnapshot = await snapshot();
      const unfinished = snap.queue.jobs.filter((j) => UNFINISHED.includes(j.status));
      // The mock's scenarios come with history; only the jobs this scenario made count.
      const finished = snap.queue.jobs.filter((j) => !UNFINISHED.includes(j.status) && labelOf(j.id) !== "?");
      const problems: string[] = [];
      if (t.jobs && JSON.stringify(unfinished.map(label)) !== JSON.stringify(t.jobs)) problems.push(`jobs ${JSON.stringify(unfinished.map(label))} != ${JSON.stringify(t.jobs)}`);
      if (t.finished && JSON.stringify(finished.map(label).sort()) !== JSON.stringify([...t.finished].sort())) problems.push(`finished ${JSON.stringify(finished.map(label))} != ${JSON.stringify(t.finished)}`);
      if (t.paused !== undefined && snap.queue.paused !== t.paused) problems.push(`paused ${snap.queue.paused} != ${t.paused}`);
      for (const [l, text] of Object.entries(t.reasons ?? {})) {
        const j = snap.queue.jobs.find((x) => x.id === saved.get(l));
        if (!j?.blockedReason?.includes(text)) problems.push(`${l}'s reason ${JSON.stringify(j?.blockedReason)} lacks ${JSON.stringify(text)}`);
      }
      for (const [l, want] of Object.entries(t.cancellable ?? {})) {
        const j = snap.queue.jobs.find((x) => x.id === saved.get(l));
        if (j?.cancellable !== want) problems.push(`${l}.cancellable ${String(j?.cancellable)} != ${want}`);
      }
      if (problems.length === 0) return;
      last = problems.join("; ");
      await h.advance(100);
    }
    throw new Error(`${where}: ${last}`);
  }

  for (const [i, step] of s.steps.entries()) {
    const where = `step ${i + 1} (${step.call ?? step.env})`;
    if (step.env) {
      switch (step.env) {
        case "failRunning":
          h.controller.failActiveJob((step.code ?? "network") as never);
          break;
        case "eglBusy":
          h.controller.setEglBusy(step.busy === true, roles[step.game!.replace(/^@/, "")]);
          break;
        case "startGame":
          await rpc("game.launch", { appName: roles[step.game!.replace(/^@/, "")]!, mode: null, skipSaveSync: true });
          break;
        case "connectivity":
          h.controller.setConnectivity(step.value as "online" | "offline");
          break;
        case "setting": {
          const settings = (await snapshot()).settings;
          await rpc("settings.update", { ...settings, whilePlaying: step.whilePlaying as typeof settings.whilePlaying });
          break;
        }
      }
    } else {
      const params = await resolve(step.params ?? null);
      let result: unknown;
      let error: RpcError | null = null;
      try {
        result = await quick((h.client.call as (m: string, ...a: unknown[]) => Promise<unknown>)(step.call!, ...(params === null ? [] : [params])));
      } catch (e) {
        if (!(e instanceof RpcError)) throw e;
        error = e;
      }
      if (step.error) {
        expect(error?.error.code, `${where} should fail with ${step.error} (got ${JSON.stringify(result)})`).toBe(step.error);
        if (step.errorTitle) expect(error!.error.title, where).toBe(step.errorTitle);
      } else if (step.notError) {
        expect(error?.error.code, `${where} must not fail with ${step.notError}`).not.toBe(step.notError);
      } else {
        expect(error, `${where} failed: ${error?.error.code} ${error?.error.detail ?? ""}`).toBeNull();
      }
      if (!error && step.result !== undefined) {
        const m = mismatch(await resolve(step.result), result, saved);
        expect(m, `${where}: ${m}`).toBeNull();
      }
      if (!error && step.save) {
        const paths = typeof step.save === "string" ? { [step.save]: "id" } : step.save;
        for (const [l, p] of Object.entries(paths)) saved.set(l, String(get(result, p)));
      }
    }
    if (step.then) await check(step.then, where);
  }
}

describe("mock <-> Core conformance (tests/conformance/scenarios.json)", () => {
  it("the table has scenarios", () => {
    expect(scenarios.length).toBeGreaterThan(10);
  });
  it.each(scenarios.map((s) => [s.name, s] as const))("%s", async (_name, s) => {
    await run(s);
  });
});
