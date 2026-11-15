import { describe, expect, it } from "vitest";
import { isBusy } from "./derive";
import { worldFor } from "../test/fixtures";

describe("isBusy", () => {
  it("reports busy per game: while its job runs or it is running", async () => {
    const d = await worldFor("downloading");
    const j = d.snap.queue.jobs.find((x) => x.status === "running")!;
    expect(isBusy(d.snap, j.appName)).toBe(true);
    expect(isBusy(d.snap, "someone-else")).toBe(false);
    expect(isBusy((await worldFor("playing")).snap, "Fortnite")).toBe(true);
    expect(isBusy((await worldFor("returning")).snap, "Fortnite")).toBe(false);
  });
});
