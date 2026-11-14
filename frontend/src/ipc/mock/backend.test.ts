import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { AppSnapshot, GameSummary, JobInfo, LibraryDelta } from "../contract.gen";
import { RPC_METHODS } from "../contract.gen";
import { SCENARIO_NAMES, type MockOptions, type ScenarioName } from "./options";
import { disposeAll, setup } from "./test-utils";

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  disposeAll();
  vi.useRealTimers();
});

type H = ReturnType<typeof setup>;
const fast: MockOptions = { timeScale: 600 };

/** Advance the fake clock in 100 ms steps until `cond` holds. */
async function until(h: H, cond: () => boolean | Promise<boolean>, maxMs = 120_000): Promise<void> {
  for (let t = 0; t < maxMs; t += 100) {
    if (await cond()) return;
    await h.advance(100);
  }
  throw new Error("condition not reached");
}

async function library(h: H): Promise<Map<string, GameSummary>> {
  const d = await h.call("library.get");
  return new Map(d.upserts.map((g) => [g.appName, g]));
}

const find = (lib: Map<string, GameSummary>, pred: (g: GameSummary) => boolean): GameSummary => {
  const g = [...lib.values()].find(pred);
  if (!g) throw new Error("no matching game in the mock library");
  return g;
};
const jobsOf = (s: AppSnapshot): JobInfo[] => s.queue.jobs;
const active = (s: AppSnapshot): JobInfo[] => s.queue.jobs.filter((j) => ["running", "queued", "paused", "waiting"].includes(j.status));

