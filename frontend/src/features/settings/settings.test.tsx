import { describe, expect, it } from "vitest";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { useApp } from "../../state/store";
import { useUi } from "../../state/ui";
import { mount, installCleanStores } from "../../test/harness";
import { isHostName } from "./DownloadsSection";
import { ReadinessResult } from "./EglSection";
import { Settings } from "./Settings";
import { resolveSection } from "./sections";

installCleanStores();

const goto = (section: Parameters<ReturnType<typeof useUi.getState>["go"]>[1]) => act(() => useUi.getState().go("settings", section));
const settings = () => useApp.getState().snapshot!.settings;

describe("Settings structure", () => {
  it("opens on Downloads: rows are a label and at most one line, with no section intro", async () => {
    await mount("returning", <Settings />);
    expect(screen.getByRole("heading", { level: 2, name: "Downloads" })).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "Download mode" })).toBeInTheDocument();
    expect(screen.getByRole("radiogroup", { name: "While a game is running" })).toBeInTheDocument();
    expect(screen.queryByText(/tunes these for your PC/)).not.toBeInTheDocument();
    expect(screen.queryByText(/How fast and how gently/)).not.toBeInTheDocument();
  });

  it("rarely used controls sit under a collapsed Advanced disclosure", async () => {
    const { user } = await mount("returning", <Settings />);
    expect(screen.queryByText("Download workers")).not.toBeInTheDocument();
    expect(screen.queryByText("Download server")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Advanced", expanded: false }));
    expect(await screen.findByText("Download workers")).toBeInTheDocument();
    expect(screen.getByText("Shared memory")).toBeInTheDocument();
    expect(screen.getByText("Download server")).toBeInTheDocument();
  });

  it("has a nav for all twelve sections, and the old 'general' page maps to Startup and tray", async () => {
    const { user } = await mount("returning", <Settings />);
    const nav = screen.getByRole("navigation", { name: "Settings sections" });
    expect(within(nav).getAllByRole("button")).toHaveLength(12);
    await user.click(within(nav).getByRole("button", { name: "Updates" }));
    expect(await screen.findByRole("heading", { level: 2, name: "Updates" })).toBeInTheDocument();
    expect(resolveSection("general")).toBe("startup");
  });

  it("each section renders", async () => {
    await mount("returning", <Settings />);
    for (const [section, heading] of [
      ["library", "Library folders"],
      ["saves", "Cloud saves"],
      ["egl", "Epic Games Launcher"],
      ["overlay", "Epic overlay"],
      ["links", "Links"],
      ["appearance", "Appearance"],
      ["startup", "Startup and tray"],
      ["account", "Account"],
      ["advanced", "Advanced"],
      ["about", "About and licenses"],
    ] as const) {
      goto(section);
      expect(await screen.findByRole("heading", { level: 2, name: heading })).toBeInTheDocument();
    }
  });
});

describe("Settings behaviour", () => {
  it("changing the download mode and the while-playing policy writes the setting", async () => {
    const { user } = await mount("returning", <Settings />);
    await user.click(screen.getByRole("radio", { name: "Full" }));
    await waitFor(() => expect(settings().downloadMode).toBe("full"));
    await user.click(screen.getByRole("radio", { name: "Continue" }));
    await waitFor(() => expect(settings().whilePlaying).toBe("continue"));
  });

  it("the CDN is Automatic or a validated manual host", async () => {
    const { user } = await mount("returning", <Settings />);
    await user.click(screen.getByRole("button", { name: "Advanced", expanded: false }));
    await user.click(await screen.findByRole("radio", { name: "Manual" }));
    const host = screen.getByRole("textbox", { name: "Download server host name" });
    await user.type(host, "not a host");
    await user.tab();
    expect(await screen.findByText(/Enter a host name/)).toBeInTheDocument();
    expect(settings().preferredCdn).toBeNull();
    await user.clear(host);
    await user.type(host, "cdn.example.com");
    await user.tab();
    await waitFor(() => expect(settings().preferredCdn).toBe("cdn.example.com"));
    await user.click(screen.getByRole("radio", { name: "Automatic" }));
    await waitFor(() => expect(settings().preferredCdn).toBeNull());
  });

  it("isHostName accepts real hosts only", () => {
    expect(isHostName("download.epicgames.com")).toBe(true);
    expect(isHostName("cdn-1.example.co.uk")).toBe(true);
    for (const bad of ["localhost", "a b.com", "https://x.com", "-x.com", "x..com", ""]) expect(isHostName(bad)).toBe(false);
  });

  it("the library folder list adds, makes default and hides Remove for folders with games", async () => {
    const { user } = await mount("returning", <Settings />);
    goto("library");
    const group = await screen.findByRole("radiogroup", { name: "Default install folder" });
    const radios = within(group).getAllByRole("radio");
    expect(radios.length).toBeGreaterThanOrEqual(3);
    const next = radios.find((r) => r.getAttribute("aria-checked") === "false")!;
    await user.click(next);
    await waitFor(() => expect(next).toBeChecked());
    // Folders with games installed have no Remove button at all.
    expect(within(group).queryAllByRole("button", { name: "Remove folder" })).toHaveLength(0);
  });

  it("Updates: one row shows what can be updated, with Check now and Update all", async () => {
    const { user } = await mount("updates", <Settings />);
    goto("updates");
    expect(await screen.findByText(/\d+ games? can be updated/)).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: "Never" }));
    await waitFor(() => expect(settings().autoUpdate).toBe("never"));
    expect(screen.getByRole("switch", { name: "Only when I'm not playing" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Update all" })).toBeEnabled();
  });

  it("Links: off by default, glider:// always on", async () => {
    await mount("returning", <Settings />);
    goto("links");
    expect(await screen.findByRole("switch", { name: /Open Epic Games Launcher links in Glider/ })).not.toBeChecked();
    expect(screen.getByText("Always on")).toBeInTheDocument();
  });

  it("Epic overlay: install state, enable switch, remove", async () => {
    const { user } = await mount("returning", <Settings />);
    goto("overlay");
    expect(await screen.findByRole("switch", { name: "Use the overlay in games" })).toBeChecked();
    await user.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(useApp.getState().snapshot!.overlay.installed).toBe(false));
    expect(await screen.findByRole("button", { name: "Install" })).toBeInTheDocument();
  });

  it("Advanced: Reset hides under More and needs a second click", async () => {
    const { user } = await mount("returning", <Settings />);
    goto("advanced");
    expect(screen.queryByRole("button", { name: "Reset" })).not.toBeInTheDocument();
    await user.click(await screen.findByRole("button", { name: "More", expanded: false }));
    await user.click(await screen.findByRole("button", { name: "Reset" }));
    expect(screen.getByRole("button", { name: "Click again to reset" })).toBeInTheDocument();
  });

  it("Run setup again restarts onboarding", async () => {
    const { user } = await mount("returning", <Settings />);
    goto("advanced");
    await user.click(await screen.findByRole("button", { name: "Run again" }));
    await waitFor(() => expect(useApp.getState().snapshot!.onboarding.completed).toBe(false));
    expect(useApp.getState().snapshot!.onboarding.step).toBe("welcome");
  });
});

