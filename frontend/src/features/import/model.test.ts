import { describe, expect, it } from "vitest";
import type { EglInstall, GliderError, JobInfo } from "../../ipc/contract.gen";
import { batchHeadline, batchNote, groupInstalls, resultPhrase, rowResult, shortFailure, summarize, unavailableLabel, verifyJobFor } from "./model";

const install = (id: string, status: EglInstall["status"], title = id): EglInstall => ({
  id,
  source: "epicGamesLauncher",
  appName: id,
  title,
  installPath: `D:\\Epic Games\\${title}`,
  drive: "D:\\",
  version: "1.0.0",
  sizeBytes: 1000,
  status,
  drift: "none",
  reason: null,
  artUrl: null,
});

const job = (patch: Partial<JobInfo>): JobInfo =>
  ({ id: "j", appName: "a", title: "A", kind: "import", status: "completed", phases: [], phase: null, progress: null, error: null, createdAt: "2026-01-01T10:00:00Z", startedAt: null, finishedAt: null, targetPath: null, fromVersion: null, toVersion: null, blockedReason: null, verify: null, outcome: null, ...patch }) as JobInfo;

const err = (code: GliderError["code"], title = "Title"): GliderError => ({ code, title, message: "m", fix: null, detail: null, appName: null, requiredBytes: null, drive: null });

describe("groupInstalls", () => {
  it("splits by what can be done and sorts each group by title", () => {
    const g = groupInstalls([install("b", "importable", "Beta"), install("a", "importable", "alpha"), install("c", "imported"), install("d", "stale"), install("e", "busy"), install("f", "notOwned")]);
    expect(g.ready.map((i) => i.id)).toEqual(["a", "b"]);
    expect(g.imported.map((i) => i.id)).toEqual(["c"]);
    expect(g.unavailable.map((i) => i.id).sort()).toEqual(["d", "e", "f"]);
  });
});

describe("rowResult and resultPhrase", () => {
  it("a job we haven't heard of is waiting; queued and paused are waiting too", () => {
    expect(rowResult(null).state).toBe("waiting");
    expect(rowResult(job({ status: "queued" })).state).toBe("waiting");
    expect(rowResult(job({ status: "running" })).state).toBe("running");
  });

  it("reads the outcome: ready, a failure with a short reason", () => {
    const ok = job({ outcome: { kind: "success", message: "x" } });
    expect(resultPhrase(rowResult(ok))).toBe("Ready");

    const failed = job({ status: "failed", error: err("filesLocked") });
    expect(resultPhrase(rowResult(failed))).toBe("Failed · folder in use");
  });

  describe("an import Core is verifying (outcome needsRepair) says what the verify job says", () => {
    const verifying = job({ outcome: { kind: "needsRepair", message: "Imported; verifying the files." } });
    const report = (filesBad: number, filesFixed: number, verified: boolean) => ({ filesChecked: 100, filesBad, filesFixed, verified, bytesToFix: 1 });
    const verifyJob = (patch: Partial<JobInfo>) => job({ id: "v", kind: "verify", createdAt: "2026-01-01T10:00:05Z", ...patch });

    it("no verify job yet, or one queued or running: Checking files", () => {
      expect(resultPhrase(rowResult(verifying, null))).toBe("Checking files");
      expect(rowResult(verifying, verifyJob({ status: "queued" })).state).toBe("checking");
      expect(rowResult(verifying, verifyJob({ status: "running" })).state).toBe("checking");
    });

    it("passed: Ready; repaired on its own: Repaired · n files", () => {
      expect(resultPhrase(rowResult(verifying, verifyJob({ verify: report(0, 0, true) })))).toBe("Ready");
      expect(resultPhrase(rowResult(verifying, verifyJob({ verify: report(12, 12, true) })))).toBe("Repaired · 12 files");
      expect(resultPhrase(rowResult(verifying, verifyJob({ verify: report(1, 1, true) })))).toBe("Repaired · 1 file");
    });

    it("left damage: Needs repair · n files (the ones still bad)", () => {
      const failedCheck = verifyJob({ status: "failed", error: err("corrupted"), verify: report(14, 2, false) });
      expect(rowResult(verifying, failedCheck).state).toBe("needsRepair");
      expect(resultPhrase(rowResult(verifying, failedCheck))).toBe("Needs repair · 12 files");
      expect(resultPhrase(rowResult(verifying, verifyJob({ status: "failed", error: err("network") })))).toBe("Needs repair");
    });

    it("finds the game's newest verify job created at or after the import, and no older one", () => {
      const old = verifyJob({ id: "old", createdAt: "2025-12-31T10:00:00Z" });
      const mine = verifyJob({ id: "mine" });
      const other = verifyJob({ id: "other", appName: "b" });
      expect(verifyJobFor(verifying, [old, other])).toBeNull();
      expect(verifyJobFor(verifying, [old, other, mine])?.id).toBe("mine");
    });
  });

  it("a completed job whose outcome failed is a failure", () => {
    const j = job({ outcome: { kind: "failed", message: "boom" }, error: err("permissionDenied") });
    expect(rowResult(j).state).toBe("failed");
    expect(resultPhrase(rowResult(j))).toBe("Failed · no permission");
  });
});