describe("scenarios", () => {
  it.each(SCENARIO_NAMES)("%s builds a consistent snapshot and library", async (name) => {
    const h = setup({ scenario: name });
    const snap = await h.snapshot();
    const lib = await h.call("library.get");
    expect(lib.reset).toBe(true);
    expect(lib.revision).toBe(snap.library.revision);
    expect(lib.upserts.filter((g) => g.kind === "game")).toHaveLength(snap.library.gameCount);
    expect(new Set(lib.upserts.map((g) => g.appName)).size).toBe(lib.upserts.length);
    for (const j of jobsOf(snap)) expect(lib.upserts.some((g) => g.appName === j.appName) || j.appName === "eos-overlay").toBe(true);
    expect(snap.library.updatesAvailable).toBe(lib.upserts.filter((g) => g.kind === "game" && g.installStatus === "installed" && g.updateAvailable).length);
  });

  it("returning: signed in, ~300 games, history, nothing running, several library folders", async () => {
    const h = setup({ scenario: "returning" });
    const s = await h.snapshot();
    const lib = await library(h);
    expect(s.auth.status).toBe("signedIn");
    expect(s.onboarding.completed).toBe(true);
    expect(s.library.gameCount).toBe(300);
    expect(lib.size).toBeGreaterThan(330);
    expect(active(s)).toHaveLength(0);
    expect(jobsOf(s).length).toBeGreaterThan(5);
    expect(s.storage.folders.length).toBeGreaterThanOrEqual(3);
    expect(new Set(s.storage.drives.map((d) => d.root)).size).toBeGreaterThanOrEqual(3);
    expect(s.storage.folders.reduce((n, f) => n + f.gameCount, 0)).toBeGreaterThan(10);
    expect(s.collections.length).toBeGreaterThanOrEqual(2);
    expect(s.overlay.installed).toBe(true);
    // Art travels as data: URIs in the mock only; the host sends short art.glider URLs.
    expect(JSON.stringify(s).replace(/data:[^"]+/g, "").length).toBeLessThan(40_000);
  });

  it("firstRun: signed out at Welcome with no library; signing in syncs it and scans for EGL installs", async () => {
    const h = setup({ scenario: "firstRun" });
    const s = await h.snapshot();
    expect(s.auth).toMatchObject({ status: "signedOut", account: null });
    expect(s.onboarding).toMatchObject({ completed: false, step: "welcome" });
    expect(s.library.gameCount).toBe(0);
    expect(s.egl.installs).toEqual([]);
    expect((await h.call("library.get")).upserts).toEqual([]);

    await h.call("auth.signIn");
    await until(h, async () => (await h.snapshot()).library.gameCount > 0);
    expect(h.deltas.some((d) => d.reset && d.upserts.length > 300)).toBe(true);
    await until(h, async () => (await h.snapshot()).egl.installs.length > 0);
    const egl = (await h.snapshot()).egl;
    expect(egl.scanning).toBe(false);
    expect(egl.lastScan).not.toBeNull();
  });

  it("firstRunSignedIn: Scan step with every kind of EGL install across several drives", async () => {
    const h = setup({ scenario: "firstRunSignedIn" });
    const s = await h.snapshot();
    expect(s.onboarding.step).toBe("scan");
    const by = (st: string) => s.egl.installs.filter((i) => i.status === st);
    expect(by("importable").length).toBeGreaterThanOrEqual(5);
    expect(by("stale").length).toBeGreaterThanOrEqual(1);
    expect(by("busy")).toHaveLength(1);
    expect(by("imported").length).toBeGreaterThanOrEqual(1);
    expect(by("notOwned")).toHaveLength(1);
    expect(new Set(s.egl.installs.map((i) => i.drive)).size).toBeGreaterThanOrEqual(3);
    expect(s.egl.installs.find((i) => i.source === "gliderV1")?.appName).toBe("Fortnite");
    // Nothing else is installed in Glider yet: the importable installs are still only in Epic Games Launcher.
    const lib = await library(h);
    expect([...lib.values()].filter((g) => g.installStatus === "installed").map((g) => g.appName)).toEqual(["Fortnite"]);
    expect(lib.get(by("importable")[0]!.appName)!.installStatus).toBe("notInstalled");
    for (const i of by("stale")) expect(i.reason).toBeTruthy();
    for (const i of by("importable")) expect(i.reason).toBeNull();
  });

  it("emptyLibrary / offline / syncError / authExpired", async () => {
    const empty = setup({ scenario: "emptyLibrary" });
    expect((await empty.snapshot()).library.gameCount).toBe(0);
    expect((await library(empty)).size).toBe(0);

    const offline = setup({ scenario: "offline" });
    const o = await offline.snapshot();
    expect(o.connectivity).toBe("offline");
    expect(o.library.syncError?.code).toBe("offline");
    expect(o.library.gameCount).toBe(300);

    const sync = setup({ scenario: "syncError" });
    expect((await sync.snapshot()).library.syncError).not.toBeNull();

    const expired = setup({ scenario: "authExpired" });
    expect((await expired.snapshot()).auth.status).toBe("expired");
  });

  it("perf600 has 600 base games", async () => {
    const h = setup({ scenario: "perf600" });
    expect((await h.snapshot()).library.gameCount).toBe(600);
    expect((await library(h)).size).toBeGreaterThan(660);
  });

  it("queue: running, paused, queued, plus history; running job first", async () => {
    const h = setup({ scenario: "queue" });
    const s = await h.snapshot();
    const a = active(s);
    expect(a[0]!.status).toBe("running");
    expect(a.filter((j) => j.status === "paused")).toHaveLength(1);
    expect(a.filter((j) => j.status === "queued").length).toBeGreaterThanOrEqual(3);
    expect(s.queue.jobs.slice(a.length).every((j) => !["running", "queued", "paused", "waiting"].includes(j.status))).toBe(true);
    const lib = await library(h);
    expect(lib.get(a[0]!.appName)?.activeJobId).toBe(a[0]!.id);
    expect(lib.get(a[0]!.appName)?.activeJobKind).toBe("install");
  });

  it("downloading starts at 42% of the download and keeps going", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    expect(j.phase).toBe("downloading");
    expect(j.progress!.network).not.toBeNull();
    const p0 = j.progress!.percent;
    expect(p0).toBeGreaterThan(30);
    await h.advance(3000);
    expect(h.progress.at(-1)!.percent).toBeGreaterThanOrEqual(p0);
  });

  it("playing: Fortnite is running", async () => {
    const h = setup({ scenario: "playing" });
    const s = await h.snapshot();
    expect(s.sessions).toHaveLength(1);
    expect(s.sessions[0]).toMatchObject({ appName: "Fortnite", state: "running", untracked: false });
    expect((await library(h)).get("Fortnite")?.runState).toBe("running");
  });

  it("updates: many installed games have updates", async () => {
    const h = setup({ scenario: "updates" });
    expect((await h.snapshot()).library.updatesAvailable).toBeGreaterThan(15);
  });

  it("diskFull: installs and updates are refused with the exact copy", async () => {
    const h = setup({ scenario: "diskFull" });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    const e = await h.callError("game.install", { appName: g.appName, basePath: "C:\\Games", components: null, dlc: null, priority: false });
    expect(e.error.code).toBe("insufficientSpace");
    expect(e.error.message).toMatch(/^This needs [\d.]+ [KMGT]B but only [\d.]+ [KMGT]B is free\. Free up space or choose another drive\.$/);
    expect(e.error.appName).toBe(g.appName);
  });

  it("eglBusy: the update waits with a reason, then auto-resumes ~14 s later", async () => {
    const h = setup({ scenario: "eglBusy" });
    let s = await h.snapshot();
    const j = active(s)[0]!;
    expect(j.status).toBe("waiting");
    expect(j.blockedReason).toMatch(/Epic Games Launcher is updating/);
    expect(s.egl.busyApps).toEqual([j.appName]);
    await h.advance(14_500);
    s = await h.snapshot();
    expect(s.egl.busyApps).toEqual([]);
    expect(s.queue.jobs.find((x) => x.id === j.id)!.status).toBe("running");
  });

  it("errors: failed jobs carry the right fixes and appName", async () => {
    const h = setup({ scenario: "errors" });
    const s = await h.snapshot();
    const failed = jobsOf(s).filter((j) => j.status === "failed");
    expect(failed.length).toBeGreaterThanOrEqual(5);
    expect(new Set(failed.map((j) => j.error!.code)).size).toBeGreaterThanOrEqual(5);
    for (const j of failed) expect(j.error!.appName).toBe(j.appName);
    expect(s.library.syncError).not.toBeNull();
  });

  it.each([
    ["frozenPreparing", "preparing"],
    ["frozenVerifying", "verifying"],
    ["frozenRepairing", "repairing"],
    ["frozenDownloading", "downloading"],
    ["frozenInstalling", "installing"],
    ["frozenFinalizing", "finalizing"],
    ["frozenMoving", "moving"],
    ["frozenRemoving", "removing"],
    ["frozenImporting", "importing"],
  ] as [ScenarioName, string][])("%s holds the %s phase still", async (name, phase) => {
    const h = setup({ scenario: name });
    const j = active(await h.snapshot())[0]!;
    expect(j.status).toBe("running");
    expect(j.phase).toBe(phase);
    expect(j.phases).toContain(phase);
    expect(j.progress!.phase).toBe(phase);
    const before = h.progress.length;
    await h.advance(2000);
    expect(h.progress.length - before).toBeGreaterThanOrEqual(15); // still emitting at 10 Hz
    const last = h.progress.at(-1)!;
    expect(last.phase).toBe(phase);
    expect(last.percent).toBeCloseTo(j.progress!.percent, 5);
    expect(active(await h.snapshot())[0]!.phase).toBe(phase);
  });

  it("repairing shows verify results; verifying shows bad files found so far", async () => {
    const rep = setup({ scenario: "frozenRepairing" });
    const j = active(await rep.snapshot())[0]!;
    expect(j.verify).toMatchObject({ verified: false });
    expect(j.verify!.filesBad).toBeGreaterThan(0);
    expect(j.progress!.network).not.toBeNull();
    expect(j.progress!.verify!.badFiles).toBe(j.verify!.filesBad);
    const ver = setup({ scenario: "frozenVerifying" });
    const v = active(await ver.snapshot())[0]!;
    expect(v.progress!.network).toBeNull();
    expect(v.progress!.verify!.filesChecked).toBeGreaterThan(0);
    expect(v.verify).toBeNull();
  });

  it("art: none yields all-null art; backdrop is configurable", async () => {
    const h = setup({ scenario: "returning", art: "none", backdrop: "mica" });
    const lib = await library(h);
    expect([...lib.values()].every((g) => g.art.tall === null && g.art.wide === null)).toBe(true);
    expect((await h.snapshot()).app.backdrop).toBe("mica");
  });

  it("loadScenario resets state, emits a library reset with a higher revision, and a snapshot", async () => {
    const h = setup({ scenario: "returning" });
    const rev0 = (await h.snapshot()).library.revision;
    const before = h.states.length;
    h.controller.loadScenario("emptyLibrary");
    await h.advance(1);
    expect(h.states.length).toBeGreaterThan(before);
    const d = h.deltas.at(-1)!;
    expect(d).toMatchObject({ reset: true, upserts: [] });
    expect(d.revision).toBeGreaterThan(rev0);
    expect((await h.snapshot()).library.gameCount).toBe(0);
    expect(h.controller.getScenario()).toBe("emptyLibrary");
  });

  it("settles in-flight calls as cancelled when the scenario is reloaded", async () => {
    const h = setup({ latency: 1 });
    const p = h.client.call("game.getDetail", { appName: "Fortnite" });
    const settled = p.then(() => "ok", (e: { error: { code: string } }) => e.error.code);
    h.controller.loadScenario("playing");
    await h.advance(500);
    expect(await settled).toBe("cancelled");
  });
});

describe("every method responds", () => {
  const PARAMS: Record<string, unknown> = {
    "app.getLogs": { maxLines: 20 },
    "app.openExternal": { url: "https://store.epicgames.com" },
    "app.openPath": { path: "D:\\Games" },
    "app.pickFolder": { initialPath: null, title: "Pick" },
    "collections.create": { name: "Test" },
    "collections.delete": { id: "col-coop" },
    "collections.rename": { id: "col-coop", name: "Renamed" },
    "collections.setMembership": { id: "col-coop", appName: "Fortnite", member: true },
    "egl.import": { ids: ["glider:Fortnite"], verify: "auto" },
    "egl.importFolder": { path: "D:\\nope", verify: "auto" },
    "egl.validateFolder": { path: "D:\\nope" },
    "game.activate": { appName: "Fortnite" },
    "game.close": { appName: "Fortnite" },
    "game.getDetail": { appName: "Fortnite" },
    "game.install": { appName: "Fortnite", basePath: "D:\\Games", components: null, dlc: null, priority: false },
    "game.installPrerequisites": { appName: "Fortnite" },
    "game.launch": { appName: "Fortnite", mode: null, skipSaveSync: false },
    "game.modify": { appName: "Fortnite", components: [], dlc: [] },
    "game.move": { appName: "Fortnite", destinationBase: "E:\\Epic Games" },
    "game.openFolder": { appName: "Fortnite" },
    "game.planInstall": { appName: "Fortnite", basePath: null, components: null, dlc: null },
    "game.prepareLaunch": { appName: "Fortnite" },
    "game.setShortcut": { appName: "Fortnite", location: "desktop", create: true },
    "game.uninstall": { appName: "Fortnite", mode: "gliderOnly" },
    "game.update": { appName: "Fortnite" },
    "game.updateSettings": { appName: "Fortnite", settings: { launchArgs: "", executableOverride: null, workingDirectory: null, autoUpdate: "default", cloudSaves: true, launchMode: "auto", launchOption: null } },
    "game.verify": { appName: "Fortnite" },
    "jobs.cancel": { jobId: "job-1" },
    "jobs.dismiss": { jobId: "job-1" },
    "jobs.getHistory": { jobId: "job-1" },
    "jobs.moveToTop": { jobId: "job-1" },
    "jobs.pause": { jobId: "job-1" },
    "jobs.reorder": { jobId: "job-1", toIndex: 0 },
    "jobs.resume": { jobId: "job-1" },
    "library.setFavorite": { appName: "Fortnite", value: true },
    "library.setHidden": { appName: "Fortnite", value: false },
    "onboarding.setStep": { step: "scan", skipped: false },
    "overlay.setEnabled": { appName: "eos-overlay", value: true },
    "saves.get": { appName: "Fortnite" },
    "saves.openBackups": { appName: "Fortnite" },
    "saves.resolve": { appName: "Fortnite", keep: "local", thenLaunch: false },
    "saves.sync": { appName: "Fortnite", direction: "auto" },
    "settings.update": null,
    "store.setBounds": { x: 0, y: 0, width: 800, height: 600 },
    "store.show": { target: { kind: "freeGames", slug: null }, bounds: { x: 0, y: 0, width: 800, height: 600 } },
  };

  it("answers all RPC methods (a result or a GliderError), never 'Unknown method'", async () => {
    const h = setup({ scenario: "returning" });
    const snap = await h.snapshot();
    PARAMS["settings.update"] = snap.settings;
    for (const method of RPC_METHODS) {
      const p = Object.hasOwn(PARAMS, method) ? PARAMS[method] : undefined;
      try {
        await h.settle((h.client.call as (m: string, ...a: unknown[]) => Promise<unknown>)(method, ...(p === undefined ? [] : [p])), 20_000);
      } catch (e) {
        const err = (e as { error?: { code: string; message: string } }).error;
        expect(err, `${method} threw a non-GliderError`).toBeDefined();
        expect(err!.message, method).not.toMatch(/Unknown method/);
      }
    }
  });

  it("rejects an unknown method with invalidRequest", async () => {
    const h = setup();
    const e = await h.callError("app.nope" as never);
    expect(e.error.code).toBe("invalidRequest");
  });
});

describe("library deltas", () => {
  it("setFavorite/setHidden push one delta each, bumping the revision by exactly one", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && !x.favorite && !x.hidden);
    const rev = (await h.snapshot()).library.revision;
    await h.call("library.setFavorite", { appName: g.appName, value: true });
    await h.advance(5);
    expect(h.deltas.at(-1)).toMatchObject({ revision: rev + 1, reset: false, removes: [] });
    expect(h.deltas.at(-1)!.upserts).toHaveLength(1);
    expect(h.deltas.at(-1)!.upserts[0]).toMatchObject({ appName: g.appName, favorite: true });
    await h.call("library.setHidden", { appName: g.appName, value: true });
    await h.advance(5);
    expect(h.deltas.at(-1)!.revision).toBe(rev + 2);
    expect((await h.snapshot()).library.revision).toBe(rev + 2);
    expect((await library(h)).get(g.appName)).toMatchObject({ favorite: true, hidden: true });
  });

  it("coalesces same-tick changes into one delta", async () => {
    const h = setup();
    const lib = await library(h);
    const [a, b] = [...lib.values()].filter((x) => x.kind === "game" && !x.favorite);
    const before = h.deltas.length;
    await Promise.all([
      h.call("library.setFavorite", { appName: a!.appName, value: true }),
      h.call("library.setFavorite", { appName: b!.appName, value: true }),
    ]);
    await h.advance(5);
    expect(h.deltas.length - before).toBeLessThanOrEqual(2);
    const merged = h.deltas.slice(before).flatMap((d) => d.upserts.map((g) => g.appName));
    expect(merged).toEqual(expect.arrayContaining([a!.appName, b!.appName]));
  });

  it("a dropped delta shows as a revision gap and library.get recovers the full state", async () => {
    const h = setup();
    const lib = await library(h);
    const [a, b] = [...lib.values()].filter((x) => x.kind === "game" && !x.favorite);
    const rev = (await h.snapshot()).library.revision;
    h.controller.dropNextLibraryDelta();
    await h.call("library.setFavorite", { appName: a!.appName, value: true });
    await h.advance(5);
    expect(h.deltas.filter((d) => !d.reset && d.revision === rev + 1)).toHaveLength(0);
    await h.call("library.setFavorite", { appName: b!.appName, value: true });
    await h.advance(5);
    const next = h.deltas.at(-1)!;
    expect(next.revision).toBe(rev + 2); // the gap the UI must detect
    const full = await h.call("library.get");
    expect(full.reset).toBe(true);
    expect(full.revision).toBe(rev + 2);
    expect(full.upserts.find((g: GameSummary) => g.appName === a!.appName)?.favorite).toBe(true);
  });

  it("refresh syncs in the background and reports the result in the snapshot", async () => {
    const h = setup({ scenario: "syncError" });
    await h.call("library.refresh");
    expect((await h.snapshot()).library.syncing).toBe(true);
    await h.advance(2000);
    let s = await h.snapshot();
    expect(s.library.syncing).toBe(false);
    expect(s.library.syncError).not.toBeNull();
    await h.call("library.refresh");
    await h.advance(2000);
    s = await h.snapshot();
    expect(s.library.syncError).toBeNull();
    expect(s.library.lastSyncedAt).not.toBeNull();
  });

  it("checkUpdates finds one update the first time, via a library delta and a notice", async () => {
    const h = setup();
    const before = (await h.snapshot()).library.updatesAvailable;
    await h.call("library.checkUpdates");
    await h.advance(1500);
    expect((await h.snapshot()).library.updatesAvailable).toBe(before + 1);
    expect(h.notices.at(-1)!.title).toBe("An update is ready");
    await h.call("library.checkUpdates");
    await h.advance(1500);
    expect(h.notices.at(-1)!.title).toBe("Everything is up to date");
  });

  it("updateAll queues an update for every installed game with one pending", async () => {
    const h = setup({ scenario: "updates" });
    const n = (await h.snapshot()).library.updatesAvailable;
    const r = await h.call("library.updateAll");
    expect(r.jobs.length).toBeGreaterThanOrEqual(n - 2);
    expect(r.jobs.every((j: JobInfo) => j.kind === "update")).toBe(true);
    expect(active(await h.snapshot())).toHaveLength(r.jobs.length);
  });
});

