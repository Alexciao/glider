import { describe, expect, it } from "vitest";
import { candidateIssue, candidateStatus, deriveHero, isBusy, isUsableCandidate, usableCandidates } from "./derive";
import { snapshotFor } from "../test/fixtures";

describe("deriveHero: the one big button follows real state", () => {
  const cases: [Parameters<typeof snapshotFor>[0], string, string][] = [
    ["fresh", "signIn", "Sign in"],
    ["installed", "play", "Play"],
    ["updateAvailable", "update", "Update"],
    ["downloading", "working", "Installing"],
    ["eglBusy", "waiting", "Waiting"],
    ["authExpired", "signIn", "Sign in again"],
    ["playing", "playing", "Playing"],
    ["signedIn", "install", "Install"],
  ];
  it.each(cases)("%s → %s", async (scenario, kind, label) => {
    const hero = deriveHero(await snapshotFor(scenario));
    expect(hero.kind).toBe(kind);
    expect(hero.label).toBe(label);
  });

  it("explains waiting on Epic Games Launcher in plain words", async () => {
    const hero = deriveHero(await snapshotFor("eglBusy"));
    expect(hero.status.text).toMatch(/Epic Games Launcher is updating Fortnite, so Glider will wait/);
    expect(hero.status.tone).toBe("warning");
  });

  it("offers Resume for a paused job and carries the job", async () => {
    const snap = await snapshotFor("downloading");
    const paused = { ...snap, jobs: snap.jobs.map((j) => (j.status === "running" ? { ...j, status: "paused" as const } : j)) };
    const hero = deriveHero(paused);
    expect(hero.kind).toBe("resume");
    expect(hero.target).toBe("resume");
    expect(hero.job?.status).toBe("paused");
  });

  it("walks the launch lifecycle", async () => {
    const snap = await snapshotFor("installed");
    const at = (runState: typeof snap.game.runState) => deriveHero({ ...snap, game: { ...snap.game, runState } });
    expect(at("preparing").kind).toBe("launching");
    expect(at("installingPrerequisites").label).toBe("Setting up…");
    expect(at("launching").label).toBe("Launching…");
    expect(at("running").kind).toBe("playing");
    expect(at("closing").label).toBe("Closing…");
    expect(at("launching").disabled).toBe(true);
  });

  it("shows Repair for a broken install and Ready to play otherwise", async () => {
    const snap = await snapshotFor("installed");
    expect(deriveHero(snap).status.text).toBe("Ready to play");
    expect(deriveHero({ ...snap, game: { ...snap.game, installStatus: "broken" } }).kind).toBe("repair");
  });

  it("reports busy while a job runs or the game is running", async () => {
    expect(isBusy(await snapshotFor("downloading"))).toBe(true);
    expect(isBusy(await snapshotFor("playing"))).toBe(true);
    expect(isBusy(await snapshotFor("installed"))).toBe(false);
  });
});

describe("EGL candidates", () => {
  it("separates usable installs from stale ones, with a human reason", async () => {
    const snap = await snapshotFor("eglMultiple");
    const [a, b] = snap.egl.candidates;
    expect(isUsableCandidate(a!)).toBe(true);
    expect(isUsableCandidate(b!)).toBe(false);
    expect(candidateIssue(a!)).toBeNull();
    expect(candidateIssue(b!)).toMatch(/no longer exists/);
    expect(usableCandidates(snap.egl)).toHaveLength(1);
    expect(candidateStatus(a!, snap.game.latestVersion)).toEqual({ tone: "warning", text: "Update needed" });
    expect(candidateStatus(b!, snap.game.latestVersion).text).toBe("Unavailable");
  });
});
