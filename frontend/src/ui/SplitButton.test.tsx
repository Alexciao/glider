import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import axe from "axe-core";
import { DropdownMenuItem, MotionProvider, SplitButton, TooltipProvider } from "./index";

const wrap = (ui: React.ReactElement) => (
  <MotionProvider>
    <TooltipProvider>{ui}</TooltipProvider>
  </MotionProvider>
);

const menu = (
  <>
    <DropdownMenuItem onSelect={() => {}}>Verify</DropdownMenuItem>
    <DropdownMenuItem onSelect={() => {}}>Uninstall…</DropdownMenuItem>
  </>
);

describe("SplitButton", () => {
  it("is one group with a primary segment and a menu segment", () => {
    render(wrap(<SplitButton label="Play" onPress={() => {}} menu={menu} />));
    const group = screen.getByRole("group", { name: "Play" });
    const buttons = group.querySelectorAll("button");
    expect(buttons).toHaveLength(2);
    expect(buttons[0]).toHaveTextContent("Play");
    expect(buttons[1]).toHaveAccessibleName("More actions");
    // One shape: the group owns the radius, the segments own none (no "two boxes with a gap").
    expect(group.style.borderRadius).toBe("12px");
    for (const b of buttons) expect(b.style.borderRadius).toBe("");
    expect(group.querySelector(".g-split-divider")).not.toBeNull();
  });

  it("segments share the group's height class for every size", () => {
    for (const [size, h, r] of [["sm", "h-8", "8px"], ["md", "h-10", "12px"], ["lg", "h-12", "16px"]] as const) {
      const { unmount } = render(wrap(<SplitButton label="Install" size={size} onPress={() => {}} menu={menu} />));
      const group = screen.getByRole("group");
      expect(group.className).toContain(h);
      expect(group.style.borderRadius).toBe(r);
      unmount();
    }
  });

  it("without a menu it is a single button, all corners round", () => {
    render(wrap(<SplitButton label="Install" onPress={() => {}} />));
    expect(screen.getAllByRole("button")).toHaveLength(1);
    expect(document.querySelector(".g-split-divider")).toBeNull();
  });

  it("presses the primary action and opens the menu from the second segment", async () => {
    const user = userEvent.setup();
    const onPress = vi.fn();
    render(wrap(<SplitButton label="Play" onPress={onPress} menu={menu} />));
    await user.click(screen.getByRole("button", { name: "Play" }));
    expect(onPress).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole("button", { name: "More actions" }));
    expect(await screen.findByRole("menuitem", { name: "Verify" })).toBeInTheDocument();
    expect(screen.getByRole("menuitem", { name: "Uninstall…" })).toBeInTheDocument();
  });

  it("each segment is its own tab stop, in order", async () => {
    const user = userEvent.setup();
    render(wrap(<SplitButton label="Play" onPress={() => {}} menu={menu} />));
    await user.tab();
    expect(screen.getByRole("button", { name: "Play" })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole("button", { name: "More actions" })).toHaveFocus();
  });

  it("busy ignores presses but stays focusable and announces itself", async () => {
    const user = userEvent.setup();
    const onPress = vi.fn();
    render(wrap(<SplitButton label="Launching…" busy onPress={onPress} menu={menu} />));
    const primary = screen.getByRole("button", { name: "Launching…" });
    expect(primary).toHaveAttribute("aria-busy", "true");
    expect(primary).toBeEnabled();
    await user.click(primary);
    expect(onPress).not.toHaveBeenCalled();
    expect(primary).toHaveFocus(); // the click focused it and it stays focused: nothing was disabled underneath the user
  });

  it("disabled primary does not fire; the menu still works", async () => {
    const user = userEvent.setup();
    const onPress = vi.fn();
    render(wrap(<SplitButton label="Install" disabled onPress={onPress} menu={menu} />));
    expect(screen.getByRole("button", { name: "Install" })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: "Install" }));
    expect(onPress).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "More actions" }));
    expect(await screen.findByRole("menuitem", { name: "Verify" })).toBeInTheDocument();
  });

  it("the face only swaps when its key changes", async () => {
    const { rerender } = render(wrap(<SplitButton label="Preparing…" faceKey="launching" busy onPress={() => {}} />));
    rerender(wrap(<SplitButton label="Syncing saves…" faceKey="launching" busy onPress={() => {}} />));
    await waitFor(() => expect(screen.getByRole("button")).toHaveTextContent("Syncing saves…"));
    expect(screen.getByRole("button").textContent).not.toContain("Preparing");
  });

  it.each(["primary", "quiet"] as const)("has no accessibility violations (%s)", async (tone) => {
    const { container } = render(wrap(<SplitButton label="Play" tone={tone} onPress={() => {}} menu={menu} />));
    const result = await axe.run(container, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.map((v) => v.id)).toEqual([]);
  });
});