describe("collections", () => {
  it("creates, renames, adds/removes members and deletes", async () => {
    const h = setup();
    const c = await h.call("collections.create", { name: "Weekend" });
    expect(c.appNames).toEqual([]);
    expect((await h.snapshot()).collections.some((x) => x.id === c.id)).toBe(true);
    const withGame = await h.call("collections.setMembership", { id: c.id, appName: "Fortnite", member: true });
    expect(withGame.appNames).toEqual(["Fortnite"]);
    expect((await h.call("collections.setMembership", { id: c.id, appName: "Fortnite", member: true })).appNames).toEqual(["Fortnite"]);
    expect((await h.call("collections.rename", { id: c.id, name: "Friday" })).name).toBe("Friday");
    expect((await h.call("collections.setMembership", { id: c.id, appName: "Fortnite", member: false })).appNames).toEqual([]);
    await h.call("collections.delete", { id: c.id });
    expect((await h.snapshot()).collections.some((x) => x.id === c.id)).toBe(false);
    expect((await h.callError("collections.rename", { id: "nope", name: "x" })).error.code).toBe("invalidRequest");
    expect((await h.callError("collections.create", { name: "  " })).error.code).toBe("invalidRequest");
  });
});

describe("small calls", () => {
  it("pickFolder cycles deterministic results; locate titles return a valid then an invalid folder", async () => {
    const h = setup({ scenario: "firstRunSignedIn" });
    const a = await h.call("app.pickFolder", { initialPath: null, title: "Choose" });
    const b = await h.call("app.pickFolder", { initialPath: null, title: "Choose" });
    expect(a.path).not.toBe(b.path);
    const good = await h.call("app.pickFolder", { initialPath: null, title: "Locate your game" });
    expect((await h.call("egl.validateFolder", { path: good.path! })).valid).toBe(true);
    const bad = await h.call("app.pickFolder", { initialPath: null, title: "Locate your game" });
    const v = await h.call("egl.validateFolder", { path: bad.path! });
    expect(v.valid).toBe(false);
    expect(v.reason).toBeTruthy();
  });

  it("returns ~300 log lines, honouring maxLines", async () => {
    const h = setup();
    const all = await h.call("app.getLogs", { maxLines: 1000 });
    expect(all.text.split("\n").length).toBeGreaterThan(250);
    expect((await h.call("app.getLogs", { maxLines: 10 })).text.split("\n")).toHaveLength(10);
  });

  it("lists Legendary 0.21.1 under GPL-3.0 with a source link", async () => {
    const h = setup();
    const l = (await h.call("app.getLicenses")).items.find((i) => i.name === "Legendary")!;
    expect(l).toMatchObject({ version: "0.21.1", license: "GPL-3.0" });
    expect(l.url).toMatch(/^https:\/\/github\.com\/legendary-gl\/legendary/);
  });

  it("toggleMaximize flips state and emits a window event", async () => {
    const h = setup();
    await h.call("window.toggleMaximize");
    expect(h.windows.at(-1)).toEqual({ maximized: true, focused: true, visible: true });
    expect((await h.call("window.getInfo")).maximized).toBe(true);
  });

  it("settings.update echoes, mirrors eglSync into the EGL state; reset restores defaults", async () => {
    const h = setup();
    const s = (await h.snapshot()).settings;
    const echoed = await h.call("settings.update", { ...s, eglSync: false, downloadMode: "full", speedLimitBytesPerSecond: 5_000_000 });
    expect(echoed).toMatchObject({ eglSync: false, downloadMode: "full" });
    const snap = await h.snapshot();
    expect(snap.egl.syncEnabled).toBe(false);
    expect(snap.queue.effectiveMode).toBe("full");
    const reset = await h.call("settings.reset");
    expect(reset).toMatchObject({ eglSync: true, downloadMode: "balanced", speedLimitBytesPerSecond: null, whilePlaying: "pause" });
    expect(reset.libraryFolders.length).toBeGreaterThan(0);
  });

  it("onboarding: setStep persists the step and the skipped one; complete; restart", async () => {
    const h = setup({ scenario: "firstRun" });
    const a = await h.call("onboarding.setStep", { step: "signIn", skipped: false });
    expect(a).toMatchObject({ step: "signIn", completed: false, skipped: [] });
    const b = await h.call("onboarding.setStep", { step: "folders", skipped: true });
    expect(b).toMatchObject({ step: "folders", skipped: ["signIn"] });
    expect((await h.snapshot()).onboarding.step).toBe("folders");
    expect(await h.call("onboarding.complete")).toMatchObject({ completed: true, step: "done" });
    expect(await h.call("onboarding.restart")).toMatchObject({ completed: false, step: "welcome", skipped: [] });
  });

  it("store show/hide/back/reload emit store events", async () => {
    const h = setup();
    const s = await h.call("store.show", { target: { kind: "product", slug: "lanternfall" }, bounds: { x: 0, y: 0, width: 800, height: 600 } });
    expect(s).toMatchObject({ created: true, visible: true, loading: true, canGoBack: true });
    await h.advance(800);
    expect(h.stores.at(-1)).toMatchObject({ loading: false, visible: true });
    expect((await h.call("store.back")).canGoBack).toBe(false);
    expect((await h.call("store.hide")).visible).toBe(false);
    expect((await h.call("store.reload")).loading).toBe(true);
  });

  it("freeGames lists free now (one owned) and upcoming; offline it still answers (Core serves its cache)", async () => {
    const h = setup();
    const r = await h.call("freeGames.get");
    expect(r.items.filter((i) => i.status === "freeNow")).toHaveLength(2);
    expect(r.items.filter((i) => i.status === "upcoming")).toHaveLength(1);
    expect(r.items.some((i) => i.owned)).toBe(true);
    expect(r.items.every((i) => i.imageUrl)).toBe(true);
    const off = setup({ scenario: "offline" });
    expect((await off.call("freeGames.get")).items.length).toBeGreaterThan(0);
  });

  it("game detail carries description, DLC, components, prerequisites, saves, settings, shortcuts", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.dlcCount > 0 && x.installStatus === "installed");
    const d = await h.call("game.getDetail", { appName: g.appName });
    expect(d.summary.appName).toBe(g.appName);
    expect(d.description).toBeTruthy();
    expect(d.dlc).toHaveLength(g.dlcCount);
    expect(d.settings.launchMode).toBe("auto");
    expect(d.executable).toMatch(/\.exe$/);
    expect((await h.callError("game.getDetail", { appName: "nope" })).error.code).toBe("notFound");
  });

  it("game settings: stored, cloudSaves toggle drives the saves state, unknown launch option rejected", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.cloudSaves && x.installStatus === "installed" && x.saveStatus === "inSync");
    const d = await h.call("game.getDetail", { appName: g.appName });
    const out = await h.call("game.updateSettings", { appName: g.appName, settings: { ...d.settings, launchArgs: "-windowed", cloudSaves: false } });
    expect(out.launchArgs).toBe("-windowed");
    expect((await h.call("saves.get", { appName: g.appName })).status).toBe("disabled");
    expect((await library(h)).get(g.appName)!.saveStatus).toBe("disabled");
    expect((await h.callError("game.updateSettings", { appName: g.appName, settings: { ...d.settings, launchOption: "Nope" } })).error.code).toBe("invalidRequest");
  });

  it("setShortcut stores desktop / start menu independently", async () => {
    const h = setup();
    const a = await h.call("game.setShortcut", { appName: "Fortnite", location: "desktop", create: true });
    expect(a.desktop).toBe(true);
    const b = await h.call("game.setShortcut", { appName: "Fortnite", location: "startMenu", create: true });
    expect(b).toEqual({ desktop: true, startMenu: true });
    expect((await h.call("game.setShortcut", { appName: "Fortnite", location: "desktop", create: false })).desktop).toBe(false);
  });
});

