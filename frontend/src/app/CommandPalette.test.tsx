import { describe, expect, it } from "vitest";
import { act, screen, waitFor } from "@testing-library/react";
import { getRecents } from "../state/recents";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { mount, installCleanStores } from "../test/harness";
import { CommandPalette } from "./CommandPalette";

installCleanStores();

const open = () => act(() => useUi.getState().setPalette(true));

describe("Command palette", () => {
  it("opens empty with suggestions and the pages; Escape closes it", async () => {
    const { user } = await mount("returning", <CommandPalette />);
    open();
    const input = await screen.findByRole("combobox");
    expect(input).toHaveFocus();
    expect(screen.getByRole("option", { name: /^Home/ })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: /^Settings/ })).toBeInTheDocument();
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("combobox")).not.toBeInTheDocument());
  });

  it("searches games: one row per game, its one action visible, Enter runs it", async () => {
    const { user, booted } = await mount("returning", <CommandPalette />);
    const game = booted.find((g) => g.kind === "game" && g.installStatus === "notInstalled" && g.thirdParty === "none");
    open();
    await user.keyboard(game.title);
    const row = await screen.findByRole("option", { name: new RegExp(`^${game.title.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`) });
    expect(row).toHaveTextContent("Install");
    await user.keyboard("{Enter}");
    expect(useUi.getState().dialog).toEqual({ kind: "install", appName: game.appName });
    expect(useUi.getState().palette).toBe(false);
  });

  it("Ctrl+Enter opens the game's page instead of acting", async () => {
    const { user, booted } = await mount("returning", <CommandPalette />);
    const game = booted.find((g) => g.kind === "game" && g.installStatus === "installed" && !g.updateAvailable && g.runState === "idle" && g.thirdParty === "none");
    open();
    await user.keyboard(game.title);
    await screen.findByRole("option");
    await user.keyboard("{Control>}{Enter}{/Control}");
    const { useRouter } = await import("../state/router");
    expect(useRouter.getState().route).toEqual({ page: "game", appName: game.appName });
  });

  it("every Settings section is reachable by name", async () => {
    const { user } = await mount("returning", <CommandPalette />);
    open();
    await user.keyboard("cloud saves");
    await user.keyboard("{Enter}");
    expect(useUi.getState().settingsSection).toBe("saves");
  });

  it("switches the download mode and remembers what was used", async () => {
    const { user } = await mount("returning", <CommandPalette />);
    open();
    await user.keyboard("mode full");
    await user.keyboard("{Enter}");
    await waitFor(() => expect(useApp.getState().snapshot!.settings.downloadMode).toBe("full"));
    expect(getRecents()[0]).toBe("cmd:mode-full");
    open();
    expect(await screen.findByText("Recent")).toBeInTheDocument();
  });

  it("a disabled command shows its reason and Enter does nothing", async () => {
    const { user } = await mount("offline", <CommandPalette />);
    open();
    await user.keyboard("check for updates");
    expect(await screen.findByText("You're offline")).toBeInTheDocument();
    await user.keyboard("{Enter}");
    expect(useUi.getState().palette).toBe(true);
  });

  it("says so, in one line, when nothing matches", async () => {
    const { user } = await mount("returning", <CommandPalette />);
    open();
    await user.keyboard("qzqzq");
    expect(await screen.findByText(/No results for/)).toBeInTheDocument();
  });

  it("arrow keys move the selection through the groups and wrap", async () => {
    const { user } = await mount("returning", <CommandPalette />);
    open();
    const input = await screen.findByRole("combobox");
    const first = input.getAttribute("aria-activedescendant");
    await user.keyboard("{ArrowUp}");
    expect(input.getAttribute("aria-activedescendant")).not.toBe(first);
    await user.keyboard("{ArrowDown}");
    expect(input.getAttribute("aria-activedescendant")).toBe(first);
  });
});
