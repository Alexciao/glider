import { describe, expect, it } from "vitest";
import type { JobInfo } from "../ipc/contract.gen";
import { reorderActive } from "./actions";

const j = (id: string, status: JobInfo["status"]): JobInfo => ({ id, status }) as JobInfo;

describe("reorderActive", () => {
  const jobs = [j("run", "running"), j("a", "queued"), j("b", "paused"), j("c", "queued"), j("done", "completed")];
  it("moves a job within the unfinished ones and leaves history alone", () => {
    expect(reorderActive(jobs, "c", 1).map((x) => x.id)).toEqual(["run", "c", "a", "b", "done"]);
    expect(reorderActive(jobs, "a", 3).map((x) => x.id)).toEqual(["run", "b", "c", "a", "done"]);
  });
  it("ignores unknown ids and finished jobs", () => {
    expect(reorderActive(jobs, "nope", 0).map((x) => x.id)).toEqual(jobs.map((x) => x.id));
    expect(reorderActive(jobs, "done", 0).map((x) => x.id)).toEqual(jobs.map((x) => x.id));
  });
});