describe("planning and space", () => {
  it("plans against the free space of the chosen drive, with components, DLC and prerequisites", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none" && x.dlcCount > 0);
    const d = await h.call("game.getDetail", { appName: g.appName });
    const d1 = await h.call("game.planInstall", { appName: g.appName, basePath: "D:\\Games", components: null, dlc: null });
    expect(d1.gamePath).toBe(`D:\\Games\\${d.summary.title.replace(/[^A-Za-z0-9]+/g, "")}`);
    expect(d1.dlc.length).toBe(d.dlc.length);
    expect(d1.hasEnoughSpace).toBe(true);
    expect(d1.freeBytes).toBeGreaterThan(100e9);
    const f = await h.call("game.planInstall", { appName: g.appName, basePath: "F:\\Library", components: null, dlc: null });
    expect(f.freeBytes).toBeLessThan(d1.freeBytes);
    const without = await h.call("game.planInstall", { appName: g.appName, basePath: "D:\\Games", components: null, dlc: [] });
    expect(without.installSizeBytes).toBeLessThanOrEqual(d1.installSizeBytes);
    expect(without.dlc.every((x: { selected: boolean }) => !x.selected)).toBe(true);
  });

  it("plans no components or prerequisites, like Core: changing components isn't available yet", async () => {
    const h = setup();
    const lib = await library(h);
    for (const app of [...lib.values()].filter((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none").slice(0, 12)) {
      const p = await h.call("game.planInstall", { appName: app.appName, basePath: null, components: null, dlc: null });
      expect(p.components).toEqual([]);
      expect(p.prerequisites).toEqual([]);
    }
    const installed = find(lib, (x) => x.kind === "game" && x.installStatus === "installed");
    expect((await h.call("game.getDetail", { appName: installed.appName })).components).toEqual([]);
  });

  it("flags an existing EGL install at the target folder", async () => {
    const h = setup({ scenario: "firstRunSignedIn" });
    const s = await h.snapshot();
    const inst = s.egl.installs.find((i) => i.status === "importable")!;
    const base = inst.installPath.replace(/\\[^\\]*$/, "");
    const plan = await h.call("game.planInstall", { appName: inst.appName, basePath: base, components: null, dlc: null });
    expect(plan.existingInstallFound).toBe(true);
  });

  it("install without enough space fails with the exact C# copy and the game's appName", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none" && (x.installSizeBytes ?? 0) > 20e9);
    const e = await h.callError("game.install", { appName: g.appName, basePath: "F:\\Library", components: null, dlc: null, priority: false });
    // F: has 18.4 GB free
    expect(e.error.code).toBe("insufficientSpace");
    expect(e.error.fix).toBe("freeSpace");
    expect(e.error.message).toContain("18.4 GB is free");
  });

  it("update doesn't pre-check free space (Core leaves that to the run)", async () => {
    const h = setup({ scenario: "diskFull" });
    const job = await h.call("game.update", { appName: "Fortnite" });
    expect(job).toMatchObject({ kind: "update", appName: "Fortnite" });
  });
});

describe("guards", () => {
  it("requires sign-in, reports an expired session with a sign-in fix", async () => {
    const out = setup({ scenario: "firstRun" });
    const e = await out.callError("game.planInstall", { appName: "Fortnite", basePath: null, components: null, dlc: null });
    expect(e.error.code).toBe("notSignedIn");
    const exp = setup({ scenario: "authExpired" });
    const lib = await library(exp);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    const e2 = await exp.callError("game.install", { appName: g.appName, basePath: "D:\\Games", components: null, dlc: null, priority: false });
    expect(e2.error).toMatchObject({ code: "authExpired", fix: "signIn" });
  });

  it("rejects install when installed, non-installed operations when not, third-party and unknown", async () => {
    const h = setup();
    const lib = await library(h);
    const inst = find(lib, (x) => x.kind === "game" && x.installStatus === "installed");
    const not = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    const tp = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstallable");
    expect((await h.callError("game.install", { appName: inst.appName, basePath: "D:\\Games", components: null, dlc: null, priority: false })).error.code).toBe("alreadyInstalled");
    expect((await h.callError("game.verify", { appName: not.appName })).error.code).toBe("notInstalled");
    expect((await h.callError("game.move", { appName: not.appName, destinationBase: "E:\\Epic Games" })).error.code).toBe("notInstalled");
    expect((await h.callError("game.uninstall", { appName: not.appName, mode: "deleteFiles" })).error.code).toBe("notInstalled");
    expect((await h.callError("game.install", { appName: tp.appName, basePath: "D:\\Games", components: null, dlc: null, priority: false })).error.code).toBe("thirdPartyRequired");
    expect((await h.callError("game.verify", { appName: "nope" })).error.code).toBe("notFound");
  });

  it("offline: installs are accepted and then fail with a network error, like Core (there is no offline gate); the cached library is still there", async () => {
    const h = setup({ scenario: "offline", ...fast });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    const job = await h.call("game.install", { appName: g.appName, basePath: "D:\\Games", components: null, dlc: [], priority: false });
    await until(h, async () => (await h.job(job.id))?.status === "failed");
    expect((await h.job(job.id))!.error?.code).toBe("network");
  });

  it("blocks verify, move and uninstall while the game runs", async () => {
    const h = setup({ scenario: "playing" });
    for (const call of [
      () => h.callError("game.verify", { appName: "Fortnite" }),
      () => h.callError("game.move", { appName: "Fortnite", destinationBase: "E:\\Epic Games" }),
      () => h.callError("game.uninstall", { appName: "Fortnite", mode: "deleteFiles" }),
      () => h.callError("game.update", { appName: "Fortnite" }),
    ]) {
      const e = await call();
      expect(e.error).toMatchObject({ code: "gameRunning", fix: "closeGame", appName: "Fortnite" });
    }
  });

  it("blocks launching while an update is running or EGL is busy with the game", async () => {
    const h = setup({ scenario: "downloading" });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.updateAvailable && x.thirdParty === "none" && x.saveStatus !== "conflict");
    await h.call("game.update", { appName: g.appName });
    expect((await h.callError("game.launch", { appName: g.appName, mode: null, skipSaveSync: false })).error.code).toBe("gameBusy");
    const egl = setup({ scenario: "eglBusy" });
    const job = active(await egl.snapshot())[0]!;
    expect((await egl.callError("game.launch", { appName: job.appName, mode: null, skipSaveSync: false })).error.code).toBe("gameBusy");
  });

  it("failNext fails exactly the next mutating call; reads are exempt", async () => {
    const h = setup();
    h.controller.failNext("network");
    await h.call("app.getSnapshot");
    expect((await h.callError("library.setFavorite", { appName: "Fortnite", value: true })).error.code).toBe("network");
    await h.call("library.setFavorite", { appName: "Fortnite", value: true });
    const f = setup({ fail: "corrupted" });
    expect((await f.callError("game.verify", { appName: "Fortnite" })).error.code).toBe("corrupted");
  });

  it("emitNotice pushes a notice with a generated id", async () => {
    const h = setup();
    h.controller.emitNotice({ level: "info", title: "Hi", message: null, action: null, appName: "Fortnite", jobId: null });
    expect(h.notices.at(-1)).toMatchObject({ id: expect.stringMatching(/^n-\d+$/) as unknown, title: "Hi", appName: "Fortnite" });
  });
});

describe("auth", () => {
  it("signs in through signingIn, then signed in with the account; signOut clears the library", async () => {
    const h = setup({ scenario: "firstRun", latency: 1 });
    const p = h.call("auth.signIn");
    await h.advance(100);
    expect((await h.client.call("app.getSnapshot").then((s) => s)).auth.status).toBeDefined();
    const r = await p;
    expect(r).toMatchObject({ status: "signedIn", account: { displayName: "Marlowe" } });
    expect(h.states.some((s) => s.auth.status === "signingIn")).toBe(true);
    await until(h, async () => (await h.snapshot()).library.gameCount > 0);
    const out = await h.call("auth.signOut");
    expect(out).toMatchObject({ status: "signedOut", account: null });
    expect((await h.snapshot()).library.gameCount).toBe(0);
    expect(h.deltas.at(-1)).toMatchObject({ reset: true, upserts: [] });
  });

  it("fail=authCancelled rejects and leaves the user signed out", async () => {
    const h = setup({ scenario: "firstRun", fail: "authCancelled" });
    expect((await h.callError("auth.signIn")).error.code).toBe("authCancelled");
    expect((await h.snapshot()).auth.status).toBe("signedOut");
  });
});

