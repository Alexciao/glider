import { describe, expect, it } from "vitest";
import { addFolderPatch, driveFor, removeBlockedReason, removeFolderPatch, samePath } from "./folders";

describe("library folder patches", () => {
  it("the first folder becomes the default; later ones don't change it", () => {
    expect(addFolderPatch({ libraryFolders: [], defaultInstallBase: null }, "D:\\Games")).toEqual({ libraryFolders: ["D:\\Games"], defaultInstallBase: "D:\\Games" });
    expect(addFolderPatch({ libraryFolders: ["D:\\Games"], defaultInstallBase: "D:\\Games" }, "E:\\Epic")).toEqual({ libraryFolders: ["D:\\Games", "E:\\Epic"], defaultInstallBase: "D:\\Games" });
  });

  it("refuses a duplicate, ignoring case and a trailing slash", () => {
    expect(addFolderPatch({ libraryFolders: ["D:\\Games"], defaultInstallBase: "D:\\Games" }, "d:\\games\\")).toBeNull();
    expect(samePath("D:\\Games\\", "d:\\games")).toBe(true);
  });

  it("removing the default hands it to the first remaining folder, or to nothing", () => {
    expect(removeFolderPatch({ libraryFolders: ["D:\\A", "E:\\B"], defaultInstallBase: "D:\\A" }, "D:\\A")).toEqual({ libraryFolders: ["E:\\B"], defaultInstallBase: "E:\\B" });
    expect(removeFolderPatch({ libraryFolders: ["D:\\A", "E:\\B"], defaultInstallBase: "D:\\A" }, "E:\\B")).toEqual({ libraryFolders: ["D:\\A"], defaultInstallBase: "D:\\A" });
    expect(removeFolderPatch({ libraryFolders: ["D:\\A"], defaultInstallBase: "D:\\A" }, "D:\\A")).toEqual({ libraryFolders: [], defaultInstallBase: null });
  });

  it("a folder with games can't be removed, and says why", () => {
    expect(removeBlockedReason({ gameCount: 0 })).toBeNull();
    expect(removeBlockedReason({ gameCount: 3 })).toMatch(/3 games are installed here/);
  });

  it("finds the drive by letter", () => {
    const drives = [
      { root: "C:\\", label: null, freeBytes: 1, totalBytes: 2, isSsd: true, hint: null },
      { root: "D:\\", label: null, freeBytes: 3, totalBytes: 4, isSsd: false, hint: null },
    ];
    expect(driveFor({ drive: "D:", path: "D:\\Games" }, drives)?.freeBytes).toBe(3);
    expect(driveFor({ drive: "", path: "x:\\Games" }, drives)).toBeUndefined();
  });
});