describe("Settings search", () => {
  it("shows matching rows from every section under their titles, and hides the rest", async () => {
    const { user } = await mount("returning", <Settings />);
    await user.type(screen.getByRole("searchbox", { name: "Search settings" }), "folder");
    expect(await screen.findByRole("heading", { level: 2, name: "Library folders" })).toBeInTheDocument();
    expect(screen.queryByText("Speed limit")).not.toBeInTheDocument();
    // (Empty sections are hidden by CSS; jsdom has none, so only rows are asserted here.)
  });

  it("finds a setting by what it does, and says when nothing matches", async () => {
    const { user } = await mount("returning", <Settings />);
    const box = screen.getByRole("searchbox", { name: "Search settings" });
    await user.type(box, "dark");
    expect(await screen.findByText("Theme")).toBeInTheDocument();
    await user.clear(box);
    await user.type(box, "zzzqq");
    expect(await screen.findByText(/No settings match/)).toBeInTheDocument();
    await user.keyboard("{Escape}");
    expect(box).toHaveValue("");
    expect(await screen.findByRole("heading", { level: 2, name: "Downloads" })).toBeInTheDocument();
  });

  it("opens the Advanced rows when they match", async () => {
    const { user } = await mount("returning", <Settings />);
    await user.type(screen.getByRole("searchbox", { name: "Search settings" }), "workers");
    expect(await screen.findByText("Download workers")).toBeInTheDocument();
  });
});

describe("Ready to uninstall Epic Games Launcher?", () => {
  it("ready: one all-clear line", () => {
    render(<ReadinessResult result={{ ready: true, items: [{ appName: null, title: "Everything checks out", severity: "ok", message: "x" }] }} />);
    expect(screen.getByText("Nothing depends on it. You can uninstall it.")).toBeInTheDocument();
    expect(screen.queryByRole("list")).not.toBeInTheDocument();
  });

  it("ready with warnings is still one line: warnings are not shown", () => {
    render(<ReadinessResult result={{ ready: true, items: [{ appName: "A", title: "Game A", severity: "warning", message: "Also listed in the launcher." }] }} />);
    expect(screen.queryByText("Game A")).not.toBeInTheDocument();
  });

  it("blocked: a count and one compact line per blocking game", () => {
    render(
      <ReadinessResult
        result={{
          ready: false,
          items: [
            { appName: "A", title: "Game A", severity: "blocker", message: "Link it with Ubisoft Connect first." },
            { appName: "B", title: "Game B", severity: "blocker", message: "Epic is still updating it." },
            { appName: "C", title: "Game C", severity: "warning", message: "Heads up." },
          ],
        }}
      />,
    );
    expect(screen.getByText("2 games still need it")).toBeInTheDocument();
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
  });

  it("the Check button runs the check and shows the result in place", async () => {
    const { user } = await mount("returning", <Settings />);
    goto("egl");
    await user.click(await screen.findByRole("button", { name: "Check" }));
    expect(await screen.findByText(/still needs? it/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Check again" })).toBeInTheDocument();
  });
});