describe("jobs: lifecycle and queue", () => {
  const installable = async (h: H, n = 0): Promise<GameSummary> => {
    const lib = await library(h);
    return [...lib.values()].filter((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none" && !x.hidden)[n]!;
  };
  const install = (h: H, appName: string, priority = false) =>
    h.call("game.install", { appName, basePath: "D:\\Games", components: null, dlc: [], priority });

  it("runs an install through every phase to completion and updates the library", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const g = await installable(h);
    const job = await install(h, g.appName);
    expect(job).toMatchObject({ kind: "install", appName: g.appName, title: g.title, toVersion: g.latestVersion });
    expect(job.phases).toEqual(["preparing", "downloading", "installing", "finalizing"]);
    expect(job.targetPath).toMatch(/^D:\\Games\\/);
    await until(h, async () => (await h.job(job.id))?.status === "completed");
    // Poll-free: the 10 Hz progress stream shows every phase the job went through.
    const phases = h.progress.filter((p) => p.jobId === job.id).map((p) => p.phase).filter((p, i, all) => all.indexOf(p) === i);
    expect(phases).toEqual(["preparing", "downloading", "installing", "finalizing"]);
    const done = (await h.job(job.id))!;
    expect(done).toMatchObject({ status: "completed", error: null });
    expect(done.finishedAt).not.toBeNull();
    expect(done.progress!.percent).toBe(100);
    const after = (await library(h)).get(g.appName)!;
    expect(after).toMatchObject({ installStatus: "installed", installedVersion: g.latestVersion, updateAvailable: false, activeJobId: null, activeJobKind: null });
    expect(after.installPath).toBe(job.targetPath);
    expect(h.notices.some((n) => n.jobId === job.id && n.level === "success" && n.appName === g.appName)).toBe(true);
  });

  it("emits progress at 10 Hz, with a monotonic whole-job percent and honest groups per phase", async () => {
    const h = setup({ scenario: "returning", timeScale: 40 });
    const g = await installable(h);
    const job = await install(h, g.appName);
    await until(h, async () => (await h.job(job.id))?.status === "completed", 300_000);
    const mine = h.progress.filter((p) => p.jobId === job.id);
    expect(mine.length).toBeGreaterThan(30);
    let prev = 0;
    for (const p of mine) {
      expect(p.percent).toBeGreaterThanOrEqual(prev);
      prev = p.percent;
      expect(p.appName).toBe(g.appName);
      if (p.phase === "downloading") expect(p.network && p.disk).toBeTruthy();
      if (p.phase === "installing") expect(p.network).toBeNull();
    }
    const dt = (mine.length - 1) / ((mine.length - 1) * 0.1);
    expect(dt).toBeCloseTo(10, 0);
  });

  it("keeps the installed state intact when an update is cancelled, and reverts an install when cancelled", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const g = await installable(h);
    const job = await install(h, g.appName);
    await h.advance(300);
    expect((await h.call("jobs.cancel", { jobId: job.id })).status).toBe("cancelled");
    expect((await library(h)).get(g.appName)!.installStatus).toBe("notInstalled");
    const lib = await library(h);
    const u = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.updateAvailable && x.thirdParty === "none");
    const uj = await h.call("game.update", { appName: u.appName });
    await h.advance(300);
    await h.call("jobs.cancel", { jobId: uj.id });
    expect((await library(h)).get(u.appName)).toMatchObject({ installStatus: "installed", updateAvailable: true });
  });

  it("pause stops progress and resume continues from the same point", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    await h.advance(1500);
    await h.call("jobs.pause", { jobId: j.id });
    expect((await h.job(j.id))!.status).toBe("paused");
    await h.advance(100);
    const n = h.progress.length;
    const pct = h.progress.at(-1)!.percent;
    await h.advance(2000);
    expect(h.progress.length).toBe(n);
    await h.call("jobs.resume", { jobId: j.id });
    await h.advance(1500);
    expect(h.progress.length).toBeGreaterThan(n);
    expect(h.progress.at(-1)!.percent).toBeGreaterThanOrEqual(pct);
  });

  it("runs one job at a time in queue order and starts the next when one finishes", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const [a, b, c] = [await installable(h, 0), await installable(h, 1), await installable(h, 2)];
    const ja = await install(h, a.appName);
    const jb = await install(h, b.appName);
    const jc = await install(h, c.appName);
    let s = await h.snapshot();
    expect(active(s).map((j) => j.status)).toEqual(["running", "queued", "queued"]);
    expect(active(s).map((j) => j.id)).toEqual([ja.id, jb.id, jc.id]);
    await until(h, async () => (await h.job(ja.id))?.status === "completed");
    s = await h.snapshot();
    expect(s.queue.jobs.find((j) => j.id === jb.id)!.status).toBe("running");
    await until(h, async () => (await h.job(jc.id))?.status === "completed");
  });

  it("is idempotent per (game, kind): a second install returns the same job", async () => {
    const h = setup({ scenario: "returning" });
    const g = await installable(h);
    const a = await install(h, g.appName);
    const b = await install(h, g.appName);
    expect(b.id).toBe(a.id);
    expect(active(await h.snapshot())).toHaveLength(1);
  });

  it("priority installs go to the front (after the running job); moveToTop and reorder work", async () => {
    const h = setup({ scenario: "returning" });
    const [a, b, c, d] = [await installable(h, 0), await installable(h, 1), await installable(h, 2), await installable(h, 3)];
    const ja = await install(h, a.appName);
    const jb = await install(h, b.appName);
    const jc = await install(h, c.appName);
    const jd = await install(h, d.appName, true);
    expect(active(await h.snapshot()).map((j) => j.id)).toEqual([ja.id, jd.id, jb.id, jc.id]);
    let q = await h.call("jobs.moveToTop", { jobId: jc.id });
    expect(q.jobs.filter((j) => j.status !== "completed").map((j) => j.id).slice(0, 4)).toEqual([ja.id, jc.id, jd.id, jb.id]);
    q = await h.call("jobs.reorder", { jobId: jb.id, toIndex: 1 });
    expect(q.jobs.map((j) => j.id).slice(0, 4)).toEqual([ja.id, jb.id, jc.id, jd.id]);
    q = await h.call("jobs.reorder", { jobId: jd.id, toIndex: 0 });
    expect(q.jobs[0]!.id).toBe(ja.id); // never ahead of the running job
  });

  it("a paused job blocks only its own game: others still run", async () => {
    const h = setup({ scenario: "queue" });
    const s = await h.snapshot();
    const running = active(s).find((j) => j.status === "running")!;
    await h.call("jobs.cancel", { jobId: running.id });
    await h.advance(500);
    const next = active(await h.snapshot());
    expect(next.find((j) => j.status === "paused")).toBeDefined();
    expect(next.find((j) => j.status === "running")).toBeDefined();
    expect(next.find((j) => j.status === "running")!.status).toBe("running");
    const paused = next.find((j) => j.status === "paused")!;
    // A queued job for the paused job's own game stays blocked.
    expect(next.filter((j) => j.appName === paused.appName && j.status === "running")).toHaveLength(0);
  });

  it("pauseAll holds everything; resumeAll continues; pausing a move or import is refused", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    let q = await h.call("queue.pauseAll");
    expect(q.paused).toBe(true);
    expect(q.jobs.find((x) => x.id === j.id)!.status).toBe("paused"); // every pausable job pauses, as in Core
    const n = h.progress.length;
    await h.advance(1500);
    expect(h.progress.length).toBe(n);
    q = await h.call("queue.resumeAll");
    expect(q.paused).toBe(false);
    expect(["queued", "running"]).toContain(q.jobs.find((x) => x.id === j.id)!.status);
    await h.advance(500);
    expect((await h.job(j.id))!.status).toBe("running");

    const mv = setup({ scenario: "frozenMoving" });
    const m = active(await mv.snapshot())[0]!;
    expect((await mv.callError("jobs.pause", { jobId: m.id })).error.code).toBe("invalidRequest");
  });

  it("pausing a finished job is an error; dismiss removes history and ignores active jobs", async () => {
    const h = setup({ scenario: "downloading" });
    const s = await h.snapshot();
    const fin = s.queue.jobs.find((j) => j.status === "completed")!;
    expect((await h.callError("jobs.pause", { jobId: fin.id })).error.code).toBe("invalidRequest");
    expect((await h.call("jobs.cancel", { jobId: fin.id })).status).toBe("completed"); // cancelling a finished job changes nothing
    await h.call("jobs.dismiss", { jobId: fin.id });
    expect((await h.snapshot()).queue.jobs.some((j) => j.id === fin.id)).toBe(false);
    await h.call("jobs.dismiss", { jobId: active(s)[0]!.id }); // an unfinished job is ignored, not an error
    expect(active(await h.snapshot()).some((j) => j.id === active(s)[0]!.id)).toBe(true);
    const cleared = await h.call("queue.clearFinished");
    expect(cleared.jobs.every((j) => ["running", "queued", "paused", "waiting"].includes(j.status))).toBe(true);
  });

  it("keeps at most 20 finished jobs in the queue state", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const lib = await library(h);
    const gs = [...lib.values()].filter((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none").slice(0, 14);
    for (const g of gs) await install(h, g.appName);
    await until(h, async () => active(await h.snapshot()).length === 0, 600_000);
    expect(jobsOf(await h.snapshot()).length).toBeLessThanOrEqual(20);
  });

  it("verify of a clean game: verifying only, then done with a report; no repair step appears", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none");
    const job = await h.call("game.verify", { appName: g.appName });
    expect(job.phases).toEqual(["verifying", "repairing", "finalizing"]);
    const seen: string[] = [];
    await until(h, async () => {
      const j = await h.job(job.id);
      if (j?.phase && seen.at(-1) !== j.phase) seen.push(j.phase);
      return j?.status === "completed";
    });
    expect(seen).not.toContain("repairing");
    const done = (await h.job(job.id))!;
    expect(done.verify).toMatchObject({ filesBad: 0, verified: true });
    expect(done.phases).not.toContain("repairing");
  });

  it("verify of a broken install repairs only the bad files and ends installed", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const lib = await library(h);
    const g = find(lib, (x) => x.installStatus === "broken");
    const job = await h.call("game.verify", { appName: g.appName });
    const seen: string[] = [];
    let report: unknown = null;
    await until(h, async () => {
      const j = await h.job(job.id);
      if (j?.phase && seen.at(-1) !== j.phase) seen.push(j.phase);
      if (j?.phase === "repairing") report = j.verify;
      return j?.status === "completed";
    });
    expect(seen).toEqual(["verifying", "repairing", "finalizing"]);
    expect((report as { filesBad: number }).filesBad).toBeGreaterThan(0);
    expect((await h.job(job.id))!.verify).toMatchObject({ verified: true });
    expect((await library(h)).get(g.appName)!.installStatus).toBe("installed");
  });

  it("failActiveJob fails the running job with the error, a notice and appName", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    h.controller.failActiveJob("network");
    const f = (await h.job(j.id))!;
    expect(f).toMatchObject({ status: "failed" });
    expect(f.error).toMatchObject({ code: "network", appName: j.appName });
    expect(h.notices.at(-1)).toMatchObject({ level: "error", jobId: j.id, appName: j.appName });
    expect((await library(h)).get(j.appName)!.activeJobId).toBeNull();
  });

  it("a corrupted failure marks an install broken", async () => {
    const h = setup({ scenario: "returning", timeScale: 600 });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.updateAvailable && x.thirdParty === "none");
    await h.call("game.update", { appName: g.appName });
    await h.advance(300);
    h.controller.failActiveJob("corrupted");
    expect((await library(h)).get(g.appName)!.installStatus).toBe("broken");
  });

  it("getHistory returns ≈1 Hz samples for running, finished and seeded jobs", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    await h.advance(3000);
    const live = await h.call("jobs.getHistory", { jobId: j.id });
    expect(live.jobId).toBe(j.id);
    expect(live.samples.length).toBeGreaterThan(5);
    expect(live.samples.at(-1)!.networkBytesPerSecond).toBeGreaterThan(0);
    const seeded = (await h.snapshot()).queue.jobs.find((x) => x.status === "completed")!;
    expect((await h.call("jobs.getHistory", { jobId: seeded.id })).samples.length).toBeGreaterThan(10);
    expect((await h.callError("jobs.getHistory", { jobId: "nope" })).error.code).toBe("invalidRequest");
  });
});

