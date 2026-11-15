import { describe, expect, it } from "vitest";
import type { HoldInfo, JobInfo, JobProgress } from "../ipc/contract.gen";
import { cardOf, causeLabel, cdnName, etaWords, explainCause, holdCopy, ofBytes, readyOf, speedStatusOf, speedText, taskOf, type CardContext, type SpeedContext } from "./transfer";

const GB = 1024 ** 3;
const MB = 1024 ** 2;

function job(over: Partial<JobInfo> = {}): JobInfo {
  return {
    id: "j1",
    appName: "Game",
    title: "Game",
    kind: "install",
    status: "running",
    phases: ["preparing", "downloading", "installing", "finalizing"],
    phase: "downloading",
    progress: null,
    error: null,
    createdAt: "2026-01-01T00:00:00Z",
    startedAt: "2026-01-01T00:00:01Z",
    finishedAt: null,
    targetPath: "D:\\Epic Games",
    fromVersion: null,
    toVersion: "1.2.4",
    blockedReason: null,
    verify: null,
    outcome: null,
    cancellable: true,
    ...over,
  };
}

function progress(over: Partial<JobProgress> = {}): JobProgress {
  return {
    jobId: "j1",
    appName: "Game",
    phase: "downloading",
    percent: 47,
    phasePercent: 60,
    elapsedSeconds: 252,
    etaSeconds: 360,
    network: { downloadedBytes: 2.41 * GB, totalBytes: 5.12 * GB, bytesPerSecond: 48 * MB, averageBytesPerSecond: 44 * MB, peakBytesPerSecond: 91 * MB, cdn: "epicgames-download1.akamaized.net", connections: 12, ceilingBytesPerSecond: 91 * MB },
    disk: { writtenBytes: 3 * GB, totalBytes: 12 * GB, bytesPerSecond: 30 * MB, averageBytesPerSecond: 28 * MB, peakBytesPerSecond: 50 * MB, installSizeBytes: 12 * GB },
    verify: null,
    filesDone: 1204,
    filesTotal: 3310,
    mode: "balanced",
    limiter: "network",
    limiterReason: null,
    ...over,
  };
}

describe("number formats", () => {
  it("ofBytes writes the unit once when both sides share it", () => {
    expect(ofBytes(15.8 * GB, 37.6 * GB)).toBe("15.8 of 37.6 GB");
    expect(ofBytes(659 * MB, 1.53 * GB)).toBe("659 MB of 1.5 GB");
    expect(ofBytes(0.4 * GB, 120 * GB)).toBe("410 MB of 120 GB");
  });
  it("speeds: one decimal under 100, whole numbers from 100, nothing when there is none", () => {
    expect(speedText(3.9 * MB)).toBe("3.9 MB/s");
    expect(speedText(247 * MB)).toBe("247 MB/s");
    expect(speedText(1.8 * GB)).toBe("1.8 GB/s");
    expect(speedText(0)).toBeNull();
    expect(speedText(null)).toBeNull();
  });
  it("time left in words for a screen reader", () => {
    expect(etaWords(20)).toBe("less than a minute");
    expect(etaWords(60)).toBe("1 minute");
    expect(etaWords(7 * 60)).toBe("7 minutes");
    expect(etaWords(97 * 60)).toBe("1 hour 37 minutes");
    expect(etaWords(2 * 3600)).toBe("2 hours");
  });
});

const ctx: CardContext = { speedLimitBytesPerSecond: 20 * MB, path: "D:\\Epic Games", chosenMode: "balanced", effectiveMode: "balanced", modeReason: null, hold: null };
const card = (j: JobInfo, p: JobProgress | null, c: Partial<CardContext> = {}) => cardOf(j, p, { ...ctx, ...c });
/** The whole step line as it reads. */
const lineOf = (v: ReturnType<typeof card>) => v.line.join(" · ") + (v.extra ? ` · ${v.extra}` : "");

