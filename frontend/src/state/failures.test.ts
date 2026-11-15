import { describe, expect, it } from "vitest";
import type { GliderError, JobInfo, StorageState } from "../ipc/contract.gen";
import { failedJobsOf, failedMeta, isFailed, failureActions, failureMessage, freeOnDrive, hasSpaceFailure, pastJobsOf, spaceNeedOf, updatesToOffer } from "./failures";
import { FIX_LABELS } from "./ui";

const GB = 1024 ** 3;
const MB = 1024 ** 2;

const error = (over: Partial<GliderError> = {}): GliderError => ({
  code: "filesLocked",
  title: "Game files are in use",
  message: "Close Sunken Keep II and any program using its files, then try again.",
  fix: "retry",
  detail: null,
  appName: "Keep",
  requiredBytes: null,
  drive: null,
  ...over,
});

const failed = (over: Partial<JobInfo> = {}): JobInfo => ({
  id: "j1",
  appName: "Keep",
  title: "Sunken Keep II",
  kind: "update",
  status: "failed",
  phases: ["preparing", "downloading", "installing", "finalizing"],
  phase: "downloading",
  progress: null,
  error: error(),
  createdAt: "2026-11-15T10:00:00Z",
  startedAt: "2026-11-15T10:00:05Z",
  finishedAt: "2026-11-15T10:20:00Z",
  targetPath: null,
  fromVersion: "1.0",
  toVersion: "1.1",
  blockedReason: null,
  verify: null,
  outcome: { kind: "failed", message: "x" },
  cancellable: false,
  ...over,
});

const space = (over: Partial<GliderError> = {}): JobInfo => failed({ error: error({ code: "insufficientSpace", fix: "freeSpace", message: "This needs 4.8 GB but only 921.6 MB is free.", requiredBytes: 4.8 * GB, drive: "D:", ...over }) });

const storage = (freeD: number | null): Pick<StorageState, "drives" | "folders"> => ({
  drives: [
    { root: "C:\\", label: "Windows", freeBytes: 200 * GB, totalBytes: 900 * GB, isSsd: true, hint: null },
    ...(freeD === null ? [] : [{ root: "D:\\", label: "Games", freeBytes: freeD, totalBytes: 1800 * GB, isSsd: true, hint: null }]),
  ],
  folders: [],
});

const actionsOf = (job: JobInfo, st = storage(0.9 * GB)) => failureActions(job, spaceNeedOf(job, st), FIX_LABELS);

describe("failed jobs and history", () => {
  const jobs = [
    failed({ id: "a" }),
    failed({ id: "b", status: "completed", error: null, outcome: null }),
    failed({ id: "c", status: "cancelled", error: null, outcome: null }),
    failed({ id: "d", status: "running", error: null, outcome: null }),
  ];
  it("failures leave History: it lists only completed and canceled jobs", () => {
    expect(failedJobsOf(jobs).map((j) => j.id)).toEqual(["a"]);
    expect(pastJobsOf(jobs).map((j) => j.id)).toEqual(["b", "c"]);
  });

  it("a job that ran to the end but whose outcome says it failed counts as a failure, not as history", () => {
    // An import Legendary didn't register: status stays `completed`, the outcome is `failed`.
    const notRegistered = failed({ id: "e", kind: "import", status: "completed", error: null, outcome: { kind: "failed", message: "Legendary didn't register the install." } });
    expect(isFailed(notRegistered)).toBe(true);
    expect(failedJobsOf([notRegistered, ...jobs]).map((j) => j.id)).toEqual(["e", "a"]);
    expect(pastJobsOf([notRegistered, ...jobs]).map((j) => j.id)).toEqual(["b", "c"]);
    expect(failureMessage(notRegistered, null)).toBe("Legendary didn't register the install.");
  });
});

