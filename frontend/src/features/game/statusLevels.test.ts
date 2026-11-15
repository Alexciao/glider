import { describe, expect, it } from "vitest";
import { STATUS_LEVEL, TONE_TEXT, statusTone } from "./statusLevels";

describe("the one status colour system", () => {
  it("danger is failure or data at risk, warning needs you, accent informs, ordinary states are uncoloured", () => {
    expect(statusTone("failed")).toBe("danger");
    expect(statusTone("syncFailed")).toBe("danger");
    for (const k of ["needsRepair", "savesDiffer", "blocker", "lowSpace", "needsSignIn", "needsInternet"] as const) expect(statusTone(k)).toBe("warning");
    expect(statusTone("updateAvailable")).toBe("accent");
    for (const k of ["installed", "ready", "inSync", "notInstalled", "queued"] as const) expect(statusTone(k)).toBe("neutral");
  });

  it("needs repair is one colour on every surface, and never red", () => {
    expect(STATUS_LEVEL.needsRepair).toBe("warning");
    expect(TONE_TEXT[statusTone("needsRepair")]).toBe("text-warning-text");
  });

  it("green is only the running dot: its text stays as calm as normal text", () => {
    expect(statusTone("running")).toBe("success");
    expect(TONE_TEXT.success).toBe(TONE_TEXT.neutral);
  });
});