describe("cardOf: what the card says per state", () => {
  it("downloading: time left leads, one line with the step, its bytes and its speed, the whole-job percent", () => {
    const v = card(job(), progress({ etaSeconds: 97 * 60 }));
    expect(v.lead).toEqual({ kind: "time", text: "1 h 37 min" });
    expect(lineOf(v)).toBe("Downloading · 2.4 of 5.1 GB · 48.0 MB/s");
    expect(v.percent).toBe(47);
    expect(v.started).toBe(true);
    expect(v.cause).toBeNull();
    expect(v.chart).toBe("network");
    expect(v.etaWords).toBe("1 hour 37 minutes");
  });

  it("no estimate yet: the step in words, never elapsed time", () => {
    const v = card(job(), progress({ etaSeconds: null }));
    expect(v.lead).toEqual({ kind: "state", text: "Downloading…" });
    expect(JSON.stringify(v)).not.toMatch(/elapsed/i);
  });

  it("starting and preparing: an indeterminate bar, no percent, no line", () => {
    const starting = card(job({ status: "running", progress: null, phase: null }), null);
    expect(starting).toMatchObject({ lead: { kind: "state", text: "Starting…" }, indeterminate: true, started: false, line: [] });
    const preparing = card(job({ phase: "preparing" }), progress({ phase: "preparing", network: null, disk: null, etaSeconds: null }));
    expect(preparing).toMatchObject({ lead: { kind: "state", text: "Preparing…" }, indeterminate: true, started: false });
  });

  it("slow drive: the cause is named and amber, the ceiling rides along for the chart", () => {
    const p = progress({ limiter: "disk", network: { ...progress().network!, bytesPerSecond: 6.5 * MB, ceilingBytesPerSecond: 58.8 * MB } });
    const v = card(job(), p);
    expect(v.cause).toMatchObject({ label: "Limited by drive D:", status: { cause: "disk", kind: "slow" } });
    expect(v.ceiling).toBe(58.8 * MB);
    // No cause, no ceiling line: a healthy download has nothing to be measured against.
    expect(card(job(), progress({ limiter: "network" })).ceiling).toBeNull();
  });

  it("a limit the player chose is named neutral, with its words in the disclosure", () => {
    const limit = card(job(), progress({ limiter: "speedLimit" }));
    expect(limit.cause).toMatchObject({ label: "Your 20.0 MB/s limit", status: { kind: "limit" } });
    expect(card(job(), progress({ limiter: "mode", mode: "background" })).cause?.label).toBe("Background mode");
    const throttle = { effectiveMode: "background" as const, modeReason: "Background mode while Fortnite is running" };
    expect(card(job(), progress({ limiter: "network" }), throttle).cause?.label).toBe("Slowed while you play");
    expect(card(job(), progress({ limiter: "cpu" })).cause?.label).toBe("Limited by the processor");
    expect(card(job(), progress({ limiter: "cdn" })).cause?.label).toBe("Server is slow");
  });

  it("installing keeps a time left (the whole job's), with the drive's bytes and speed and a disk chart", () => {
    const v = card(job({ phase: "installing" }), progress({ phase: "installing", network: null, etaSeconds: 170 }));
    expect(v.lead).toEqual({ kind: "time", text: "3 min" });
    expect(lineOf(v)).toBe("Installing · 3.0 of 12.0 GB · 30.0 MB/s");
    expect(v.chart).toBe("disk");
    expect(v.ceiling).toBeNull();
  });

  it("verifying: no download figure, found damage set apart; repairing names the count", () => {
    const verifying = card(
      job({ kind: "verify", phase: "verifying" }),
      progress({ phase: "verifying", network: null, disk: null, etaSeconds: 120, verify: { filesChecked: 10, filesTotal: 20, bytesChecked: 3.6 * GB, bytesTotal: 7.7 * GB, readBytesPerSecond: 1.8 * GB, badFiles: 3, fixedFiles: 0 } }),
    );
    expect(lineOf(verifying)).toBe("Checking files · 3.6 of 7.7 GB · 1.8 GB/s · 3 to repair");
    expect(verifying.cause).toBeNull();
    expect(verifying.chart).toBeNull();
    const repairing = card(job({ kind: "verify", phase: "repairing" }), progress({ phase: "repairing", verify: { filesChecked: 20, filesTotal: 20, bytesChecked: 7 * GB, bytesTotal: 7 * GB, readBytesPerSecond: 0, badFiles: 10, fixedFiles: 0 } }));
    expect(repairing.line[0]).toBe("Repairing 10 files");
  });

  it("moving: its own bytes, no chart; finalizing and the short steps say it in the headline with no line", () => {
    const moving = card(job({ kind: "move", phase: "moving" }), progress({ phase: "moving", network: null, etaSeconds: 40 }));
    expect(lineOf(moving)).toBe("Moving · 3.0 of 12.0 GB · 30.0 MB/s");
    expect(moving.chart).toBeNull();
    for (const [phase, text] of [
      ["finalizing", "Almost done"],
      ["removing", "Removing files…"],
      ["importing", "Linking the install…"],
    ] as const) {
      const v = card(job({ phase }), progress({ phase, network: null, disk: null, etaSeconds: 10 }));
      expect(v.lead).toEqual({ kind: "state", text });
      expect(v.line).toEqual([]);
    }
  });

  it("paused: one phrase for the time ('2 min left when you resume'), never 'about under a minute'", () => {
    const p = progress({
      etaSeconds: null,
      network: { ...progress().network!, downloadedBytes: 659 * MB, totalBytes: 1.53 * GB, averageBytesPerSecond: 8 * MB },
      disk: { ...progress().disk!, writtenBytes: 600 * MB, totalBytes: 1.53 * GB, averageBytesPerSecond: 20 * MB },
    });
    const v = card(job({ status: "paused" }), p);
    expect(v.lead).toEqual({ kind: "state", text: "Paused" });
    expect(lineOf(v)).toBe("Downloading paused · 659 MB of 1.5 GB · 2 min left when you resume");
    expect(v.speed).toBeNull();
    expect(v.moving).toBe(false);
  });

  it("paused with under a minute left reads as one phrase too", () => {
    const v = card(job({ status: "paused" }), progress({ etaSeconds: 20 }));
    expect(lineOf(v)).toMatch(/· Under a minute left when you resume$/);
    expect(lineOf(v)).not.toMatch(/about/i);
  });

  it("no time phrase on any state stacks a qualifier ('about under', 'left left', 'elapsed')", () => {
    const states = [
      card(job(), progress({ etaSeconds: 10 })),
      card(job({ status: "paused" }), progress({ etaSeconds: 10 })),
      card(job({ status: "paused" }), progress({ etaSeconds: null })),
      card(job({ status: "queued", progress: null }), null, { hold: { kind: "game", subject: "Fortnite" } }),
    ];
    for (const v of states) {
      const text = [v.lead.text, ...v.line].join(" | ");
      expect(text).not.toMatch(/about|elapsed|left left|average|peak|queued/i);
    }
  });

  it("paused before it ever started has a lead and nothing to count", () => {
    const v = card(job({ status: "paused", progress: null, phase: null }), null);
    expect(v.lead.text).toBe("Paused");
    expect(v.line).toEqual([]);
    expect(v.started).toBe(false);
  });
});