describe("shortFailure", () => {
  it("maps codes to a few words, and falls back to a short title or 'see logs'", () => {
    expect(shortFailure(err("offline"), null)).toBe("no connection");
    expect(shortFailure(err("insufficientSpace"), null)).toBe("not enough space");
    expect(shortFailure(err("unknown", "Disk is read-only."), null)).toBe("disk is read-only");
    expect(shortFailure(err("unknown", "A very long title that would never fit on one short line"), null)).toBe("see logs");
    expect(shortFailure(null, null)).toBe("see logs");
  });
});

describe("summarize and batchHeadline", () => {
  it("counts and words a running and a finished batch", () => {
    expect(batchHeadline(summarize(["imported", "running", "waiting"]))).toBe("Importing 1 of 3");
    expect(batchHeadline(summarize(["imported"]))).toBe("1 game ready");
    expect(batchHeadline(summarize(["imported", "imported", "checking"]))).toBe("2 games added, 1 being checked");
    expect(batchHeadline(summarize(["checking", "checking"]))).toBe("2 games being checked");
    expect(batchHeadline(summarize(["imported", "imported", "needsRepair"]))).toBe("2 games ready, 1 needs a repair");
    expect(batchHeadline(summarize(["needsRepair", "needsRepair"]))).toBe("2 games need a repair");
    expect(batchHeadline(summarize(["imported", "needsRepair", "failed"]))).toBe("1 game ready, 1 needs a repair, 1 failed");
    expect(batchHeadline(summarize(["imported", "failed"]))).toBe("1 game ready, 1 failed");
    expect(batchHeadline(summarize(["failed"]))).toBe("Nothing was imported");
    expect(summarize([]).settled).toBe(false);
  });
  it("adds the repair note only once something needs one", () => {
    expect(batchNote(summarize(["imported"]))).toBe("Nothing was downloaded.");
    expect(batchNote(summarize(["imported", "checking"]))).toBe("Nothing was downloaded.");
    expect(batchNote(summarize(["imported", "needsRepair"]))).toBe("Nothing was downloaded. Repairs re-download only damaged files.");
    expect(batchNote(summarize(["failed"]))).toBeNull();
    expect(batchNote(summarize(["imported", "running"]))).toBeNull();
  });

  it("labels the unavailable statuses in a few words", () => {
    expect(["imported", "stale", "busy", "notOwned"].map((s) => unavailableLabel(s as EglInstall["status"]))).toEqual(["Already in Glider", "Folder missing", "Launcher is busy", "Not in your library"]);
  });
});
