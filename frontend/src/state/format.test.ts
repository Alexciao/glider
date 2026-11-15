import { describe, expect, it } from "vitest";
import {
  buildOf,
  driveOf,
  formatBytes,
  formatBytesPrecise,
  formatCount,
  formatSpeedCompact,
  formatClock,
  formatDuration,
  formatEta,
  formatPercent,
  formatPlaytime,
  formatRelativeTime,
  formatSpeed,
  joinPath,
  prettyVersion,
  splitPath,
} from "./format";

const GiB = 1024 ** 3;

describe("formatBytes (matches the backend's Format.Bytes)", () => {
  it("uses 1024-based KB/MB/GB labels with one trimmed decimal", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1024)).toBe("1 KB");
    expect(formatBytes(1.5 * 1024 * 1024)).toBe("1.5 MB");
    expect(formatBytes(28 * GiB)).toBe("28 GB");
    expect(formatBytes(94.8 * GiB)).toBe("94.8 GB");
    expect(formatBytes(1.2 * 1024 ** 4)).toBe("1.2 TB");
  });
  it("is safe on junk", () => {
    expect(formatBytes(Number.NaN)).toBe("—");
    expect(formatBytes(Number.POSITIVE_INFINITY)).toBe("—");
  });
});

describe("formatSpeed", () => {
  it("keeps one fixed decimal so live numbers don't change width", () => {
    expect(formatSpeed(48 * 1024 * 1024)).toBe("48.0 MB/s");
    expect(formatSpeed(48.24 * 1024 * 1024)).toBe("48.2 MB/s");
    expect(formatSpeed(900)).toBe("900 B/s");
    expect(formatSpeed(-1)).toBe("—");
  });
});

describe("formatEta / formatDuration / formatClock", () => {
  it("formats ETA for humans", () => {
    expect(formatEta(null)).toBe("—");
    expect(formatEta(12)).toBe("Under a minute");
    expect(formatEta(9 * 60)).toBe("9 min");
    expect(formatEta(72 * 60)).toBe("1 h 12 min");
    expect(formatEta(2 * 3600)).toBe("2 h");
    expect(formatEta(26 * 3600)).toBe("1 d 2 h");
  });
  it("formats durations compactly", () => {
    expect(formatDuration(38)).toBe("38 s");
    expect(formatDuration(250)).toBe("4 min 10 s");
    expect(formatDuration(3600)).toBe("1 h");
    expect(formatDuration(4320)).toBe("1 h 12 min");
  });
  it("formats clocks", () => {
    expect(formatClock(0)).toBe("0:00");
    expect(formatClock(252)).toBe("4:12");
    expect(formatClock(3729)).toBe("1:02:09");
  });
});

describe("formatPercent", () => {
  it("floors so 100% only appears when truly done", () => {
    expect(formatPercent(41.99)).toBe("41%");
    expect(formatPercent(99.97)).toBe("99%");
    expect(formatPercent(100)).toBe("100%");
    expect(formatPercent(-4)).toBe("0%");
    expect(formatPercent(140)).toBe("100%");
  });
});