describe("cardOf: waiting, never 'Queued'", () => {
  const game: HoldInfo = { kind: "game", subject: "Fortnite" };

  it("a job that never started under a game hold is 'queued' in Core and reads as waiting for the game", () => {
    const v = card(job({ status: "queued", progress: null, phase: null }), null, { hold: game });
    expect(v.lead).toEqual({ kind: "state", text: "Waiting for Fortnite to close" });
    expect(v.line).toEqual(["Downloads pause while you play and continue on their own"]);
    expect(v.held?.setting).toBe("whilePlaying");
    expect(v.started).toBe(false);
    expect(v.indeterminate).toBe(false);
    expect(v.moving).toBe(false);
  });

  it("one that had started keeps its percent and bytes, then says it continues", () => {
    const v = card(job({ status: "waiting", blockedReason: "Paused while Fortnite is running" }), progress(), { hold: game });
    expect(v.started).toBe(true);
    expect(v.percent).toBe(47);
    expect(lineOf(v)).toBe("2.4 of 5.1 GB · Downloads pause while you play and continue on their own");
    expect(v.speed).toBeNull();
    expect(v.cause).toBeNull();
  });

  it("offline, Epic launcher and others each have their own words and no setting unless the player chose it", () => {
    expect(card(job({ status: "queued", progress: null }), null, { hold: { kind: "offline", subject: null } })).toMatchObject({
      lead: { text: "Waiting for an internet connection" },
      line: ["Continues on its own when you're back online"],
      held: { setting: null },
    });
    const egl = card(job({ status: "waiting", progress: null, blockedReason: "Epic Games Launcher is updating this game, so Glider will wait and continue automatically." }), null, { hold: { kind: "egl", subject: null } });
    expect(egl.lead.text).toBe("Waiting for Epic Games Launcher");
    expect(egl.line).toEqual(["It's updating this game. Glider continues on its own"]);
    expect(holdCopy({ kind: "egl", subject: null }, "Epic Games Launcher is installing this game").line).toContain("installing");
    expect(holdCopy({ kind: "game", subject: null }).lead).toBe("Waiting for a game to close");
    expect(holdCopy({ kind: "other", subject: "Waiting for Fortnite's cloud saves to sync." }).lead).toBe("Waiting for Fortnite's cloud saves to sync");
  });

  it("a waiting job with no hold from Core still says what Core said; a queued one with none is just starting", () => {
    const w = card(job({ status: "waiting", progress: null, blockedReason: "Another task is using the game files" }), null);
    expect(w.lead.text).toBe("Another task is using the game files");
    expect(card(job({ status: "queued", progress: null }), null).lead.text).toBe("Starting…");
  });

  it("a user pause wins over a hold", () => {
    expect(card(job({ status: "paused" }), progress(), { hold: game }).lead.text).toBe("Paused");
  });
});

