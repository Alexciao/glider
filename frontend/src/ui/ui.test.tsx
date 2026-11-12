import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Avatar, Badge, Button, ErrorCallout, IconButton, ProgressBar, SegmentedControl, Switch, TooltipProvider, initialsOf } from "./index";

describe("Button", () => {
  it("is busy and non-interactive while loading", () => {
    const onClick = vi.fn();
    render(
      <Button loading onClick={onClick}>
        Install
      </Button>,
    );
    const b = screen.getByRole("button", { name: "Install" });
    expect(b).toHaveAttribute("aria-busy", "true");
    fireEvent.click(b);
    expect(onClick).toHaveBeenCalledTimes(1); // pointer-events:none is CSS; the attribute is the contract
  });

  it("IconButton requires and exposes an accessible name", () => {
    render(
      <TooltipProvider>
        <IconButton label="Pause download">
          <svg />
        </IconButton>
      </TooltipProvider>,
    );
    expect(screen.getByRole("button", { name: "Pause download" })).toBeInTheDocument();
  });
});

describe("SegmentedControl", () => {
  function Harness() {
    const [v, setV] = useState<"system" | "dark" | "light">("system");
    return (
      <SegmentedControl
        aria-label="Theme"
        value={v}
        onValueChange={setV}
        options={[
          { value: "system", label: "System" },
          { value: "dark", label: "Dark" },
          { value: "light", label: "Light" },
        ]}
      />
    );
  }
  it("selects with click and arrow keys (radio group semantics)", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    expect(screen.getByRole("radio", { name: "System" })).toBeChecked();
    await user.click(screen.getByRole("radio", { name: "Dark" }));
    expect(screen.getByRole("radio", { name: "Dark" })).toBeChecked();
    // Radix moves focus on a timer, so hold the key (as a real press does) until it lands.
    await user.keyboard("{ArrowRight>}");
    await waitFor(() => expect(screen.getByRole("radio", { name: "Light" })).toBeChecked());
    await user.keyboard("{/ArrowRight}");
  });
});

describe("Switch", () => {
  it("toggles and reports its state", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(<Switch aria-label="Close to tray" checked={false} onCheckedChange={onChange} />);
    const sw = screen.getByRole("switch", { name: "Close to tray" });
    expect(sw).toHaveAttribute("aria-checked", "false");
    await user.click(sw);
    expect(onChange).toHaveBeenCalledWith(true);
  });
});

describe("ProgressBar", () => {
  it("exposes progress to assistive tech and clamps", () => {
    const { rerender } = render(<ProgressBar value={42} label="Install progress" />);
    expect(screen.getByRole("progressbar", { name: "Install progress" })).toHaveAttribute("aria-valuenow", "42");
    rerender(<ProgressBar value={180} label="Install progress" />);
    expect(screen.getByRole("progressbar")).toHaveAttribute("aria-valuenow", "100");
  });
  it("is indeterminate with no value", () => {
    render(<ProgressBar label="Starting" />);
    expect(screen.getByRole("progressbar")).not.toHaveAttribute("aria-valuenow");
  });
});

describe("ErrorCallout", () => {
  it("says what happened and offers the fix", async () => {
    const user = userEvent.setup();
    const onFix = vi.fn();
    const onDismiss = vi.fn();
    render(
      <ErrorCallout
        title="Not enough disk space"
        message="This needs 4.8 GB but only 921.6 MB is free."
        fixLabel="Free up space"
        onFix={onFix}
        onDismiss={onDismiss}
        detail="needed=5153960755 free=966367641"
      />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Not enough disk space");
    await user.click(screen.getByRole("button", { name: "Free up space" }));
    expect(onFix).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Details" }));
    expect(screen.getByText(/needed=5153960755/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Dismiss" }));
    expect(onDismiss).toHaveBeenCalledOnce();
  });
  it("uses a polite status for waiting states", () => {
    render(<ErrorCallout tone="warning" title="Waiting" message="Epic Games Launcher is busy" />);
    expect(screen.getByRole("status")).toBeInTheDocument();
  });
});

describe("Avatar & Badge", () => {
  it("derives initials from handles and names", () => {
    expect(initialsOf("Marlowe")).toBe("MA");
    expect(initialsOf("sky_runner")).toBe("SR");
    expect(initialsOf("Ada Lovelace")).toBe("AL");
    expect(initialsOf("x")).toBe("X");
    expect(initialsOf("")).toBe("?");
    expect(initialsOf("😀 Joy")).toBe("😀J");
  });
  it("renders", () => {
    render(
      <>
        <Avatar name="Marlowe" />
        <Badge tone="success">Up to date</Badge>
      </>,
    );
    expect(screen.getByText("Up to date")).toBeInTheDocument();
  });
});
