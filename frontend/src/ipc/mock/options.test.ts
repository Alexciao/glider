import { describe, expect, it } from "vitest";
import { parseMockOptions } from "./options";

describe("parseMockOptions", () => {
  it("reads every documented parameter", () => {
    expect(
      parseMockOptions("?scenario=downloading&seek=0.6&timescale=30&freeze=1&fail=network&art=none&backdrop=mica&latency=0&seed=9&games=600&eac=0"),
    ).toEqual({
      scenario: "downloading",
      seek: 0.6,
      timeScale: 30,
      freeze: true,
      fail: "network",
      art: "none",
      backdrop: "mica",
      latency: 0,
      seed: 9,
      games: 600,
      eacInstalled: false,
    });
  });

  it("returns an empty object for an empty query and ignores unknown or invalid values", () => {
    expect(parseMockOptions("")).toEqual({});
    expect(parseMockOptions("?scenario=nope&fail=bogus&backdrop=glass&seek=abc&timescale=-3&latency=-1&art=big&games=-4")).toEqual({});
  });

  it("clamps seek into 0..1 and accepts a query without a leading ?", () => {
    expect(parseMockOptions("seek=4").seek).toBe(1);
    expect(parseMockOptions("seek=-2").seek).toBe(0);
    expect(parseMockOptions("scenario=perf600").scenario).toBe("perf600");
    expect(parseMockOptions("scenario=installed").scenario).toBeUndefined();
  });

  it("treats freeze as a flag", () => {
    expect(parseMockOptions("?freeze").freeze).toBe(true);
    expect(parseMockOptions("?freeze=0").freeze).toBe(false);
    expect(parseMockOptions("?freeze=true").freeze).toBe(true);
  });
});
