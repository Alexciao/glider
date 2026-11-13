import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RPC_METHODS, type AppSnapshot, type JobInfo, type RpcMethodName, type Settings } from "../contract.gen";
import { GiB } from "./constants";
import { SCENARIO_NAMES, type ScenarioName } from "./options";
import { disposeAll, setup } from "./test-utils";

beforeEach(() => {
  vi.useFakeTimers();
  vi.spyOn(console, "debug").mockImplementation(() => {});
});
afterEach(() => {
  disposeAll();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

const LIVE = ["running", "paused", "waiting"];
const TERMINAL = ["completed", "failed", "cancelled"];

function expectValidSnapshot(s: AppSnapshot): void {
  // One wire shape: JSON round-trip loses nothing (no undefined, no NaN).
  expect(JSON.parse(JSON.stringify(s))).toStrictEqual(s);
  expect(s.app.version).toBe("0.1.0");
  expect(s.app.legendaryVersion).toBe("0.21.1");
  expect(s.app.autoWorkers).toBe(8);
  expect(s.app.autoSharedMemoryMb).toBe(1024);
  expect(s.game.title).toBe("Fortnite");
  expect(s.settings.eglSync).toBe(s.egl.syncEnabled);
  expect(s.jobs.filter((j) => LIVE.includes(j.status)).length).toBeLessThanOrEqual(1);
  if (s.game.installed) expect(["installed", "broken"]).toContain(s.game.installStatus);
  if (s.game.installStatus === "installed") expect(s.game.installed).not.toBeNull();
  if (s.game.updateAvailable) expect(s.game.installed).not.toBeNull();
  if (s.auth.status === "signedIn") expect(s.auth.account).not.toBeNull();
  for (const j of s.jobs) {
    expect(Number.isNaN(Date.parse(j.createdAt))).toBe(false);
    expect(j.finishedAt !== null).toBe(TERMINAL.includes(j.status));
    if (j.error) expect(j.status).toBe("failed");
    if (j.progress) {
      expect(j.progress.jobId).toBe(j.id);
      expect(j.progress.percent).toBeGreaterThanOrEqual(0);
      expect(j.progress.percent).toBeLessThanOrEqual(100);
    }
    if (j.status === "waiting") expect(j.blockedReason).toBeTruthy();
  }
  for (const c of s.egl.candidates) if (c.isStale) expect(c.folderExists && c.executableFound).toBe(false);
}

describe("scenarios", () => {
  it.each(SCENARIO_NAMES)("%s produces a valid snapshot", async (name) => {
    const t = setup({ scenario: name });
    const s = await t.snapshot();
    expectValidSnapshot(s);
    expect(s.app.backdrop).toBe("solid");
    expect(s.game.art?.heroUrl).toMatch(/^data:image\/svg\+xml/);
  });

  const expectations: Record<ScenarioName, (s: AppSnapshot) => void> = {
    fresh: (s) => {
      expect(s.auth.status).toBe("signedOut");
      expect(s.onboarding).toEqual({ completed: false, step: "welcome" });
      expect(s.egl.launcherInstalled).toBe(false);
      expect(s.game.installStatus).toBe("notInstalled");
    },
    eglSignedOut: (s) => {
      expect(s.auth.status).toBe("signedOut");
      expect(s.egl.candidates).toHaveLength(1);
      expect(s.onboarding.step).toBe("welcome");
    },
    signedIn: (s) => {
      expect(s.auth.account?.displayName).toBe("Marlowe");
      expect(s.onboarding).toEqual({ completed: false, step: "chooseInstall" });
      expect(s.egl.candidates).toHaveLength(0);
    },
    eglDetected: (s) => {
      expect(s.egl.candidates).toHaveLength(1);
      expect(s.egl.candidates[0]).toMatchObject({ installPath: "D:\\Epic Games\\Fortnite", source: "epicGamesLauncher", isStale: false, version: s.game.latestVersion });
      expect(s.onboarding.step).toBe("chooseInstall");
    },
    eglMultiple: (s) => {
      expect(s.egl.candidates).toHaveLength(2);
      expect(s.egl.candidates.filter((c) => c.isStale)).toHaveLength(1);
      expect(s.egl.candidates[0]!.version).not.toBe(s.game.latestVersion);
    },
    installed: (s) => {
      expect(s.game.installStatus).toBe("installed");
      expect(s.game.installed).toMatchObject({ installPath: "D:\\Epic Games\\Fortnite", sharedWithEgl: true });
      expect(s.game.updateAvailable).toBe(false);
      expect(s.onboarding.completed).toBe(true);
      expect(s.jobs).toHaveLength(1);
      expect(s.jobs[0]).toMatchObject({ kind: "update", status: "completed" });
    },
    updateAvailable: (s) => {
      expect(s.game.updateAvailable).toBe(true);
      expect(s.game.downloadSizeBytes).toBe(Math.round(1.2 * GiB));
      expect(s.game.installed!.version).not.toBe(s.game.latestVersion);
    },
    downloading: (s) => {
      expect(s.game.installStatus).toBe("installing");
      const running = s.jobs.find((j) => j.status === "running")!;
      expect(running.kind).toBe("install");
      expect(running.progress!.phase).toBe("downloading");
      expect(running.progress!.percent).toBeCloseTo(42, 0);
      expect(s.jobs.some((j) => j.kind === "verify" && j.status === "completed")).toBe(true);
    },
    eglBusy: (s) => {
      expect(s.egl.busyWithGame).toBe(true);
      expect(s.egl.launcherRunning).toBe(true);
      expect(s.jobs[0]).toMatchObject({ kind: "update", status: "waiting", blockedReason: "Epic Games Launcher is updating Fortnite" });
    },
    diskFull: (s) => {
      const failed = s.jobs.find((j) => j.status === "failed")!;
      expect(failed.error).toMatchObject({ code: "insufficientSpace", fix: "freeSpace" });
      expect(s.game.updateAvailable).toBe(true);
    },
    authExpired: (s) => {
      expect(s.auth.status).toBe("expired");
      expect(s.auth.account?.displayName).toBe("Marlowe");
      expect(s.game.latestVersion).toBeNull();
      expect(s.game.updateAvailable).toBe(false);
    },
    playing: (s) => {
      expect(s.game.runState).toBe("running");
      const since = Date.parse(s.game.runningSince!);
      expect(Date.now() - since).toBeGreaterThan(71 * 60_000);
      expect(Date.now() - since).toBeLessThan(73 * 60_000);
    },
  };

  it.each(SCENARIO_NAMES)("%s has the documented shape", async (name) => {
    const t = setup({ scenario: name });
    expectations[name](await t.snapshot());
  });

  it("art:none yields null art; backdrop is configurable", async () => {
    const t = setup({ scenario: "installed", art: "none", backdrop: "mica" });
    const s = await t.snapshot();
    expect(s.game.art).toBeNull();
    expect(s.app.backdrop).toBe("mica");
  });

  it("loadScenario resets state and pushes a snapshot", async () => {
    const t = setup({ scenario: "fresh" });
    await t.snapshot();
    const before = t.states.length;
    t.controller.loadScenario("installed");
    await t.advance(1);
    expect(t.states.length).toBeGreaterThan(before);
    expect(t.states.at(-1)!.game.installStatus).toBe("installed");
    expect(t.controller.getScenario()).toBe("installed");
    const listener = vi.fn();
    t.controller.subscribe(listener);
    t.controller.loadScenario("playing");
    await t.advance(1);
    expect(listener).toHaveBeenCalled();
  });

  it("settles in-flight calls as cancelled when the scenario is reloaded", async () => {
    const t = setup({ scenario: "eglDetected", latency: 1 });
    const p = t.client.call("egl.rescan").then(
      () => "ok",
      (e: { error: { code: string } }) => e.error.code,
    );
    await t.advance(1);
    t.controller.loadScenario("fresh");
    await t.advance(2000);
    expect(await p).toBe("cancelled");
  });
});

describe("every method responds", () => {
  const settings: Settings = {
    downloadWorkers: null,
    sharedMemoryMb: null,
    preferredCdn: null,
    autoUpdate: true,
    autoUpdateOnlyWhenIdle: true,
    updateCheckIntervalMinutes: 30,
    startWithWindows: false,
    startMinimized: false,
    closeToTray: true,
    minimizeToTrayWhilePlaying: true,
    launchArgs: "",
    logLevel: "info",
    theme: "system",
    eglSync: true,
    notifications: true,
    defaultInstallBase: null,
  };
  const params: Partial<Record<RpcMethodName, unknown>> = {
    "app.getLogs": { maxLines: 50 },
    "app.openExternal": { url: "https://example.com" },
    "app.openPath": { path: "C:\\" },
    "app.pickFolder": { initialPath: null, title: null },
    "game.adopt": { candidateId: null, path: "F:\\Games\\Fortnite", verify: "auto" },
    "game.install": { basePath: "D:\\Games" },
    "game.move": { destinationBase: "F:\\Games" },
    "game.planInstall": { basePath: null },
    "game.uninstall": { mode: "gliderOnly" },
    "game.validateFolder": { path: "F:\\Games\\Fortnite" },
    "jobs.cancel": { jobId: "job-999" },
    "jobs.dismiss": { jobId: "job-999" },
    "jobs.pause": { jobId: "job-999" },
    "jobs.resume": { jobId: "job-999" },
    "settings.update": settings,
  };

  it("answers all %d RPC methods (result or a GliderError), never 'Unknown method'", async () => {
    const t = setup({ scenario: "installed" });
    for (const method of RPC_METHODS) {
      const p = (t.client.call as (m: RpcMethodName, p?: unknown) => Promise<unknown>)(method, params[method]);
      try {
        await t.settle(p);
      } catch (e) {
        const err = (e as { error?: { code: string; detail: string | null; title: string; message: string } }).error;
        expect(err, `${method} rejected with a non-RpcError`).toBeDefined();
        expect(err!.title.length).toBeGreaterThan(0);
        expect(err!.detail ?? "", method).not.toMatch(/Unknown method/);
      }
    }
  });

  it("rejects an unknown method with invalidRequest", async () => {
    const t = setup();
    const p = (t.client.call as (m: string) => Promise<unknown>)("nope.nothing");
    await expect(t.settle(p)).rejects.toMatchObject({ error: { code: "invalidRequest" } });
  });
});

describe("small calls", () => {
  it("pickFolder cycles deterministic results by title", async () => {
    const t = setup();
    const base = (title: string | null) => t.call("app.pickFolder", { initialPath: null, title }).then((r) => r.path);
    expect([await base("Choose install location"), await base(null), await base(null), await base(null)]).toEqual([
      "D:\\Games",
      "E:\\Games",
      "C:\\Games",
      "D:\\Games",
    ]);
    expect([await base("Locate existing install"), await base("Locate your Fortnite folder")]).toEqual(["F:\\Games\\Fortnite", "C:\\Users\\Public\\Documents"]);
  });

  it("validateFolder accepts folders named fortnite only", async () => {
    const t = setup();
    expect(await t.call("game.validateFolder", { path: "F:\\Games\\Fortnite" })).toMatchObject({ valid: true, gamePath: "F:\\Games\\Fortnite", reason: null });
    expect(await t.call("game.validateFolder", { path: "f:\\games\\FORTNITE\\" })).toMatchObject({ valid: true });
    const bad = await t.call("game.validateFolder", { path: "C:\\Users\\Public\\Documents" });
    expect(bad).toEqual({
      valid: false,
      gamePath: null,
      version: null,
      reason: "This folder doesn't contain Fortnite. Choose the folder that contains FortniteGame and Engine.",
    });
  });

  it("returns ~300 log lines, honouring maxLines", async () => {
    const t = setup();
    const all = await t.call("app.getLogs", { maxLines: 5000 });
    const lines = all.text.split("\n");
    expect(lines.length).toBeGreaterThan(280);
    expect(lines.length).toBeLessThan(340);
    expect(all.text).toMatch(/INFO/);
    expect(all.text).toMatch(/WARN/);
    expect(all.text).toMatch(/ERROR/);
    expect(all.logFile).toMatch(/^C:\\Users\\alex\\AppData\\Local\\Glider\\logs\\glider-\d{8}\.log$/);
    const tail = await t.call("app.getLogs", { maxLines: 20 });
    expect(tail.text.split("\n")).toHaveLength(20);
    expect(lines.slice(-20)).toEqual(tail.text.split("\n"));
  });

  it("lists Legendary 0.21.1 under GPL-3.0 with a source link", async () => {
    const t = setup();
    const { items } = await t.call("app.getLicenses");
    const legendary = items.find((i) => i.name === "Legendary")!;
    expect(legendary).toMatchObject({ version: "0.21.1", license: "GPL-3.0", url: "https://github.com/legendary-gl/legendary/tree/0.21.1" });
    expect(legendary.text).toContain("GPL");
    expect(items.map((i) => i.name)).toEqual(expect.arrayContaining([".NET", "Velopack", "React", "Zustand", "Inter"]));
  });

  it("toggleMaximize flips state and emits a window event", async () => {
    const t = setup();
    expect(await t.call("window.getInfo")).toEqual({ maximized: false, focused: true });
    await t.call("window.toggleMaximize");
    expect(t.windows.at(-1)).toEqual({ maximized: true, focused: true });
    expect((await t.call("window.getInfo")).maximized).toBe(true);
    await t.call("window.toggleMaximize");
    expect(t.windows.at(-1)!.maximized).toBe(false);
  });

  it("settings.update echoes and mirrors eglSync; reset restores defaults", async () => {
    const t = setup({ scenario: "installed" });
    const cur = (await t.snapshot()).settings;
    const next = await t.call("settings.update", { ...cur, theme: "light", eglSync: false, downloadWorkers: 12 });
    expect(next).toMatchObject({ theme: "light", eglSync: false, downloadWorkers: 12 });
    const s = await t.snapshot();
    expect(s.settings.theme).toBe("light");
    expect(s.egl.syncEnabled).toBe(false);
    const reset = await t.call("settings.reset");
    expect(reset).toMatchObject({ theme: "system", eglSync: true, downloadWorkers: null, closeToTray: true, updateCheckIntervalMinutes: 30 });
    expect((await t.snapshot()).egl.syncEnabled).toBe(true);
  });

  it("completeOnboarding marks onboarding done", async () => {
    const t = setup({ scenario: "signedIn" });
    await t.call("app.completeOnboarding");
    expect((await t.snapshot()).onboarding).toEqual({ completed: true, step: "done" });
  });

  it("checkForUpdates sets checkingForUpdates in a pushed snapshot, then resolves", async () => {
    const t = setup({ scenario: "installed" });
    await t.snapshot();
    const res = await t.call("game.checkForUpdates");
    expect(res).toMatchObject({ updateAvailable: false });
    expect(t.states.some((s) => s.game.checkingForUpdates)).toBe(true);
    expect(t.states.at(-1)!.game.checkingForUpdates).toBe(false);
  });

  it("egl.rescan refreshes lastScan", async () => {
    const t = setup({ scenario: "eglDetected" });
    const before = (await t.snapshot()).egl.lastScan!;
    await t.advance(5000);
    const egl = await t.call("egl.rescan");
    expect(Date.parse(egl.lastScan!)).toBeGreaterThan(Date.parse(before));
  });
});

describe("planning and space", () => {
  it("plans against the free space of the chosen drive", async () => {
    const t = setup({ scenario: "signedIn" });
    const d = await t.call("game.planInstall", { basePath: "D:\\Games" });
    expect(d).toMatchObject({ gamePath: "D:\\Games\\Fortnite", hasEnoughSpace: true, existingInstallFound: false });
    expect(d.downloadSizeBytes).toBe(Math.round(27.9 * GiB));
    expect(d.installSizeBytes).toBe(Math.round(96.3 * GiB));
    const e = await t.call("game.planInstall", { basePath: "E:\\Games" });
    expect(e.hasEnoughSpace).toBe(false);
    expect(e.freeBytes).toBe(Math.round(18.4 * GiB));
    expect((await t.call("game.planInstall", { basePath: "C:\\Games" })).hasEnoughSpace).toBe(false);
    expect((await t.call("game.planInstall", { basePath: null })).basePath).toBe("D:\\Games");
  });

  it("flags an existing install at the target and the diskFull squeeze", async () => {
    const t = setup({ scenario: "eglDetected" });
    expect(await t.call("game.planInstall", { basePath: "D:\\Epic Games" })).toMatchObject({ existingInstallFound: true });
    t.controller.loadScenario("diskFull");
    await t.advance(1);
    const p = await t.call("game.planInstall", { basePath: "D:\\Games" });
    expect(p.hasEnoughSpace).toBe(false);
    expect(p.freeBytes).toBe(Math.round(12.6 * GiB));
  });

  it("install without enough space fails with the exact C# copy", async () => {
    const t = setup({ scenario: "signedIn" });
    const err = await t.callError("game.install", { basePath: "E:\\Games" });
    expect(err.error).toMatchObject({
      code: "insufficientSpace",
      title: "Not enough disk space",
      message: "This needs 96.3 GB but only 18.4 GB is free. Free up space or choose another drive.",
      fix: "freeSpace",
    });
  });

  it("update without headroom fails in diskFull", async () => {
    const t = setup({ scenario: "diskFull" });
    const err = await t.callError("game.update");
    expect(err.error).toMatchObject({ code: "insufficientSpace", fix: "freeSpace" });
    expect(err.error.message).toContain("4.8 GB");
    expect(err.error.message).toContain("921.6 MB");
  });
});

describe("guards", () => {
  it("requires sign-in for game operations", async () => {
    const t = setup({ scenario: "fresh" });
    expect((await t.callError("game.planInstall", { basePath: null })).error.code).toBe("notSignedIn");
    expect((await t.callError("game.install", { basePath: "D:\\Games" })).error).toMatchObject({ code: "notSignedIn", fix: "signIn" });
  });

  it("reports an expired session with a sign-in fix", async () => {
    const t = setup({ scenario: "authExpired" });
    expect((await t.callError("game.launch")).error).toMatchObject({ code: "authExpired", fix: "signIn" });
    expect((await t.callError("game.checkForUpdates")).error.code).toBe("authExpired");
  });

  it("rejects install when installed and notInstalled operations when not", async () => {
    const installed = setup({ scenario: "installed" });
    expect((await installed.callError("game.install", { basePath: "D:\\Games" })).error.code).toBe("alreadyInstalled");
    const empty = setup({ scenario: "signedIn" });
    expect((await empty.callError("game.launch")).error.code).toBe("notInstalled");
    expect((await empty.callError("game.verify")).error.code).toBe("notInstalled");
  });

  it("blocks verify, move and uninstall while the game runs", async () => {
    const t = setup({ scenario: "playing" });
    expect((await t.callError("game.verify")).error).toMatchObject({ code: "gameRunning", fix: "closeGame" });
    expect((await t.callError("game.move", { destinationBase: "F:\\Games" })).error.code).toBe("gameRunning");
    expect((await t.callError("game.uninstall", { mode: "gliderOnly" })).error.code).toBe("gameRunning");
  });

  it("blocks launching while an update is running", async () => {
    const t = setup({ scenario: "updateAvailable" });
    await t.call("game.update");
    expect((await t.callError("game.launch")).error.code).toBe("busy");
  });

  it("blocks launching while Epic Games Launcher is busy with the game", async () => {
    const t = setup({ scenario: "eglBusy" });
    expect((await t.callError("game.launch")).error).toMatchObject({ code: "eglBusy", fix: "waitForEgl" });
  });
});

describe("auth", () => {
  it("signs in through signingIn, then advances onboarding", async () => {
    const t = setup({ scenario: "fresh", latency: 1 });
    const result = await t.call("auth.signIn");
    expect(result).toMatchObject({ status: "signedIn", account: { displayName: "Marlowe", avatarUrl: null } });
    expect(t.states.some((s) => s.auth.status === "signingIn")).toBe(true);
    const s = await t.snapshot();
    expect(s.auth.status).toBe("signedIn");
    expect(s.onboarding).toEqual({ completed: false, step: "chooseInstall" });
    expect(s.game.latestVersion).not.toBeNull();
    expect(s.game.downloadSizeBytes).toBe(Math.round(27.9 * GiB));
  });

  it("takes about 1.4 s at latency 1", async () => {
    const t = setup({ scenario: "fresh", latency: 1 });
    let done = false;
    void t.client.call("auth.signIn").then(() => (done = true));
    await t.advance(1000);
    expect(done).toBe(false);
    await t.advance(700);
    expect(done).toBe(true);
  });

  it("fail=authCancelled rejects and leaves the user signed out", async () => {
    const t = setup({ scenario: "fresh", fail: "authCancelled" });
    const err = await t.callError("auth.signIn");
    expect(err.error).toMatchObject({ code: "authCancelled", title: "Sign-in cancelled", fix: null });
    expect((await t.snapshot()).auth.status).toBe("signedOut");
    expect((await t.call("auth.signIn")).status).toBe("signedIn");
  });

  it("re-signing in from an expired session clears the expiry and finds the update state", async () => {
    const t = setup({ scenario: "authExpired" });
    await t.call("auth.signIn");
    const s = await t.snapshot();
    expect(s.auth.status).toBe("signedIn");
    expect(s.game.latestVersion).not.toBeNull();
  });

  it("browser fallback: signInWithCode accepts the pasted JSON page or the bare code and signs in", async () => {
    const code = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
    const t = setup({ scenario: "fresh", latency: 1 });
    await t.call("auth.openBrowserLogin");
    expect((await t.call("auth.signInWithCode", { code: `{"redirectUrl":"x","sid":null,"authorizationCode":"${code}"}` })).status).toBe("signedIn");
    const t2 = setup({ scenario: "fresh", latency: 1 });
    expect((await t2.call("auth.signInWithCode", { code: ` "${code}" ` })).status).toBe("signedIn");
  });

  it("browser fallback: text without a code is rejected and leaves the user signed out", async () => {
    const t = setup({ scenario: "fresh", latency: 1 });
    expect((await t.callError("auth.signInWithCode", { code: "hello" })).error).toMatchObject({ code: "authFailed", fix: null });
    expect((await t.snapshot()).auth.status).toBe("signedOut");
  });

  it("signOut clears the account", async () => {
    const t = setup({ scenario: "installed" });
    expect(await t.call("auth.signOut")).toEqual({ status: "signedOut", account: null });
    expect((await t.snapshot()).auth.account).toBeNull();
  });
});

describe("install lifecycle", () => {
  it("runs a fresh install through every phase to completion", async () => {
    const t = setup({ scenario: "signedIn" });
    const job = await t.call("game.install", { basePath: "D:\\Games" });
    expect(job).toMatchObject({ kind: "install", status: "running", targetPath: "D:\\Games\\Fortnite" });
    expect((await t.snapshot()).game.installStatus).toBe("installing");

    await t.advance(120_000);
    const s = await t.snapshot();
    expectValidSnapshot(s);
    expect(s.game.installStatus).toBe("installed");
    expect(s.game.installed).toMatchObject({
      installPath: "D:\\Games\\Fortnite",
      version: s.game.latestVersion,
      sharedWithEgl: false,
      needsVerification: false,
    });
    expect(s.game.updateAvailable).toBe(false);
    expect(s.jobs.find((j) => j.id === job.id)).toMatchObject({ status: "completed", error: null });
    expect(t.notices.some((n) => n.level === "success" && n.title === "Fortnite is ready to play")).toBe(true);

    const phases = [...new Set(t.progress.map((p) => p.phase))];
    expect(phases).toEqual(["preparing", "downloading", "verifying", "finalizing"]);
    expect(t.progress.at(-1)!.percent).toBe(100);
  });

  it("emits progress at 10 Hz and a state snapshot on phase changes", async () => {
    const t = setup({ scenario: "downloading" });
    await t.snapshot();
    t.progress.length = 0;
    const statesBefore = t.states.length;
    await t.advance(3000);
    expect(t.progress.length).toBeGreaterThanOrEqual(29);
    expect(t.progress.length).toBeLessThanOrEqual(31);
    // No per-tick state spam while a single phase is running.
    expect(t.states.length - statesBefore).toBeLessThanOrEqual(1);
  });

  it("keeps progress monotonic in the download phase and ETA counting down", async () => {
    const t = setup({ scenario: "downloading" });
    await t.snapshot();
    t.progress.length = 0;
    await t.advance(25_000);
    const dl = t.progress.filter((p) => p.phase === "downloading");
    expect(dl.length).toBeGreaterThan(200);
    for (let i = 1; i < dl.length; i++) {
      expect(dl[i]!.percent).toBeGreaterThanOrEqual(dl[i - 1]!.percent);
      expect(dl[i]!.downloadedBytes).toBeGreaterThanOrEqual(dl[i - 1]!.downloadedBytes);
      expect(dl[i]!.percent).toBeLessThanOrEqual(100);
    }
    // Average ETA over the first, middle and last tenth: it counts down even with speed dips in between.
    const tenth = Math.floor(dl.length / 10);
    const meanEta = (from: number) => dl.slice(from, from + tenth).reduce((s, p) => s + p.etaSeconds!, 0) / tenth;
    const mid = Math.floor((dl.length - tenth) / 2);
    expect(meanEta(0)).toBeGreaterThan(meanEta(mid));
    expect(meanEta(mid)).toBeGreaterThan(meanEta(dl.length - tenth));
    // Smoothed speed varies far less than the raw speed.
    const raw = dl.map((p) => p.rawDownloadBytesPerSecond);
    const smooth = dl.map((p) => p.downloadBytesPerSecond);
    const spread = (xs: number[]) => Math.max(...xs) - Math.min(...xs);
    expect(spread(smooth)).toBeLessThan(spread(raw));
  });

  it("pre-seeds a late-joining UI with ~90 synthetic samples ending at the current progress", async () => {
    const t = setup({ scenario: "downloading" });
    const snap = await t.snapshot();
    expect(t.progress.length).toBe(90);
    expect(t.progress.at(-1)).toEqual(snap.jobs.find((j) => j.status === "running")!.progress);
    for (let i = 1; i < t.progress.length; i++) expect(t.progress[i]!.downloadedBytes).toBeGreaterThanOrEqual(t.progress[i - 1]!.downloadedBytes);
    // Only the first snapshot is replayed.
    await t.snapshot();
    expect(t.progress.length).toBeLessThan(120);
  });

  it("respects ?seek", async () => {
    const t = setup({ scenario: "downloading", seek: 0.8 });
    const running = (await t.snapshot()).jobs.find((j) => j.status === "running")!;
    expect(running.progress!.percent).toBeCloseTo(80, 0);
  });

  it("pause stops progress events and the clock; resume continues from the same point", async () => {
    const t = setup({ scenario: "downloading" });
    await t.snapshot();
    await t.advance(1000);
    const paused = await t.call("jobs.pause", { jobId: "job-2" });
    expect(paused.status).toBe("paused");
    const at = (await t.job("job-2"))!.progress!.downloadedBytes;
    t.progress.length = 0;
    await t.advance(3000);
    expect(t.progress).toHaveLength(0);
    expect((await t.job("job-2"))!.progress!.downloadedBytes).toBe(at);
    expect((await t.job("job-2"))!.status).toBe("paused");

    const resumed = await t.call("jobs.resume", { jobId: "job-2" });
    expect(resumed.status).toBe("running");
    await t.advance(1000);
    expect(t.progress.length).toBeGreaterThanOrEqual(9);
    expect((await t.job("job-2"))!.progress!.downloadedBytes).toBeGreaterThan(at);
  });

  it("cancelling an install returns to notInstalled", async () => {
    const t = setup({ scenario: "downloading" });
    const cancelled = await t.call("jobs.cancel", { jobId: "job-2" });
    expect(cancelled.status).toBe("cancelled");
    expect(cancelled.finishedAt).not.toBeNull();
    const s = await t.snapshot();
    expect(s.game.installStatus).toBe("notInstalled");
    expect(s.game.installed).toBeNull();
    expect(s.jobs.find((j) => j.id === "job-2")!.status).toBe("cancelled");
    t.progress.length = 0;
    await t.advance(2000);
    expect(t.progress).toHaveLength(0);
  });

  it("cancelling an update keeps the install", async () => {
    const t = setup({ scenario: "updateAvailable" });
    const job = await t.call("game.update");
    await t.advance(2000);
    await t.call("jobs.cancel", { jobId: job.id });
    const s = await t.snapshot();
    expect(s.game.installStatus).toBe("installed");
    expect(s.game.updateAvailable).toBe(true);
  });

  it("queues a second job behind the active one and starts it afterwards", async () => {
    const t = setup({ scenario: "updateAvailable" });
    const update = await t.call("game.update");
    const verify = await t.call("game.verify");
    expect(update.status).toBe("running");
    expect(verify.status).toBe("queued");
    expect((await t.snapshot()).jobs.filter((j) => LIVE.includes(j.status))).toHaveLength(1);

    let sawVerifyRunning = false;
    for (let i = 0; i < 400 && !sawVerifyRunning; i++) {
      await t.advance(250);
      const s = t.states.at(-1)!;
      const u = s.jobs.find((j) => j.id === update.id);
      const v = s.jobs.find((j) => j.id === verify.id);
      if (v?.status === "running") {
        sawVerifyRunning = true;
        expect(u?.status).toBe("completed");
      }
    }
    expect(sawVerifyRunning).toBe(true);
    await t.advance(60_000);
    const s = await t.snapshot();
    expect(s.jobs.find((j) => j.id === update.id)!.status).toBe("completed");
    expect(s.jobs.find((j) => j.id === verify.id)!.status).toBe("completed");
    expect(s.game.installed!.version).toBe(s.game.latestVersion);
    expect(s.game.updateAvailable).toBe(false);
  });

  it("a queued job can be cancelled without disturbing the active one", async () => {
    const t = setup({ scenario: "updateAvailable" });
    await t.call("game.update");
    const verify = await t.call("game.verify");
    await t.call("jobs.cancel", { jobId: verify.id });
    const s = await t.snapshot();
    expect(s.jobs.find((j) => j.id === verify.id)!.status).toBe("cancelled");
    expect(s.jobs.filter((j) => j.status === "running")).toHaveLength(1);
  });

  it("pausing or cancelling a finished job is an error; dismiss removes history", async () => {
    const t = setup({ scenario: "installed" });
    expect((await t.callError("jobs.pause", { jobId: "job-1" })).error.code).toBe("invalidRequest");
    expect((await t.callError("jobs.cancel", { jobId: "job-1" })).error.code).toBe("invalidRequest");
    expect((await t.callError("jobs.pause", { jobId: "job-404" })).error.code).toBe("invalidRequest");
    await t.call("jobs.dismiss", { jobId: "job-1" });
    expect((await t.snapshot()).jobs).toHaveLength(0);
  });

  it("cannot dismiss an active job", async () => {
    const t = setup({ scenario: "downloading" });
    expect((await t.callError("jobs.dismiss", { jobId: "job-2" })).error.code).toBe("busy");
  });

  it("keeps at most 8 finished jobs", async () => {
    const t = setup({ scenario: "installed" });
    for (let i = 0; i < 12; i++) {
      const j = await t.call("game.verify");
      await t.call("jobs.cancel", { jobId: j.id });
    }
    const s = await t.snapshot();
    expect(s.jobs.filter((j) => TERMINAL.includes(j.status)).length).toBeLessThanOrEqual(8);
  });

  it("verify runs only the verifying phase and completes with a notice", async () => {
    const t = setup({ scenario: "installed" });
    const job = await t.call("game.verify");
    expect(job.kind).toBe("verify");
    await t.advance(40_000);
    expect([...new Set(t.progress.map((p) => p.phase))]).toEqual(["verifying"]);
    expect((await t.job(job.id))!.status).toBe("completed");
    expect(t.notices.some((n) => n.title === "Files verified")).toBe(true);
  });
});

describe("EGL busy", () => {
  it("auto-resumes a waiting job after ~14 s", async () => {
    const t = setup({ scenario: "eglBusy" });
    expect((await t.job("job-1"))!.status).toBe("waiting");
    t.progress.length = 0;
    await t.advance(5000);
    expect(t.progress).toHaveLength(0);
    await t.advance(10_000);
    const j = await t.job("job-1");
    expect(j!.status).toBe("running");
    expect(j!.blockedReason).toBeNull();
    expect((await t.snapshot()).egl.busyWithGame).toBe(false);
    expect(t.progress.length).toBeGreaterThan(0);
  });

  it("setEglBusy toggles running ⇄ waiting", async () => {
    const t = setup({ scenario: "eglBusy" });
    t.controller.setEglBusy(false);
    await t.advance(500);
    expect((await t.job("job-1"))!.status).toBe("running");
    t.controller.setEglBusy(true);
    await t.advance(1);
    const waiting = await t.job("job-1");
    expect(waiting).toMatchObject({ status: "waiting", blockedReason: "Epic Games Launcher is updating Fortnite" });
    expect((await t.snapshot()).egl.busyWithGame).toBe(true);
  });

  it("creates new write jobs as waiting (not an error) while EGL is busy", async () => {
    const t = setup({ scenario: "eglBusy" });
    t.controller.setEglBusy(false);
    await t.advance(10);
    await t.call("jobs.cancel", { jobId: "job-1" });
    t.controller.setEglBusy(true);
    await t.advance(10);
    const v = await t.call("game.verify");
    expect(v.status).toBe("waiting");
    expect(v.blockedReason).toBeTruthy();
  });

  it("jobs.resume forces a waiting job to run", async () => {
    const t = setup({ scenario: "eglBusy", freeze: true });
    expect((await t.call("jobs.resume", { jobId: "job-1" })).status).toBe("running");
  });

  it("does not auto-resume when frozen", async () => {
    const t = setup({ scenario: "eglBusy", freeze: true });
    await t.advance(30_000);
    expect((await t.job("job-1"))!.status).toBe("waiting");
  });
});

describe("injected failures", () => {
  it("failNext fails exactly the next mutating call", async () => {
    const t = setup({ scenario: "signedIn" });
    t.controller.failNext("network");
    const err = await t.callError("game.install", { basePath: "D:\\Games" });
    expect(err.error).toMatchObject({ code: "network", title: "Can't reach Epic", fix: "retry" });
    const job = await t.call("game.install", { basePath: "D:\\Games" });
    expect(job.status).toBe("running");
  });

  it("the fail option applies to the first mutating call", async () => {
    const t = setup({ scenario: "installed", fail: "permissionDenied" });
    const err = await t.callError("game.move", { destinationBase: "F:\\Games" });
    expect(err.error).toMatchObject({ code: "permissionDenied", fix: "chooseFolder" });
  });

  it.each([
    ["network", "retry"],
    ["cdnUnavailable", "retry"],
    ["filesLocked", "retry"],
    ["legendaryFailed", "retry"],
    ["insufficientSpace", "freeSpace"],
    ["corrupted", "repair"],
  ] as const)("failActiveJob(%s) fails the job and notifies with fix %s", async (code, fix) => {
    const t = setup({ scenario: "downloading" });
    t.controller.failActiveJob(code);
    await t.advance(5);
    const s = await t.snapshot();
    const job = s.jobs.find((j) => j.id === "job-2")!;
    expect(job.status).toBe("failed");
    expect(job.error).toMatchObject({ code, fix });
    expect(job.finishedAt).not.toBeNull();
    expect(s.game.installStatus).toBe("notInstalled");
    expect(t.notices.at(-1)).toMatchObject({ level: "error", action: fix });
    expectValidSnapshot(s);
  });

  it("a corrupted failure marks an install broken and the next verify becomes a repair", async () => {
    const t = setup({ scenario: "installed" });
    await t.call("game.verify");
    t.controller.failActiveJob("corrupted");
    await t.advance(5);
    expect((await t.snapshot()).game.installStatus).toBe("broken");
    const repair = await t.call("game.verify");
    expect(repair.kind).toBe("repair");
    await t.advance(60_000);
    const s = await t.snapshot();
    expect(s.game.installStatus).toBe("installed");
    expect(t.progress.some((p) => p.phase === "downloading")).toBe(true);
  });

  it("emitNotice pushes a notice with a generated id", async () => {
    const t = setup();
    t.controller.emitNotice({ level: "warning", title: "Heads up", message: null, action: "openLogs" });
    t.controller.emitNotice({ level: "info", title: "Second", message: "msg", action: null });
    expect(t.notices).toHaveLength(2);
    expect(t.notices[0]!.id).not.toBe(t.notices[1]!.id);
    expect(t.notices[0]).toMatchObject({ level: "warning", action: "openLogs" });
  });
});

describe("freeze", () => {
  it("keeps every value stable while still emitting at 10 Hz", async () => {
    const t = setup({ scenario: "downloading", freeze: true });
    await t.snapshot();
    t.progress.length = 0;
    await t.advance(2000);
    expect(t.progress.length).toBeGreaterThanOrEqual(19);
    const first = t.progress[0]!;
    for (const p of t.progress) expect(p).toEqual(first);
    expect(first.downloadBytesPerSecond).toBeGreaterThan(50e6);
    const job = (await t.snapshot()).jobs.find((j) => j.id === "job-2")!;
    expect(job.progress).toEqual(first);
  });

  it("holds a launch at 'launching'", async () => {
    const t = setup({ scenario: "installed", freeze: true });
    let launched = false;
    void t.client.call("game.launch").then(() => (launched = true));
    await t.advance(10_000);
    expect(launched).toBe(false);
    expect((await t.snapshot()).game.runState).toBe("launching");
  });
});

describe("adopting an existing install", () => {
  it("uses an EGL candidate in one step, without a download", async () => {
    const t = setup({ scenario: "eglDetected" });
    const res = await t.call("game.adopt", { candidateId: "egl-1", path: null, verify: "auto" });
    expect(res.verifyJob).toBeNull();
    expect(res.game.installStatus).toBe("installed");
    expect(res.game.installed).toMatchObject({ installPath: "D:\\Epic Games\\Fortnite", sharedWithEgl: true, needsVerification: false });
    expect(res.game.updateAvailable).toBe(false);
    const s = await t.snapshot();
    expect(s.jobs.some((j) => j.kind === "import" && j.status === "completed")).toBe(true);
    expect(s.onboarding.step).toBe("done");
    expect(s.egl.syncAvailable).toBe(true);
    expect(t.notices.some((n) => n.title === "Fortnite is ready to play")).toBe(true);
    expect(t.progress.every((p) => p.phase === "preparing")).toBe(true);
  });

  it("an older candidate means an update is available afterwards", async () => {
    const t = setup({ scenario: "eglMultiple" });
    const res = await t.call("game.adopt", { candidateId: "egl-1", path: null, verify: "auto" });
    expect(res.game.updateAvailable).toBe(true);
    expect(res.game.downloadSizeBytes).toBe(Math.round(1.2 * GiB));
  });

  it("a stale candidate is rejected with invalidFolder", async () => {
    const t = setup({ scenario: "eglMultiple" });
    const err = await t.callError("game.adopt", { candidateId: "egl-2", path: null, verify: "auto" });
    expect(err.error).toMatchObject({ code: "invalidFolder", fix: "chooseFolder" });
    expect((await t.snapshot()).game.installStatus).toBe("notInstalled");
  });

  it("background verify returns the verify job and flags needsVerification until it finishes", async () => {
    const t = setup({ scenario: "eglDetected" });
    const res = await t.call("game.adopt", { candidateId: "egl-1", path: null, verify: "background" });
    expect(res.verifyJob).toMatchObject({ kind: "verify" });
    expect(res.game.installed!.needsVerification).toBe(true);
    await t.advance(60_000);
    const s = await t.snapshot();
    expect(s.game.installed!.needsVerification).toBe(false);
    expect(s.jobs.find((j) => j.id === res.verifyJob!.id)!.status).toBe("completed");
  });

  it("full verify resolves only after the verify job completes", async () => {
    const t = setup({ scenario: "eglDetected" });
    const res = await t.call("game.adopt", { candidateId: "egl-1", path: null, verify: "full" });
    expect(res.verifyJob).toMatchObject({ kind: "verify", status: "completed" });
    expect(res.game.installed!.needsVerification).toBe(false);
  });

  it("adopts a located folder (not shared with EGL) and rejects a bad one", async () => {
    const t = setup({ scenario: "signedIn" });
    const bad = await t.callError("game.adopt", { candidateId: null, path: "C:\\Users\\Public\\Documents", verify: "auto" });
    expect(bad.error.code).toBe("invalidFolder");
    const res = await t.call("game.adopt", { candidateId: null, path: "F:\\Games\\Fortnite", verify: "auto" });
    expect(res.game.installed).toMatchObject({ installPath: "F:\\Games\\Fortnite", sharedWithEgl: false });
    expect(res.game.updateAvailable).toBe(true);
  });

  it("requires a candidate or a path", async () => {
    const t = setup({ scenario: "eglDetected" });
    expect((await t.callError("game.adopt", { candidateId: null, path: null, verify: "auto" })).error.code).toBe("invalidRequest");
  });
});

describe("launching", () => {
  it("walks preparing → launching → running and resolves when the game is up", async () => {
    const t = setup({ scenario: "installed", latency: 1 });
    const runStates = (): string[] => [...new Set(t.states.map((s) => s.game.runState))];
    const result = await t.call("game.launch");
    expect(result).toEqual({ started: true, eacInstalled: true });
    expect(runStates()).toEqual(["preparing", "launching", "running"]);
    const s = await t.snapshot();
    expect(s.game.runState).toBe("running");
    expect(Date.now() - Date.parse(s.game.runningSince!)).toBeLessThan(5000);
  });

  it("installs anti-cheat first when it is missing", async () => {
    const t = setup({ scenario: "installed", eacInstalled: false, latency: 1 });
    expect(await t.call("game.launch")).toEqual({ started: true, eacInstalled: true });
    const seen = [...new Set(t.states.map((s) => s.game.runState))];
    expect(seen).toEqual(["preparing", "installingPrerequisites", "launching", "running"]);
    // Only the first launch needs it.
    t.controller.endGame();
    await t.advance(2000);
    t.states.length = 0;
    await t.call("game.launch");
    expect([...new Set(t.states.map((s) => s.game.runState))]).not.toContain("installingPrerequisites");
  });

  it("endGame goes through closing back to idle with an info notice", async () => {
    const t = setup({ scenario: "playing", latency: 1 });
    t.controller.endGame();
    await t.advance(10);
    expect((await t.snapshot()).game.runState).toBe("closing");
    await t.advance(800);
    const s = await t.snapshot();
    expect(s.game).toMatchObject({ runState: "idle", runningSince: null });
    expect(t.notices.at(-1)).toMatchObject({ level: "info", title: "Fortnite closed" });
  });

  it("the game exits on its own after ~2 minutes (not when frozen)", async () => {
    const t = setup({ scenario: "playing", latency: 1 });
    await t.advance(119_000);
    expect((await t.snapshot()).game.runState).toBe("running");
    await t.advance(3000);
    expect((await t.snapshot()).game.runState).toBe("idle");
    const frozen = setup({ scenario: "playing", freeze: true });
    await frozen.advance(300_000);
    expect((await frozen.snapshot()).game.runState).toBe("running");
  });
});

describe("move and uninstall", () => {
  it("moves the install and keeps EGL pointed at it when sync is on", async () => {
    const t = setup({ scenario: "installed" });
    const job = await t.call("game.move", { destinationBase: "F:\\Games" });
    expect(job).toMatchObject({ kind: "move", targetPath: "F:\\Games\\Fortnite" });
    await t.advance(90_000);
    const s = await t.snapshot();
    expect(s.game.installed!.installPath).toBe("F:\\Games\\Fortnite");
    expect(s.game.installed!.sharedWithEgl).toBe(true);
    expect(s.egl.candidates[0]!.installPath).toBe("F:\\Games\\Fortnite");
    expect([...new Set(t.progress.map((p) => p.phase))]).toEqual(["moving"]);
  });

  it("with sync off, EGL loses track of a moved install", async () => {
    const t = setup({ scenario: "installed" });
    const cur = (await t.snapshot()).settings;
    await t.call("settings.update", { ...cur, eglSync: false });
    await t.call("game.move", { destinationBase: "F:\\Games" });
    await t.advance(90_000);
    const s = await t.snapshot();
    expect(s.game.installed!.sharedWithEgl).toBe(false);
    expect(s.egl.candidates[0]).toMatchObject({ isStale: true, folderExists: false });
    expectValidSnapshot(s);
  });

  it("refuses to move onto a drive without space or into the same folder", async () => {
    const t = setup({ scenario: "installed" });
    expect((await t.callError("game.move", { destinationBase: "E:\\Games" })).error.code).toBe("insufficientSpace");
    expect((await t.callError("game.move", { destinationBase: "D:\\Epic Games" })).error.code).toBe("invalidFolder");
  });

  it("'Remove from Glider only' keeps the files and the EGL candidate", async () => {
    const t = setup({ scenario: "installed" });
    await t.call("game.uninstall", { mode: "gliderOnly" });
    await t.advance(5000);
    const s = await t.snapshot();
    expect(s.game.installStatus).toBe("notInstalled");
    expect(s.game.installed).toBeNull();
    expect(s.egl.candidates).toHaveLength(1);
    expect(t.notices.at(-1)).toMatchObject({ title: "Removed from Glider" });
  });

  it("'Uninstall and delete files' drops the EGL candidate too", async () => {
    const t = setup({ scenario: "installed" });
    await t.call("game.uninstall", { mode: "deleteFiles" });
    await t.advance(5000);
    const s = await t.snapshot();
    expect(s.game.installStatus).toBe("notInstalled");
    expect(s.egl.candidates).toHaveLength(0);
    expect(t.notices.at(-1)).toMatchObject({ title: "Fortnite uninstalled" });
  });
});

describe("snapshots on every change", () => {
  it("coalesces same-tick changes into one push and always pushes full snapshots", async () => {
    const t = setup({ scenario: "installed" });
    await t.snapshot();
    t.states.length = 0;
    const a = t.client.call("game.verify");
    const b = t.client.call("jobs.dismiss", { jobId: "job-1" });
    await t.settle(Promise.all([a, b]));
    expect(t.states.length).toBeGreaterThan(0);
    for (const s of t.states) expectValidSnapshot(s);
    const last = t.states.at(-1)!;
    expect(last.jobs.map((j) => j.kind)).toEqual(["verify"]);
  });

  it("job states carry the latest progress", async () => {
    const t = setup({ scenario: "downloading" });
    await t.snapshot();
    await t.advance(1500);
    const s = await t.snapshot();
    const job = s.jobs.find((j) => j.id === "job-2")!;
    expect(job.progress).toEqual(t.progress.at(-1));
  });
});

describe("types stay in sync with the contract", () => {
  it("covers every method in RPC_METHODS (compile-time via Handlers; sanity check here)", () => {
    expect(RPC_METHODS.length).toBeGreaterThanOrEqual(34);
  });

  it("job info objects only use contract fields", async () => {
    const t = setup({ scenario: "installed" });
    const j: JobInfo = await t.call("game.verify");
    expect(Object.keys(j).sort()).toEqual(
      ["blockedReason", "createdAt", "error", "finishedAt", "fromVersion", "id", "kind", "progress", "status", "targetPath", "toVersion", "verify"].sort(),
    );
  });
});
