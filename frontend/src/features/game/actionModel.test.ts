import { describe, expect, it } from "vitest";
import type { AppSnapshot, GameSummary, JobInfo, RunningGame } from "../../ipc/contract.gen";
import { worldFor, type World } from "../../test/fixtures";
import { canPause, deriveGameAction, type ActionContext } from "./actionModel";

let w: World;
const ctx = (over: Partial<ActionContext> = {}): ActionContext => ({ pending: {}, ...over });
const withJob = (snap: AppSnapshot, job: Partial<JobInfo>): AppSnapshot => ({
  ...snap,
  queue: { ...snap.queue, jobs: [{ ...snap.queue.jobs[0]!, ...job } as JobInfo, ...snap.queue.jobs.slice(1)] },
});
const job = (g: GameSummary, kind: JobInfo["kind"], status: JobInfo["status"], extra: Partial<JobInfo> = {}): Partial<JobInfo> => ({
  id: "j1",
  appName: g.appName,
  title: g.title,
  kind,
  status,
  phase: "downloading",
  progress: null,
  blockedReason: null,
  ...extra,
});

describe("deriveGameAction", () => {
  it("is built from a real mock world", async () => {
    w = await worldFor("returning");
    expect(w.games.size).toBeGreaterThan(100);
  });

  it("Play for an installed game, with one status line and no version repeated", () => {
    const g = w.find((x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none" && !x.updateAvailable && x.canRunOffline);
    const a = deriveGameAction(w.snap, g, ctx());
    expect(a).toMatchObject({ kind: "play", label: "Play", tone: "primary", intent: "launch", busy: false, disabled: false });
    expect(a.status).toEqual({ tone: "neutral", text: "Ready to play", quiet: true });
  });

  it("a pressed Play is busy with the Launching face, not a different button", () => {
    const g = w.find((x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none" && !x.updateAvailable);
    const a = deriveGameAction(w.snap, g, ctx({ pending: { launch: true } }));
    expect(a).toMatchObject({ kind: "play", label: "Launching…", busy: true, faceKey: "launching" });
  });

  it("walks the launch stages in the status line while the button holds one face", () => {
    const g = { ...w.find((x) => x.installStatus === "installed" && x.thirdParty === "none"), runState: "idle" as GameSummary["runState"] };
    const stages = [
      ["preparing", "Preparing"],
      ["syncingSaves", "Syncing saves"],
      ["installingPrerequisites", "Installing prerequisites"],
      ["launching", "Launching"],
    ] as const;
    for (const [runState, text] of stages) {
      const a = deriveGameAction(w.snap, { ...g, runState }, ctx());
      expect(a.status.text).toBe(text);
      expect(a).toMatchObject({ label: "Launching…", faceKey: "launching", busy: true });
    }
  });

  it("while running the primary is Close game and the status ticks from the session start", () => {
    const g = { ...w.find((x) => x.installStatus === "installed" && x.thirdParty === "none"), runState: "running" as const };
    const session: RunningGame = { appName: g.appName, title: g.title, state: "running", since: "2026-11-14T10:00:00Z", offline: false, untracked: false };
    const a = deriveGameAction(w.snap, g, ctx({ session }));
    expect(a).toMatchObject({ kind: "closeGame", label: "Close game", intent: "close" });
    expect(a.status).toMatchObject({ text: "Playing", since: "2026-11-14T10:00:00Z" });
  });

  it("an untracked (third-party) session says so instead of counting time", () => {
    const g = { ...w.find((x) => x.thirdParty === "ubisoft" && x.installStatus === "installed"), runState: "running" as const };
    const session: RunningGame = { appName: g.appName, title: g.title, state: "running", since: null, offline: false, untracked: true };
    expect(deriveGameAction(w.snap, g, ctx({ session })).status.text).toBe("Started in Ubisoft Connect");
  });

  it("a running install offers Pause, a running move offers Cancel, never a disabled status label", () => {
    const g = w.find((x) => x.installStatus === "installed" && x.thirdParty === "none");
    const install = deriveGameAction(withJob(w.snap, job(g, "install", "running")), g, ctx());
    expect(install).toMatchObject({ kind: "pause", label: "Pause", intent: "pause", disabled: false, tone: "quiet" });
    expect(install.status).toMatchObject({ text: "Installing", spinner: true });
    const move = deriveGameAction(withJob(w.snap, job(g, "move", "running", { phase: "moving" })), g, ctx());
    expect(move).toMatchObject({ kind: "cancel", label: "Cancel", intent: "cancel", disabled: false });
    expect(move.status.sub).toBeUndefined();
  });

  it("only install, update, repair and verify can pause", () => {
    expect((["install", "update", "repair", "verify"] as const).every(canPause)).toBe(true);
    expect((["move", "uninstall", "import", "modify", "overlay"] as const).some(canPause)).toBe(false);
  });

  it("a paused job is Resume with its kind in the status", () => {
    const g = w.find((x) => x.installStatus === "installed" && x.thirdParty === "none");
    const a = deriveGameAction(withJob(w.snap, job(g, "update", "paused")), g, ctx());
    expect(a).toMatchObject({ kind: "resume", label: "Resume", tone: "primary", intent: "resume" });
    expect(a.status.text).toBe("Update paused");
  });

  it("a waiting job says why, a queued one says when", () => {
    const g = w.find((x) => x.installStatus === "installed" && x.thirdParty === "none");
    const waiting = deriveGameAction(withJob(w.snap, job(g, "update", "waiting", { blockedReason: "Epic Games Launcher is updating it." })), g, ctx());
    expect(waiting.status.text).toBe("Epic Games Launcher is updating it, so Glider will wait");
    const queued = deriveGameAction(withJob(w.snap, job(g, "install", "queued")), g, ctx());
    expect(queued.status).toMatchObject({ text: "Install queued", sub: "Starts when the current task finishes" });
    expect(queued.intent).toBe("cancel");
  });

  it("the phase only shows when it says more than the verb", () => {
    const g = w.find((x) => x.installStatus === "installed" && x.thirdParty === "none");
    expect(deriveGameAction(withJob(w.snap, job(g, "install", "running", { phase: "downloading" })), g, ctx()).status.sub).toBe("Downloading");
    expect(deriveGameAction(withJob(w.snap, job(g, "install", "running", { phase: "installing" })), g, ctx()).status.sub).toBeUndefined();
    expect(deriveGameAction(withJob(w.snap, job(g, "verify", "running", { phase: "verifying" })), g, ctx()).status.sub).toBeUndefined();
  });

  it("Update outranks Play when an update is pending and the PC is online", () => {
    const g = w.find((x) => x.installStatus === "installed" && x.updateAvailable && x.thirdParty === "none");
    const a = deriveGameAction(w.snap, g, ctx());
    expect(a).toMatchObject({ kind: "update", label: "Update", intent: "update" });
    expect(a.status.text).toBe("Update available");
  });

  it("offline: Install is unavailable, a game that needs the internet cannot Play", () => {
    const offline: AppSnapshot = { ...w.snap, connectivity: "offline" };
    const fresh = w.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none");
    expect(deriveGameAction(offline, fresh, ctx())).toMatchObject({ kind: "unavailable", disabled: true });
    const online = w.find((x) => x.installStatus === "installed" && x.thirdParty === "none" && !x.canRunOffline);
    const a = deriveGameAction(offline, online, ctx());
    expect(a).toMatchObject({ kind: "unavailable", disabled: true });
    expect(a.status.text).toBe("Needs an internet connection");
  });

  it("signed out: Sign in; broken: Repair; not installed: Install; included DLC: nothing to do", () => {
    const g = w.find((x) => x.kind === "game" && x.installStatus === "installed" && x.thirdParty === "none");
    expect(deriveGameAction({ ...w.snap, auth: { status: "signedOut", account: null } }, g, ctx())).toMatchObject({ kind: "signIn", intent: "signIn" });
    expect(deriveGameAction(w.snap, { ...g, installStatus: "broken", updateAvailable: false }, ctx())).toMatchObject({ kind: "repair", intent: "verify" });
    expect(deriveGameAction(w.snap, w.find((x) => x.kind === "game" && x.installStatus === "notInstalled" && x.thirdParty === "none"), ctx())).toMatchObject({ kind: "install", intent: "install" });
    const dlc = w.find((x) => x.kind === "dlc" && x.installStatus === "notInstallable");
    expect(deriveGameAction(w.snap, dlc, ctx())).toMatchObject({ kind: "unavailable", disabled: true });
  });

  it("a third-party title that Epic does not install plays through its launcher", () => {
    const g = w.find((x) => x.installStatus === "notInstallable" && x.thirdParty !== "none");
    const a = deriveGameAction(w.snap, g, ctx());
    expect(a).toMatchObject({ kind: "play", intent: "launch" });
    expect(a.status.text).toMatch(/^Plays through /);
  });
});