describe("a failure for lack of space", () => {
  it("says what is missing against the drive's free space now: 'Needs 4.8 GB, 921.6 MB free on D:'", () => {
    const job = space();
    const need = spaceNeedOf(job, storage(921.6 * MB))!;
    expect(need).toMatchObject({ drive: "D:", enough: false });
    expect(failureMessage(job, need)).toBe("Needs 4.8 GB, 921.6 MB free on D:");
  });

  it("follows the free space as it changes: freed space turns it into 'There's enough space now.'", () => {
    const job = space();
    expect(failureMessage(job, spaceNeedOf(job, storage(2 * GB)))).toBe("Needs 4.8 GB, 2 GB free on D:");
    const freed = spaceNeedOf(job, storage(40 * GB))!;
    expect(freed.enough).toBe(true);
    expect(failureMessage(job, freed)).toBe("There's enough space now.");
    // Exactly enough counts as enough.
    expect(spaceNeedOf(job, storage(4.8 * GB))!.enough).toBe(true);
  });

  it("while short: 'Free up space' leads and 'Try again' stays beside it, never taken away", () => {
    const a = actionsOf(space());
    expect(a.primary).toEqual({ id: "fix", fix: "freeSpace", label: "Free up space" });
    expect(a.secondary).toEqual({ id: "retry", label: "Try again" });
  });

  it("once there is room, 'Try again' alone and in front", () => {
    const a = actionsOf(space(), storage(40 * GB));
    expect(a.primary).toEqual({ id: "retry", label: "Try again" });
    expect(a.secondary).toBeUndefined();
  });

  it("reads the drive from the failure first, then from the job's own path", () => {
    expect(spaceNeedOf(space({ drive: "C:" }), storage(1 * GB))!.free).toBe(200 * GB);
    const byPath = failed({ targetPath: "D:\\Epic Games\\Keep", error: error({ code: "insufficientSpace", requiredBytes: 4.8 * GB, drive: null }) });
    expect(spaceNeedOf(byPath, storage(1 * GB))!.drive).toBe("D:");
  });

  it("when the snapshot can't say how much is free, it still names the need and keeps both buttons", () => {
    const job = space();
    const need = spaceNeedOf(job, storage(null))!;
    expect(need.free).toBeNull();
    expect(need.enough).toBe(false);
    expect(failureMessage(job, need)).toBe("Needs 4.8 GB free on D:");
    expect(actionsOf(job, storage(null)).secondary).toBeDefined();
  });

  it("a failure that doesn't carry its need is an ordinary failure", () => {
    expect(spaceNeedOf(space({ requiredBytes: null }), storage(0))).toBeNull();
  });

  it("looks up free space by drive letter in the drives, then in the library folders", () => {
    expect(freeOnDrive(storage(5 * GB), "d:")).toBe(5 * GB);
    expect(freeOnDrive({ drives: [], folders: [{ path: "E:\\Epic Games", isDefault: false, exists: true, drive: "E:\\", freeBytes: 7 * GB, gameCount: 1 }] }, "E:")).toBe(7 * GB);
    expect(freeOnDrive(storage(5 * GB), "")).toBeNull();
    expect(freeOnDrive(storage(5 * GB), "Z:")).toBeNull();
  });
});

describe("other failures", () => {
  it("show Core's one-line message, with the fix as the one button; a plain retry is 'Try again'", () => {
    const job = failed();
    expect(failureMessage(job, null)).toBe("Close Sunken Keep II and any program using its files, then try again.");
    expect(failureActions(job, null, FIX_LABELS)).toEqual({ primary: { id: "retry", label: "Try again" } });
    const repair = failed({ error: error({ code: "corrupted", fix: "repair" }) });
    expect(failureActions(repair, null, FIX_LABELS).primary).toEqual({ id: "fix", fix: "repair", label: "Repair" });
    expect(failureActions(failed({ error: error({ fix: null }) }), null, FIX_LABELS).primary.label).toBe("Try again");
  });

  it("title the failure by what failed and when: 'Update failed · 25 min ago'", () => {
    expect(failedMeta(failed(), Date.parse("2026-11-15T10:45:00Z"))).toBe("Update failed · 25 min ago");
    expect(failedMeta(failed({ kind: "move" }), Date.parse("2026-11-15T10:45:00Z"))).toBe("Move failed · 25 min ago");
  });
});

describe("the bulk update offer", () => {
  it("does not count games whose update sits in 'Couldn't finish' (they have their own Try again)", () => {
    expect(updatesToOffer(10, [failed({ appName: "A" }), failed({ id: "2", appName: "B" })])).toBe(8);
    expect(updatesToOffer(10, [failed({ appName: "A", kind: "install" })])).toBe(10); // an install is not a pending update
    expect(updatesToOffer(1, [failed({ appName: "A" }), failed({ id: "2", appName: "A" })])).toBe(0);
    expect(updatesToOffer(0, [])).toBe(0);
  });
});

describe("hasSpaceFailure", () => {
  it("is true only for failures that name a need (so the page keeps free space fresh)", () => {
    expect(hasSpaceFailure([failed(), space()])).toBe(true);
    expect(hasSpaceFailure([failed(), space({ requiredBytes: null })])).toBe(false);
    expect(hasSpaceFailure([])).toBe(false);
  });
});