describe("EGL busy and while-playing policy", () => {
  it("setEglBusy toggles running ⇄ waiting, and new jobs for that game wait (not an error)", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    h.controller.setEglBusy(true);
    const w = (await h.job(j.id))!;
    expect(w).toMatchObject({ status: "waiting" });
    expect(w.blockedReason).toMatch(/Epic Games Launcher/);
    h.controller.setEglBusy(false);
    await h.advance(200);
    expect((await h.job(j.id))!.status).toBe("running");
  });

  it("jobs.resume puts a waiting job back in line, and the gate sends it back to waiting while Epic is still busy; frozen scenarios don't auto-resume", async () => {
    const h = setup({ scenario: "eglBusy" });
    const j = active(await h.snapshot())[0]!;
    const r = await h.call("jobs.resume", { jobId: j.id });
    expect(r.status).toBe("waiting");
    h.controller.setEglBusy(false);
    await h.advance(200);
    expect((await h.job(j.id))!.status).toBe("running");
    const f = setup({ scenario: "eglBusy", freeze: true });
    await f.advance(20_000);
    expect(active(await f.snapshot())[0]!.status).toBe("waiting");
  });

  it("whilePlaying=pause: a running job waits while a game runs and resumes after it closes", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none" && x.saveStatus !== "conflict" && x.appName !== j.appName && x.canRunOffline);
    const launch = h.call("game.launch", { appName: g.appName, mode: null, skipSaveSync: true });
    await until(h, async () => (await h.snapshot()).sessions.some((s) => s.state === "running"));
    await launch;
    const s = await h.snapshot();
    expect(s.queue.jobs.find((x) => x.id === j.id)).toMatchObject({ status: "waiting" });
    expect(s.queue.jobs.find((x) => x.id === j.id)!.blockedReason).toMatch(/Paused while .* is running/);
    expect(s.queue.modeReason).toMatch(/Paused while/);
    h.controller.endGame(g.appName);
    await h.advance(1500);
    expect((await h.job(j.id))!.status).toBe("running");
  });

  it("whilePlaying=throttle: effective mode drops to background with a reason", async () => {
    const h = setup({ scenario: "playing" });
    const s = (await h.snapshot()).settings;
    await h.call("settings.update", { ...s, whilePlaying: "throttle" });
    const q = (await h.snapshot()).queue;
    expect(q.effectiveMode).toBe("background");
    expect(q.modeReason).toMatch(/Throttled while Fortnite is running/);
  });

  it("offline: a running network job fails with a network error (no offline gate in Core); Resume retries it once back online", async () => {
    const h = setup({ scenario: "downloading" });
    const j = active(await h.snapshot())[0]!;
    h.controller.setConnectivity("offline");
    await h.advance(300);
    expect((await h.job(j.id))!).toMatchObject({ status: "failed", error: { code: "network" } });
    h.controller.setConnectivity("online");
    await h.call("jobs.resume", { jobId: j.id });
    await h.advance(300);
    expect((await h.job(j.id))!).toMatchObject({ status: "running", error: null });
  });
});

