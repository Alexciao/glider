import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ArtThumb, Callout, Checkbox, ChoiceGroup, Disclosure, UsageBar, isLowOnSpace, type CheckedState } from "./index";

describe("Checkbox", () => {
  function Harness({ initial = false }: { initial?: CheckedState }) {
    const [v, setV] = useState<CheckedState>(initial);
    return (
      <>
        <Checkbox checked={v} onCheckedChange={setV} aria-label="Select all" />
        <output>{String(v)}</output>
      </>
    );
  }

  it("toggles with the mouse and the space key", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const box = screen.getByRole("checkbox", { name: "Select all" });
    await user.click(box);
    expect(box).toHaveAttribute("aria-checked", "true");
    box.focus();
    await user.keyboard(" ");
    expect(box).toHaveAttribute("aria-checked", "false");
  });

  it("indeterminate is exposed as mixed, and the next click selects everything", async () => {
    const user = userEvent.setup();
    render(<Harness initial="indeterminate" />);
    const box = screen.getByRole("checkbox");
    expect(box).toHaveAttribute("aria-checked", "mixed");
    await user.click(box);
    expect(box).toHaveAttribute("aria-checked", "true");
  });

  it("is inert when disabled", async () => {
    const onChange = vi.fn();
    render(<Checkbox checked={false} onCheckedChange={onChange} disabled aria-label="x" />);
    fireEvent.click(screen.getByRole("checkbox"));
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe("ChoiceGroup", () => {
  const options = [
    { value: "a", title: "Alpha", description: "First one" },
    { value: "b", title: "Beta", description: "Second one", badge: <span>Recommended</span> },
  ] as const;

  it("is a radio group: arrows move and select, the badge and description are part of the name", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<ChoiceGroup aria-label="Mode" value="a" onValueChange={onChange} options={options} />);
    expect(screen.getByRole("radiogroup", { name: "Mode" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: /Beta.*Recommended.*Second one/ })).toBeInTheDocument();
    screen.getByRole("radio", { name: /Alpha/ }).focus();
    await user.keyboard("{ArrowDown>}");
    await user.keyboard("{/ArrowDown}");
    await waitFor(() => expect(onChange).toHaveBeenCalledWith("b"));
  });
});

describe("UsageBar", () => {
  it("is a meter with a percentage, and goes amber when the drive is nearly full", () => {
    const GiB = 1024 ** 3;
    const { rerender } = render(<UsageBar label="D: drive usage" usedBytes={250 * GiB} totalBytes={1000 * GiB} />);
    const meter = screen.getByRole("meter", { name: "D: drive usage" });
    expect(meter).toHaveAttribute("aria-valuenow", "25");
    expect(meter.firstElementChild).not.toHaveClass("bg-warning");
    rerender(<UsageBar label="D: drive usage" usedBytes={990 * GiB} totalBytes={1000 * GiB} />);
    expect(screen.getByRole("meter").firstElementChild).toHaveClass("bg-warning");
  });

  it("low means under 5 % or 20 GB, whichever is more", () => {
    const GiB = 1024 ** 3;
    expect(isLowOnSpace(15 * GiB, 500 * GiB)).toBe(true);
    expect(isLowOnSpace(60 * GiB, 500 * GiB)).toBe(false);
    expect(isLowOnSpace(60 * GiB, 4000 * GiB)).toBe(true);
    expect(isLowOnSpace(0, 0)).toBe(true);
  });
});

describe("Disclosure", () => {
  it("is collapsed by default, expands in place and reports its state", async () => {
    const user = userEvent.setup();
    render(
      <Disclosure title="Advanced">
        <p>Hidden controls</p>
      </Disclosure>,
    );
    const trigger = screen.getByRole("button", { name: "Advanced" });
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("Hidden controls")).not.toBeInTheDocument();
    await user.click(trigger);
    expect(await screen.findByText("Hidden controls")).toBeInTheDocument();
    expect(trigger).toHaveAttribute("aria-expanded", "true");
  });
});

describe("ArtThumb", () => {
  it("is decorative, and falls back to a monogram without art or when the image fails", () => {
    const { container, rerender } = render(<ArtThumb src={null} title="Ivory Undertow" />);
    expect(container.firstElementChild).toHaveAttribute("aria-hidden", "true");
    expect(container).toHaveTextContent("I");
    rerender(<ArtThumb src="x.png" title="Ivory Undertow" />);
    const img = container.querySelector("img")!;
    expect(img).toHaveAttribute("loading", "lazy");
    fireEvent.error(img);
    expect(container.querySelector("img")).toBeNull();
    expect(container).toHaveTextContent("I");
  });
});

describe("Callout", () => {
  it("announces warnings and offline notes, and stays quiet for info", () => {
    const { rerender } = render(<Callout tone="offline" title="You're offline" />);
    expect(screen.getByRole("status")).toHaveTextContent("You're offline");
    rerender(<Callout tone="info" title="FYI" />);
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });
});