describe("speedStatusOf", () => {
  const sctx: SpeedContext = { speedLimitBytesPerSecond: 20 * MB, path: "D:\\Epic Games", chosenMode: "balanced", effectiveMode: "balanced", modeReason: null };
  const status = (p: JobProgress, c: Partial<SpeedContext> = {}, through: "network" | "write" | "read" | null = "network", j: JobInfo = job()) => speedStatusOf(j, p, through, { ...sctx, ...c });

  it("is silent when the network is the limit (the normal case), the job is paused, or the phase has no network", () => {
    expect(status(progress({ limiter: "network" }))).toBeNull();
    expect(status(progress({ limiter: "paused" }))).toBeNull();
    expect(status(progress({ limiter: "unknown" }))).toBeNull();
    expect(status(progress({ limiter: "disk" }), {}, "network", job({ status: "paused" }))).toBeNull();
    expect(status(progress({ limiter: "disk" }), {}, null)).toBeNull();
  });

  it("a slow drive, processor or server is the amber kind; a limit the user chose is the neutral one, linked to its setting", () => {
    expect(status(progress({ limiter: "disk" }))).toMatchObject({ cause: "disk", kind: "slow", setting: null });
    expect(status(progress({ limiter: "cpu" }))).toMatchObject({ cause: "cpu", kind: "slow" });
    expect(status(progress({ limiter: "cdn" }))).toMatchObject({ cause: "cdn", kind: "slow", setting: "cdn" });
    expect(status(progress({ limiter: "speedLimit" }))).toMatchObject({ cause: "speedLimit", kind: "limit", setting: "speedLimit" });
    expect(status(progress({ limiter: "mode", mode: "background" }))).toMatchObject({ cause: "mode", kind: "limit", setting: "downloadMode" });
  });

  it("says nothing about disk for verify reads or the install tail", () => {
    expect(status(progress({ limiter: "disk", network: null }), {}, "write")).toBeNull();
    expect(status(progress({ limiter: "disk", network: null }), {}, "read")).toBeNull();
  });

  it("priority: a throttle Glider applied on its own beats the disk, the disk beats the processor", () => {
    const throttle = { effectiveMode: "background" as const, modeReason: "Background mode while Fortnite is running" };
    expect(status(progress({ limiter: "disk" }), throttle)).toMatchObject({ cause: "playing", kind: "limit", reason: "Background mode while Fortnite is running.", setting: "whilePlaying" });
    // Idle boost only adds speed: it is not a limit and shows nothing by itself.
    expect(status(progress({ limiter: "network" }), { effectiveMode: "full", modeReason: "Full speed while the PC is idle" })).toBeNull();
    expect(status(progress({ limiter: "disk" }))!.cause).toBe("disk");
    expect(status(progress({ limiter: "cpu" }))!.cause).toBe("cpu");
  });

  it("the throttle for a game is about the network phases only", () => {
    const throttle = { effectiveMode: "background" as const, modeReason: "Background mode while Fortnite is running" };
    expect(status(progress({ limiter: "disk", network: null }), throttle, "write")).toBeNull();
  });
});