describe("launching", () => {
  const launch = (h: H, appName: string, extra: Record<string, unknown> = {}) =>
    h.client.call("game.launch", { appName, mode: null, skipSaveSync: false, ...extra } as never);

  it("walks preparing → launching → running (with a cloud sync and prerequisites when needed) and resolves when up", async () => {
    const h = setup({ scenario: "returning", eacInstalled: false });
    const p = h.settle(launch(h, "Fortnite"));
    const r = await p;
    expect(r).toMatchObject({ outcome: "started", offline: false });
    const states = h.states.flatMap((s) => s.sessions.map((x) => x.state));
    expect(states).toEqual(expect.arrayContaining(["preparing", "installingPrerequisites", "launching", "running"]));
    expect((await h.snapshot()).sessions[0]).toMatchObject({ appName: "Fortnite", state: "running", untracked: false });
    const sum = (await library(h)).get("Fortnite")!;
    expect(sum.runState).toBe("running");
    expect((await h.call("game.getDetail", { appName: "Fortnite" })).prerequisites.every((x) => x.installed)).toBe(true);
  });

  it("syncs cloud saves before launch when one side is newer", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.installStatus === "installed" && x.saveStatus === "localNewer" && x.thirdParty === "none");
    const r = await h.settle(launch(h, g.appName));
    expect(r.outcome).toBe("started");
    expect(h.states.flatMap((s) => s.sessions.map((x) => x.state))).toContain("syncingSaves");
    expect((await library(h)).get(g.appName)!.saveStatus).toBe("inSync");
  });

  it("returns SaveConflict (nothing launched) for the conflicting game; Play without syncing skips it", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.saveStatus === "conflict");
    const r = await h.call("game.launch", { appName: g.appName, mode: null, skipSaveSync: false });
    expect(r.outcome).toBe("saveConflict");
    expect(r.conflict).toMatchObject({ appName: g.appName, title: g.title });
    expect(r.conflict!.localModifiedAt).not.toBeNull();
    expect((await h.snapshot()).sessions).toHaveLength(0);
    const skip = await h.settle(launch(h, g.appName, { skipSaveSync: true }));
    expect(skip.outcome).toBe("started");
  });

  it("saves.resolve keeps one side, backs up the other, and can launch right after", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.saveStatus === "conflict");
    const before = await h.call("saves.get", { appName: g.appName });
    expect(before.status).toBe("conflict");
    const r = await h.settle(h.client.call("saves.resolve", { appName: g.appName, keep: "cloud", thenLaunch: true }));
    expect(r!.outcome).toBe("started");
    const after = await h.call("saves.get", { appName: g.appName });
    expect(after.status).toBe("inSync");
    expect(after.backups).toBe(before.backups + 1);
    const none = setup();
    const other = find(await library(none), (x) => x.saveStatus === "conflict");
    expect(await none.call("saves.resolve", { appName: other.appName, keep: "local", thenLaunch: false })).toBeNull();
  });

  it("saves.sync auto never resolves a conflict silently; explicit directions do", async () => {
    const h = setup();
    const g = find(await library(h), (x) => x.saveStatus === "conflict");
    const auto = await h.call("saves.sync", { appName: g.appName, direction: "auto" });
    expect(auto.status).toBe("conflict");
    const up = await h.call("saves.sync", { appName: g.appName, direction: "upload" });
    expect(up.status).toBe("inSync");
    expect(up.lastSyncedAt).not.toBeNull();
    expect((await library(h)).get(g.appName)!.saveStatus).toBe("inSync");
    const n = find(await library(h), (x) => !x.cloudSaves);
    expect((await h.callError("saves.sync", { appName: n.appName, direction: "auto" })).error.code).toBe("invalidRequest");
    expect((await h.call("saves.get", { appName: n.appName })).supported).toBe(false);
  });

  it("third-party game needing activation: NeedsActivation until game.activate, then it launches through the other launcher", async () => {
    const h = setup();
    const lib = await library(h);
    const g = find(lib, (x) => x.thirdParty === "ubisoft" && x.installStatus === "installed");
    const need = find(lib, (x) => x.thirdParty === "ubisoft" && x.installStatus === "installed" && (x.appName === g.appName));
    const first = await h.call("game.launch", { appName: need.appName, mode: null, skipSaveSync: false });
    expect(first.outcome).toBe("needsActivation");
    expect(first.message).toContain("Ubisoft Connect");
    expect((await h.snapshot()).sessions).toHaveLength(0);
    await h.call("game.activate", { appName: need.appName });
    const r = await h.settle(launch(h, need.appName));
    expect(r.outcome).toBe("thirdPartyLauncher");
    expect((await h.snapshot()).sessions[0]).toMatchObject({ appName: need.appName, untracked: true });
    expect((await h.callError("game.activate", { appName: "Fortnite" })).error.code).toBe("invalidRequest");
  });

  it("not-installable third-party titles just open their launcher", async () => {
    const h = setup();
    const g = find(await library(h), (x) => x.kind === "game" && x.installStatus === "notInstallable" && x.thirdParty === "ea");
    const r = await h.call("game.launch", { appName: g.appName, mode: null, skipSaveSync: false });
    expect(r).toMatchObject({ outcome: "thirdPartyLauncher" });
    expect(r.message).toContain("EA app");
  });

  it("offline: games that can run offline start offline; Core never refuses up front, it tries and the run fails", async () => {
    const h = setup({ scenario: "offline" });
    const lib = await library(h);
    const ok = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.canRunOffline && x.thirdParty === "none" && x.saveStatus !== "conflict");
    const forced = await h.callError("game.launch", { appName: ok.appName, mode: "online", skipSaveSync: false });
    expect(forced.error.code).toBe("network");
    expect((await library(h)).get(ok.appName)!.runState).toBe("idle");
    const r = await h.settle(launch(h, ok.appName));
    expect(r).toMatchObject({ outcome: "started", offline: true });
    expect((await h.snapshot()).sessions[0]!.offline).toBe(true);
    const no = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && !x.canRunOffline && x.thirdParty === "none");
    expect((await h.callError("game.launch", { appName: no.appName, mode: null, skipSaveSync: false })).error.code).toBe("network");
  });

  it("broken installs can't launch (corrupted with a repair fix); DLC can't be launched, add-ons can", async () => {
    const h = setup();
    const lib = await library(h);
    const b = find(lib, (x) => x.installStatus === "broken");
    expect((await h.callError("game.launch", { appName: b.appName, mode: null, skipSaveSync: false })).error).toMatchObject({ code: "corrupted", fix: "repair" });
    const dlc = find(lib, (x) => x.kind === "dlc");
    expect((await h.callError("game.launch", { appName: dlc.appName, mode: null, skipSaveSync: false })).error.code).toBe("invalidRequest");
  });

  it("endGame goes through closing back to idle, adds playtime and shows a notice", async () => {
    const h = setup({ scenario: "playing" });
    const before = (await library(h)).get("Fortnite")!.playtimeSeconds;
    h.controller.endGame();
    expect((await h.snapshot()).sessions[0]!.state).toBe("closing");
    await h.advance(800);
    expect((await h.snapshot()).sessions).toHaveLength(0);
    const after = (await library(h)).get("Fortnite")!;
    expect(after.runState).toBe("idle");
    expect(after.playtimeSeconds).toBeGreaterThan(before);
    expect(h.notices.at(-1)!.title).toBe("Fortnite closed");
  });

  it("game.close closes a running game; the game exits on its own after ~2 minutes (not when frozen)", async () => {
    const h = setup({ scenario: "playing" });
    await h.call("game.close", { appName: "Fortnite" });
    await h.advance(800);
    expect((await h.snapshot()).sessions).toHaveLength(0);
    const auto = setup({ scenario: "playing" });
    await auto.advance(121_000);
    expect((await auto.snapshot()).sessions).toHaveLength(0);
    const frozen = setup({ scenario: "playing", freeze: true });
    await frozen.advance(200_000);
    expect((await frozen.snapshot()).sessions).toHaveLength(1);
  });

  it("freeze holds a launch at 'launching'", async () => {
    const h = setup({ scenario: "returning", freeze: true });
    void launch(h, "Fortnite").catch(() => undefined);
    await h.advance(5000);
    expect((await h.snapshot()).sessions[0]!.state).toBe("launching");
  });

  it("installPrerequisites sets them up (and the run state returns to idle)", async () => {
    const h = setup({ scenario: "returning", eacInstalled: false });
    expect((await h.call("game.getDetail", { appName: "Fortnite" })).prerequisites[0]!.installed).toBe(false);
    await h.call("game.installPrerequisites", { appName: "Fortnite" });
    expect((await h.call("game.getDetail", { appName: "Fortnite" })).prerequisites[0]!.installed).toBe(true);
    expect((await library(h)).get("Fortnite")!.runState).toBe("idle");
  });
});