describe("formatRelativeTime (local calendar days, not 24-hour blocks)", () => {
  // Built from local parts so the assertions hold in any time zone.
  const now = new Date(2026, 10, 14, 12, 0).getTime();
  const at = (day: number, h: number, min = 0) => new Date(2026, 10, day, h, min).toISOString();
  it("reads naturally", () => {
    expect(formatRelativeTime(new Date(now - 10_000).toISOString(), now)).toBe("Just now");
    expect(formatRelativeTime(new Date(now - 4 * 60_000).toISOString(), now)).toBe("4 min ago");
    expect(formatRelativeTime(at(14, 9), now)).toBe("3 h ago");
    expect(formatRelativeTime(null, now)).toBe("—");
    expect(formatRelativeTime("nope", now)).toBe("—");
  });
  it("counts days by date: the same moment never gets two names", () => {
    // 26 hours earlier on the clock, but two calendar days back.
    const lateNov12 = new Date(2026, 10, 12, 23, 0).getTime();
    expect(formatRelativeTime(new Date(lateNov12).toISOString(), new Date(2026, 10, 14, 1, 0).getTime())).toBe("2 days ago");
    expect(formatRelativeTime(at(13, 22, 20), now)).toBe("Yesterday");
    expect(formatRelativeTime(at(13, 1), now)).toBe("Yesterday");
    expect(formatRelativeTime(at(12, 1, 57), now)).toBe("2 days ago");
    expect(formatRelativeTime(at(9, 12), now)).toBe("5 days ago");
  });
  it("crossing midnight in under an hour stays in minutes; in a few hours it is yesterday", () => {
    expect(formatRelativeTime(new Date(2026, 10, 13, 23, 50).toISOString(), new Date(2026, 10, 14, 0, 10).getTime())).toBe("20 min ago");
    expect(formatRelativeTime(new Date(2026, 10, 13, 22, 0).toISOString(), new Date(2026, 10, 14, 0, 10).getTime())).toBe("Yesterday");
  });
  it("switches to the date from a week on, with the year only when it isn't this one", () => {
    expect(formatRelativeTime(at(7, 12), now)).toMatch(/Nov|nov/i);
    expect(formatRelativeTime(new Date(2025, 9, 28).toISOString(), now)).toMatch(/2025/);
  });
});

describe("versions and paths", () => {
  it("extracts the readable version from Legendary's app version", () => {
    expect(prettyVersion("++Fortnite+Release-40.20-CL-47120934-Windows")).toBe("40.20");
    expect(buildOf("++Fortnite+Release-40.20-CL-47120934-Windows")).toBe("47120934");
    expect(prettyVersion("++Fortnite+Release-Cert-Next-40.20-CL-47120934-Windows-Shipping-Win64")).toBe("40.20");
    expect(prettyVersion("weird")).toBe("weird");
    expect(prettyVersion("++Fortnite+Release-Experimental-Preview-Build-Windows")).toBe("++Fortnite+Release-Experime…");
    expect(prettyVersion(null)).toBe("—");
  });
  it("splits, joins and identifies drives", () => {
    expect(splitPath("D:\\Games\\Epic Games\\Fortnite")).toEqual({ head: "D:\\Games\\", tail: "Epic Games\\Fortnite" });
    expect(splitPath("D:\\Epic Games\\Fortnite")).toEqual({ head: "", tail: "D:\\Epic Games\\Fortnite" });
    expect(splitPath("D:\\Fortnite")).toEqual({ head: "", tail: "D:\\Fortnite" });
    expect(joinPath("D:\\Games\\", "Fortnite")).toBe("D:\\Games\\Fortnite");
    expect(driveOf("d:\\x")).toBe("D:");
    expect(driveOf(null)).toBe("");
  });
});

describe("formatPlaytime", () => {
  it("formats hours and minutes", () => {
    expect(formatPlaytime(42 * 3600 + 17 * 60 + 40)).toBe("42 h 17 min");
    expect(formatPlaytime(3600)).toBe("1 h 0 min");
    expect(formatPlaytime(17 * 60)).toBe("17 min");
    expect(formatPlaytime(30)).toBe("< 1 min");
    expect(formatPlaytime(-5)).toBe("< 1 min");
  });
});

describe("download formatters", () => {
  it("formatBytesPrecise keeps two decimals from GB up", () => {
    expect(formatBytesPrecise(2.41 * 1024 ** 3)).toBe("2.41 GB");
    expect(formatBytesPrecise(5.12 * 1024 ** 3)).toBe("5.12 GB");
    expect(formatBytesPrecise(340 * 1024 ** 2)).toBe("340 MB");
    expect(formatBytesPrecise(512)).toBe("512 B");
  });
  it("formatSpeedCompact is whole from 10 up", () => {
    expect(formatSpeedCompact(60 * 1024 ** 2)).toBe("60 MB/s");
    expect(formatSpeedCompact(1.5 * 1024 ** 2)).toBe("1.5 MB/s");
    expect(formatSpeedCompact(1.8 * 1024 ** 3)).toBe("1.8 GB/s");
  });
  it("formatCount groups digits", () => {
    expect(formatCount(31204)).toBe((31204).toLocaleString());
  });
});
