import { describe, expect, it } from "vitest";
import type { EglInstall, GliderError, JobInfo } from "../../ipc/contract.gen";
import { batchHeadline, groupInstalls, resultPhrase, rowResult, shortFailure, summarize, unavailableLabel } from "./model";

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
  ({ id: "j", appName: "a", title: "A", kind: "import", status: "completed", phases: [], phase: null, progress: null, error: null, createdAt: "", startedAt: null, finishedAt: null, targetPath: null, fromVersion: null, toVersion: null, blockedReason: null, verify: null, outcome: null, ...patch }) as JobInfo;

const err = (code: GliderError["code"], title = "Title"): GliderError => ({ code, title, message: "m", fix: null, detail: null, appName: null });

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

  it("reads the outcome: imported, needs repair (with files when known), failed with a short reason", () => {
    const ok = job({ outcome: { kind: "success", message: "x" } });
    expect(resultPhrase(rowResult(ok), ok)).toBe("Imported");

    const repair = job({ outcome: { kind: "needsRepair", message: "older" } });
    expect(resultPhrase(rowResult(repair), repair)).toBe("Needs repair");
    const withFiles = job({ outcome: { kind: "needsRepair", message: "older" }, verify: { filesChecked: 100, filesBad: 12, filesFixed: 0, verified: false, bytesToFix: 1 } });
    expect(resultPhrase(rowResult(withFiles), withFiles)).toBe("Needs repair: 12 files");

    const failed = job({ status: "failed", error: err("filesLocked") });
    expect(resultPhrase(rowResult(failed), failed)).toBe("Failed: folder in use");
  });

  it("a completed job whose outcome failed is a failure", () => {
    const j = job({ outcome: { kind: "failed", message: "boom" }, error: err("permissionDenied") });
    expect(rowResult(j).state).toBe("failed");
    expect(resultPhrase(rowResult(j), j)).toBe("Failed: no permission");
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
    expect(batchHeadline(summarize(["imported"]))).toBe("1 game imported");
    expect(batchHeadline(summarize(["imported", "imported", "needsRepair"]))).toBe("3 games imported");
    expect(batchHeadline(summarize(["imported", "failed"]))).toBe("1 of 2 imported");
    expect(batchHeadline(summarize(["failed"]))).toBe("Nothing was imported");
    expect(summarize([]).settled).toBe(false);
  });

  it("labels the unavailable statuses in a few words", () => {
    expect(["imported", "stale", "busy", "notOwned"].map((s) => unavailableLabel(s as EglInstall["status"]))).toEqual(["Already in Glider", "Folder missing", "Launcher is busy", "Not in your library"]);
  });
});