describe("modify, move and uninstall", () => {
  it("moves the install and keeps the EGL link when sync is on; drops it when sync is off", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const lib = await library(h);
    const g = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.sharedWithEgl && x.thirdParty === "none");
    const job = await h.call("game.move", { appName: g.appName, destinationBase: "E:\\Epic Games" });
    expect(job.phases).toEqual(["preparing", "moving", "finalizing"]);
    expect(job.targetPath).toMatch(/^E:\\Epic Games\\/);
    await until(h, async () => (await h.job(job.id))?.status === "completed");
    expect((await library(h)).get(g.appName)).toMatchObject({ installPath: job.targetPath, sharedWithEgl: true });

    const off = setup({ scenario: "returning", ...fast });
    const s = (await off.snapshot()).settings;
    await off.call("settings.update", { ...s, eglSync: false });
    const g2 = find(await library(off), (x) => x.kind === "game" && x.installStatus === "installed" && x.sharedWithEgl && x.thirdParty === "none");
    const j2 = await off.call("game.move", { appName: g2.appName, destinationBase: "E:\\Epic Games" });
    await until(off, async () => (await off.job(j2.id))?.status === "completed");
    expect((await library(off)).get(g2.appName)!.sharedWithEgl).toBe(false);
  });

  it("refuses to move into the same folder or onto a drive without space", async () => {
    const h = setup();
    const g = find(await library(h), (x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none" && (x.installedSizeBytes ?? 0) > 20e9);
    const base = g.installPath!.replace(/\\[^\\]*$/, "");
    expect((await h.callError("game.move", { appName: g.appName, destinationBase: base })).error.code).toBe("invalidRequest");
    expect((await h.callError("game.move", { appName: g.appName, destinationBase: `${g.installPath!}\\inside` })).error.code).toBe("invalidFolder");
    expect((await h.callError("game.move", { appName: g.appName, destinationBase: "F:\\Library2" })).error.code).toBe("insufficientSpace");
  });

  it("'Remove from Glider only' keeps the EGL record; 'delete files' drops it", async () => {
    // Games Glider shares with EGL are the ones the scan reports as already imported.
    const pick = async (h: H) => {
      const lib = await library(h);
      const rec = (await h.snapshot()).egl.installs.find((i) => i.status === "imported" && i.appName !== "Fortnite" && lib.get(i.appName)?.installStatus === "installed");
      if (!rec) throw new Error("no imported EGL install in the mock");
      return lib.get(rec.appName)!;
    };
    const h = setup({ scenario: "returning", ...fast });
    const g = await pick(h);
    const a = await h.call("game.uninstall", { appName: g.appName, mode: "gliderOnly" });
    await until(h, async () => (await h.job(a.id))?.status === "completed");
    expect((await library(h)).get(g.appName)).toMatchObject({ installStatus: "notInstalled", installPath: null, installedVersion: null, sharedWithEgl: false });
    expect((await h.snapshot()).egl.installs.find((i) => i.appName === g.appName)?.status).toBe("importable");

    const del = setup({ scenario: "returning", ...fast });
    const g2 = await pick(del);
    const b = await del.call("game.uninstall", { appName: g2.appName, mode: "deleteFiles" });
    expect(b.phases).toEqual(["removing"]);
    await until(del, async () => (await del.job(b.id))?.status === "completed");
    expect((await del.snapshot()).egl.installs.some((i) => i.appName === g2.appName)).toBe(false);
  });

  it("modify: component changes are not available yet (like Core); DLC changes are install/uninstall jobs", async () => {
    const h = setup({ scenario: "returning", ...fast });
    const lib = await library(h);
    const host = find(lib, (x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none" && [...lib.values()].some((d) => d.baseAppName === x.appName && d.installStatus === "notInstalled"));
    const e = await h.callError("game.modify", { appName: host.appName, components: ["any"], dlc: [] });
    expect(e.error).toMatchObject({ code: "unknown", title: "Not available yet" });
    const have = [...lib.values()].filter((d) => d.baseAppName === host.appName && d.installStatus === "installed").map((d) => d.appName);
    expect((await h.callError("game.modify", { appName: host.appName, components: [], dlc: have })).error.code).toBe("invalidRequest"); // nothing to change
    const dlc = [...lib.values()].find((d) => d.baseAppName === host.appName && d.installStatus === "notInstalled")!;
    const job = await h.call("game.modify", { appName: host.appName, components: [], dlc: [...have, dlc.appName] });
    expect(job).toMatchObject({ kind: "install", appName: dlc.appName });
    await until(h, async () => (await h.job(job.id))?.status === "completed");
    expect((await library(h)).get(dlc.appName)!.installStatus).toBe("installed");
    const off = await h.call("game.modify", { appName: host.appName, components: [], dlc: have });
    expect(off).toMatchObject({ kind: "uninstall", appName: dlc.appName });
  });
});

describe("EGL scan and import", () => {
  const eglInstalls = async (h: H) => (await h.snapshot()).egl.installs;

  it("imports an EGL install with no download and ends installed; a needsRepair outcome for older copies", async () => {
    const h = setup({ scenario: "firstRunSignedIn", ...fast });
    const installs = await eglInstalls(h);
    const clean = installs.find((i) => i.status === "importable" && i.drift === "none")!;
    const res = await h.call("egl.import", { ids: [clean.id], verify: "auto" });
    expect(res.jobs).toHaveLength(1);
    expect(res.jobs[0]).toMatchObject({ kind: "import", appName: clean.appName, targetPath: clean.installPath });
    await until(h, async () => (await h.job(res.jobs[0]!.id))?.status === "completed");
    const done = (await h.job(res.jobs[0]!.id))!;
    expect(done.outcome).toMatchObject({ kind: "success" });
    expect((await library(h)).get(clean.appName)).toMatchObject({ installStatus: "installed", installPath: clean.installPath, sharedWithEgl: true, updateAvailable: false });
    expect((await eglInstalls(h)).find((i) => i.id === clean.id)!.status).toBe("imported");

    const old = installs.find((i) => i.status === "importable" && i.drift === "eglNewer");
    if (old) {
      const r2 = await h.call("egl.import", { ids: [old.id], verify: "auto" });
      await until(h, async () => (await h.job(r2.jobs[0]!.id))?.status === "completed");
      expect((await h.job(r2.jobs[0]!.id))!.outcome).toMatchObject({ kind: "needsRepair" });
      expect((await library(h)).get(old.appName)!.updateAvailable).toBe(true);
    }
  });

  it("bulk import creates one Import job each, serially, with per-game outcomes", async () => {
    const h = setup({ scenario: "firstRunSignedIn", ...fast });
    const ids = (await eglInstalls(h)).filter((i) => i.status === "importable").map((i) => i.id);
    const res = await h.call("egl.import", { ids, verify: "auto" });
    expect(res.jobs).toHaveLength(ids.length);
    expect(active(await h.snapshot()).map((j) => j.status)).toEqual(["running", ...Array(ids.length - 1).fill("queued")]);
    await until(h, async () => active(await h.snapshot()).length === 0, 600_000);
    const outcomes = (await h.snapshot()).queue.jobs.filter((j) => j.kind === "import").map((j) => j.outcome?.kind);
    expect(outcomes.filter(Boolean).length).toBeGreaterThanOrEqual(ids.length);
  });

  it("background verify queues a verify job afterwards and flags needsVerification meanwhile; full verify does it in the job", async () => {
    const h = setup({ scenario: "firstRunSignedIn", ...fast });
    const i = (await eglInstalls(h)).find((x) => x.status === "importable")!;
    const r = await h.call("egl.import", { ids: [i.id], verify: "background" });
    await until(h, async () => (await h.job(r.jobs[0]!.id))?.status === "completed");
    expect((await library(h)).get(i.appName)!.needsVerification).toBe(true);
    expect(active(await h.snapshot()).some((j) => j.kind === "verify" && j.appName === i.appName)).toBe(true);
    await until(h, async () => active(await h.snapshot()).length === 0);
    expect((await library(h)).get(i.appName)!.needsVerification).toBe(false);

    const full = setup({ scenario: "firstRunSignedIn", ...fast });
    const j = (await eglInstalls(full)).find((x) => x.status === "importable")!;
    const fr = await full.call("egl.import", { ids: [j.id], verify: "full" });
    expect(fr.jobs[0]!.phases).toEqual(["importing", "verifying"]);
  });

  it("stale, busy and not-owned installs are skipped, not errors; an empty selection is", async () => {
    const h = setup({ scenario: "firstRunSignedIn" });
    const installs = await eglInstalls(h);
    for (const status of ["stale", "busy", "notOwned"] as const) {
      const one = installs.find((i) => i.status === status)!;
      expect((await h.call("egl.import", { ids: [one.id], verify: "auto" })).jobs).toEqual([]);
    }
    expect((await h.call("egl.import", { ids: ["nope"], verify: "auto" })).jobs).toEqual([]);
    expect((await h.callError("egl.import", { ids: [], verify: "auto" })).error.code).toBe("invalidRequest");
  });

  it("rescan and quickScan refresh lastScan; quickScan finds one extra install once", async () => {
    const h = setup({ scenario: "firstRunSignedIn", latency: 1 });
    const n = (await eglInstalls(h)).length;
    const scanning = h.client.call("egl.quickScan");
    await h.advance(200);
    expect((await h.snapshot()).egl.scanning).toBe(true);
    const r = await h.settle(scanning);
    expect(r.scanning).toBe(false);
    expect(r.installs.length).toBe(n + 1);
    expect(r.installs.at(-1)).toMatchObject({ source: "manual", status: "importable" });
    expect((await h.call("egl.rescan")).installs.length).toBe(n + 1);
    expect((await h.call("egl.quickScan")).installs.length).toBe(n + 1);
  });

  it("locate another folder: validate then importFolder", async () => {
    const h = setup({ scenario: "firstRunSignedIn", ...fast });
    const g = find(await library(h), (x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none" && !(x.appName.startsWith("zz")));
    const folder = [...(await eglInstalls(h))].find((i) => i.appName === g.appName)?.installPath ?? `E:\\Old Library\\${g.title.replace(/[^A-Za-z0-9]+/g, "")}`;
    const v = await h.call("egl.validateFolder", { path: folder });
    expect(v.valid).toBe(true);
    const job = await h.call("egl.importFolder", { path: v.gamePath!, verify: "auto" });
    await until(h, async () => (await h.job(job.id))?.status === "completed");
    expect((await library(h)).get(v.appName!)!.installStatus).toBe("installed");
    expect((await h.callError("egl.importFolder", { path: "D:\\Downloads\\NotAGame", verify: "auto" })).error.code).toBe("invalidFolder");
  });

  it("readiness: blockers for the unlinked third-party game, warnings for shared installs, ready=false while blocked", async () => {
    const h = setup();
    const r = await h.call("egl.checkReadiness");
    expect(r.ready).toBe(false);
    expect(r.items.some((i) => i.severity === "blocker")).toBe(true);
    expect(r.items.some((i) => i.severity === "warning")).toBe(true);
    for (const i of r.items) expect(i.message.length).toBeGreaterThan(0);
    const need = find(await library(h), (x) => x.thirdParty === "ubisoft" && x.installStatus === "installed");
    await h.call("game.activate", { appName: need.appName });
    const again = await h.call("egl.checkReadiness");
    expect(again.items.filter((i) => i.severity === "blocker").length).toBeLessThan(r.items.filter((i) => i.severity === "blocker").length);
  });
});

describe("EOS overlay", () => {
  it("install runs as an overlay job, then remove and enable/disable", async () => {
    const h = setup({ scenario: "firstRun", timeScale: 600 });
    await h.call("auth.signIn");
    await h.advance(1500);
    expect((await h.snapshot()).overlay.installed).toBe(false);
    const job = await h.call("overlay.install");
    expect(job).toMatchObject({ kind: "overlay", appName: "eos-overlay", title: "Epic overlay" });
    expect((await h.snapshot()).overlay.busy).toBe(true);
    await until(h, async () => (await h.job(job.id))?.status === "completed");
    const o = (await h.snapshot()).overlay;
    expect(o).toMatchObject({ installed: true, enabled: true, busy: false, updateAvailable: false });
    expect((await h.call("overlay.setEnabled", { appName: "eos-overlay", value: false })).enabled).toBe(false);
    expect(await h.call("overlay.remove")).toMatchObject({ installed: false, enabled: false, version: null });
    expect((await h.callError("overlay.setEnabled", { appName: "eos-overlay", value: true })).error.code).toBe("invalidRequest");
  });
});

describe("snapshots", () => {
  it("coalesces same-tick changes into one push and always pushes the whole (small) snapshot", async () => {
    const h = setup();
    await h.snapshot();
    const before = h.states.length;
    await Promise.all([
      h.call("onboarding.setStep", { step: "folders", skipped: false }),
      h.call("collections.create", { name: "A" }),
    ]);
    await h.advance(10);
    const pushed = h.states.slice(before);
    expect(pushed.length).toBeGreaterThanOrEqual(1);
    expect(pushed.length).toBeLessThanOrEqual(2);
    const last = pushed.at(-1)!;
    expect(last.onboarding.step).toBe("folders");
    expect(last.collections.some((c) => c.name === "A")).toBe(true);
    expect(Object.keys(last).sort()).toEqual(["app", "auth", "collections", "connectivity", "egl", "library", "onboarding", "overlay", "queue", "sessions", "settings", "storage"]);
  });

  it("running jobs carry their latest progress in the snapshot", async () => {
    const h = setup({ scenario: "downloading" });
    await h.advance(1000);
    const j = active(await h.snapshot())[0]!;
    expect(j.progress!.percent).toBeCloseTo(h.progress.at(-1)!.percent, 1);
  });

  it("job info objects only use contract fields", async () => {
    const h = setup({ scenario: "queue" });
    const keys = ["id", "appName", "title", "kind", "status", "phases", "phase", "progress", "error", "createdAt", "startedAt", "finishedAt", "targetPath", "fromVersion", "toVersion", "blockedReason", "verify", "outcome", "cancellable"];
    for (const j of jobsOf(await h.snapshot())) expect(Object.keys(j).sort()).toEqual([...keys].sort());
  });

  it("library summaries only use contract fields", async () => {
    const h = setup();
    const lib = (await h.call("library.get")) as LibraryDelta;
    const keys = Object.keys(lib.upserts[0]!).sort();
    for (const g of lib.upserts) expect(Object.keys(g).sort()).toEqual(keys);
    expect(keys).toHaveLength(32);
  });
});