describe("causes in words", () => {
  const sctx: SpeedContext = { speedLimitBytesPerSecond: 8 * MB, path: "D:\\Epic Games", chosenMode: "balanced", effectiveMode: "balanced", modeReason: null };
  const explain = (limiter: JobProgress["limiter"]) => {
    const p = progress({ limiter, mode: limiter === "mode" ? "background" : "balanced" });
    const st = speedStatusOf(job(), p, "network", sctx)!;
    return { label: causeLabel(st, sctx, p), e: explainCause(st, sctx) };
  };

  it("the drive: its letter in the label and the advice, no setting to change", () => {
    const { label, e } = explain("disk");
    expect(label).toBe("Limited by drive D:");
    expect(e.title).toBe("Drive D: is the limit");
    expect(e.advice).toHaveLength(3);
    expect(e.advice[0]).toContain("D:");
    expect(e.link).toBeNull();
  });
  it("the choices link to their Settings rows; the server links to the picker", () => {
    expect(explain("speedLimit").e).toMatchObject({ title: "Held to your 8.0 MB/s limit", link: { focus: "speedLimit", label: "Change limit" } });
    expect(explain("mode").e).toMatchObject({ title: "Background mode is on", link: { focus: "downloadMode", label: "Change mode" } });
    expect(explain("cdn").e.link).toEqual({ focus: "cdn", label: "Choose a server" });
    expect(explain("cpu").e).toMatchObject({ title: "The processor is busy", link: null });
  });
  it("never the old verdict", () => {
    for (const l of ["disk", "cpu", "cdn", "speedLimit", "mode"] as const) expect(explain(l).label).not.toMatch(/slow download/i);
  });
});

describe("task, ready and the rest", () => {
  it("the Task field says what kind of job this is", () => {
    expect(taskOf(job())).toBe("Install, version 1.2.4");
    expect(taskOf(job({ kind: "update", fromVersion: "1.2.3", toVersion: "1.2.4" }))).toBe("Update, 1.2.3 → 1.2.4");
    expect(taskOf(job({ kind: "move", targetPath: "E:\\Epic Games\\Fortnite" }))).toBe("Move to E:\\Epic Games\\Fortnite");
    expect(taskOf(job({ kind: "verify" }))).toBe("Verify files, repair what's damaged");
  });

  it("ready: an install or an update is ready to play; a verify, a repair and a move say what happened and have no Play", () => {
    const done = { status: "completed" as const, startedAt: "2026-01-01T10:00:00Z", finishedAt: "2026-01-01T10:28:00Z" };
    expect(readyOf(job(done))).toEqual({ lead: "Ready to play", line: "Installed in 28 min.", play: true });
    expect(readyOf(job({ ...done, kind: "update" })).line).toBe("Updated in 28 min.");
    expect(readyOf(job({ ...done, kind: "verify", verify: { filesChecked: 100, filesBad: 0, filesFixed: 0, verified: true, bytesToFix: 0 } }))).toMatchObject({ lead: "All files are fine", play: false });
    expect(readyOf(job({ ...done, kind: "verify", verify: { filesChecked: 100, filesBad: 10, filesFixed: 10, verified: true, bytesToFix: 0 } }))).toMatchObject({ lead: "Repaired 10 files", play: false });
    expect(readyOf(job({ ...done, kind: "move", targetPath: "E:\\Games" }))).toMatchObject({ lead: "Moved to E:", play: false });
  });

  it("maps CDN hosts to providers", () => {
    expect(cdnName("fastly-download.epicgames.com")).toBe("Fastly");
    expect(cdnName("download.epicgames.com")).toBe("Epic");
    expect(cdnName("example.net")).toBe("example.net");
  });
});
