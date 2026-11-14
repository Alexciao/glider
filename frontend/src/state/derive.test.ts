import { describe, expect, it } from "vitest";
import type { GameSummary } from "../ipc/contract.gen";
import { deriveHero, eglInstallStatus, importableInstalls, isBusy } from "./derive";
import { worldFor } from "../test/fixtures";

const plainInstalled = (g: GameSummary) => g.kind === "game" && g.installStatus === "installed" && g.thirdParty === "none";

describe("deriveHero: the one big button follows real state of one game", () => {
  it("a signed-out user is asked to sign in; an expired session to sign in again", async () => {
    const w = await worldFor("returning");
    const g = w.find((x) => x.kind === "game" && x.installStatus === "notInstalled");
    const out = deriveHero({ ...w.snap, auth: { status: "signedOut", account: null } }, g);
    expect(out).toMatchObject({ kind: "signIn", label: "Sign in" });
    const exp = await worldFor("authExpired");
    expect(deriveHero(exp.snap, exp.find(plainInstalled))).toMatchObject({ kind: "signIn", label: "Sign in again" });
  });

  it("installed → Play (with version); update pending → Update (with size); not installed → Install; broken → Repair", async () => {
    const w = await worldFor("returning");
    const installed = w.find((x) => plainInstalled(x) && !x.updateAvailable);
    const play = deriveHero(w.snap, installed);
    expect(play).toMatchObject({ kind: "play", label: "Play", target: "launch", disabled: false });
    expect(play.status.text).toBe("Ready to play");
    const upd = w.find((x) => x.installStatus === "installed" && x.updateAvailable);
    expect(deriveHero(w.snap, upd)).toMatchObject({ kind: "update", label: "Update", target: "update" });
    expect(deriveHero(w.snap, upd).status.sub).toMatch(/[KMG]B$/);
    expect(deriveHero(w.snap, w.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none"))).toMatchObject({ kind: "install", label: "Install", target: "install" });
    expect(deriveHero(w.snap, w.find((x) => x.installStatus === "broken"))).toMatchObject({ kind: "repair", target: "repair" });
  });

  it("names the game in its own copy instead of a hard-coded title", async () => {
    const w = await worldFor("playing");
    expect(deriveHero(w.snap, w.get("Fortnite")).status.text).toBe("Fortnite is running");
    const other = w.find((x) => plainInstalled(x) && x.appName !== "Fortnite");
    const at = (runState: GameSummary["runState"]) => deriveHero(w.snap, { ...other, runState });
    expect(at("preparing").status.text).toBe(`Starting ${other.title}`);
    expect(at("running").status.text).toBe(`${other.title} is running`);
    expect(at("closing").status.text).toBe(`${other.title} is closing`);
  });

  it("walks the launch lifecycle", async () => {
    const w = await worldFor("returning");
    const g = w.find(plainInstalled);
    const at = (runState: GameSummary["runState"]) => deriveHero(w.snap, { ...g, runState });
    expect(at("preparing").kind).toBe("launching");
    expect(at("syncingSaves").status.text).toBe("Syncing cloud saves");
    expect(at("installingPrerequisites").label).toBe("Setting up…");
    expect(at("launching").label).toBe("Launching…");
    expect(at("running").kind).toBe("playing");
    expect(at("closing").label).toBe("Closing…");
    expect(at("launching").disabled).toBe(true);
  });

  it("shows the job of THIS game only: working, waiting on EGL, paused, queued", async () => {
    const q = await worldFor("queue");
    const jobs = q.snap.queue.jobs;
    const running = jobs.find((j) => j.status === "running")!;
    const hero = deriveHero(q.snap, q.get(running.appName));
    expect(hero.kind).toBe("working");
    expect(hero.label).toBe("Installing");
    expect(hero.job?.id).toBe(running.id);
    const paused = jobs.find((j) => j.status === "paused")!;
    expect(deriveHero(q.snap, q.get(paused.appName))).toMatchObject({ kind: "resume", target: "resume" });
    const queued = jobs.find((j) => j.status === "queued")!;
    expect(deriveHero(q.snap, q.get(queued.appName))).toMatchObject({ kind: "queued" });
    const unrelated = q.find((g) => plainInstalled(g) && !jobs.some((j) => j.appName === g.appName));
    expect(deriveHero(q.snap, unrelated).kind).not.toBe("working");

    const e = await worldFor("eglBusy");
    const waiting = e.snap.queue.jobs.find((j) => j.status === "waiting")!;
    const wh = deriveHero(e.snap, e.get(waiting.appName));
    expect(wh).toMatchObject({ kind: "waiting", label: "Waiting" });
    expect(wh.status.text).toMatch(/Epic Games Launcher is updating .*, so Glider will wait/);
    expect(wh.status.tone).toBe("warning");
  });

  it("carries the job and a quiet emphasis while working", async () => {
    const d = await worldFor("downloading");
    const j = d.snap.queue.jobs.find((x) => x.status === "running")!;
    const hero = deriveHero(d.snap, d.get(j.appName), { percent: 42, phase: "downloading" });
    expect(hero).toMatchObject({ kind: "working", emphasis: "quiet", percent: 42 });
    expect(hero.status.sub).toBe("Downloading");
  });

  it("third-party and entitlement-only titles, offline and games that can't run offline", async () => {
    const w = await worldFor("returning");
    const tp = w.find((x) => x.kind === "game" && x.installStatus === "notInstallable" && x.thirdParty === "ea");
    expect(deriveHero(w.snap, tp)).toMatchObject({ kind: "play", status: { text: "Starts in EA app" } });
    const dlc = w.find((x) => x.kind === "dlc" && x.installStatus === "notInstallable");
    expect(deriveHero(w.snap, dlc)).toMatchObject({ kind: "unavailable", disabled: true });
    const off = await worldFor("offline");
    const cant = off.find((x) => plainInstalled(x) && !x.canRunOffline);
    expect(deriveHero(off.snap, cant)).toMatchObject({ disabled: true, status: { text: "Needs an internet connection" } });
    const can = off.find((x) => plainInstalled(x) && x.canRunOffline);
    expect(deriveHero(off.snap, can)).toMatchObject({ kind: "play", status: { text: "Offline" } });
    const not = off.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    expect(deriveHero(off.snap, not)).toMatchObject({ kind: "unavailable", status: { text: "You're offline" } });
  });

  it("reports busy per game: while its job runs or it is running", async () => {
    const d = await worldFor("downloading");
    const j = d.snap.queue.jobs.find((x) => x.status === "running")!;
    expect(isBusy(d.snap, j.appName)).toBe(true);
    expect(isBusy(d.snap, "someone-else")).toBe(false);
    expect(isBusy((await worldFor("playing")).snap, "Fortnite")).toBe(true);
    expect(isBusy((await worldFor("returning")).snap, "Fortnite")).toBe(false);
  });
});

describe("EGL installs", () => {
  it("only 'importable' installs can be imported; every other status explains itself", async () => {
    const w = await worldFor("firstRunSignedIn");
    const installs = w.snap.egl.installs;
    const ok = importableInstalls(installs);
    expect(ok.length).toBeGreaterThanOrEqual(5);
    expect(ok.every((i) => i.status === "importable" && i.reason === null)).toBe(true);
    for (const i of installs.filter((x) => x.status !== "importable" && x.status !== "imported")) expect(i.reason).toBeTruthy();
    expect(eglInstallStatus(ok[0]!).tone).toMatch(/success|warning/);
    expect(eglInstallStatus(installs.find((i) => i.status === "stale")!).text).toBe("Folder missing");
    expect(eglInstallStatus(installs.find((i) => i.status === "busy")!).tone).toBe("warning");
    expect(eglInstallStatus(installs.find((i) => i.status === "imported")!).text).toBe("Already in Glider");
    expect(eglInstallStatus(installs.find((i) => i.status === "notOwned")!).text).toBe("Not in your library");
    const older = installs.find((i) => i.drift === "eglNewer" && i.status === "importable");
    if (older) expect(eglInstallStatus(older).tone).toBe("warning");
  });
});
