import { describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MiniSparkline, PhaseStepper, ReorderList, StatGroups, ThroughputChart, monotonePath, nearestIndex, niceCeil, slotOffset, type ChartData } from "./index";

describe("niceCeil", () => {
  it("rounds up to a clean axis maximum", () => {
    const MB = 1024 ** 2;
    expect(niceCeil(0)).toBe(1);
    expect(niceCeil(91 * MB * 1.3)).toBe(150 * MB);
    expect(niceCeil(58 * MB)).toBe(60 * MB);
    expect(niceCeil(1.2 * 1024 ** 3)).toBe(1.5 * 1024 ** 3);
    expect(niceCeil(7)).toBe(8);
  });
});

describe("nearestIndex", () => {
  it("finds the closest sample on an ascending time axis", () => {
    const t = [0, 1, 2, 5, 10];
    expect(nearestIndex(t, -3)).toBe(0);
    expect(nearestIndex(t, 1.4)).toBe(1);
    expect(nearestIndex(t, 3.6)).toBe(3);
    expect(nearestIndex(t, 99)).toBe(4);
    expect(nearestIndex([], 1)).toBe(-1);
  });
});

describe("monotonePath", () => {
  const ys = (d: string) => [...d.matchAll(/C[\d.-]+ ([\d.-]+) [\d.-]+ ([\d.-]+) [\d.-]+ ([\d.-]+)/g)].flatMap((m) => [Number(m[1]), Number(m[2]), Number(m[3])]);
  it("never overshoots the samples (no dip below a flat run, no invented peak)", () => {
    const d = monotonePath([0, 10, 20, 30, 40], [50, 50, 10, 10, 10]);
    const all = ys(d);
    expect(Math.min(...all)).toBeGreaterThanOrEqual(10 - 1e-6);
    expect(Math.max(...all)).toBeLessThanOrEqual(50 + 1e-6);
  });
  it("handles 0, 1 and 2 points", () => {
    expect(monotonePath([], [])).toBe("");
    expect(monotonePath([5], [7])).toBe("M5 7");
    expect(monotonePath([0, 10], [0, 10])).toMatch(/^M0 0C/);
  });
});

describe("ThroughputChart", () => {
  function setup(active = true) {
    let notify: () => void = () => {};
    const t: number[] = [0, 1, 2];
    const net: number[] = [10, 20, 30];
    const disk: number[] = [5, 6, 7];
    const read = vi.fn((): ChartData => ({ t, values: [net, disk] }));
    const subscribe = (n: () => void) => {
      notify = n;
      return () => {
        notify = () => {};
      };
    };
    const ui = (a: boolean) => (
      <ThroughputChart
        series={[
          { key: "n", label: "Network", color: "red", area: true },
          { key: "d", label: "Disk write", color: "blue" },
        ]}
        read={read}
        subscribe={subscribe}
        active={a}
        format={(v) => `${v} B/s`}
        formatTime={(s) => `${s}s`}
        markers={() => ({ average: 20, peak: 30 })}
        label="Speed over time"
      />
    );
    const view = render(ui(active));
    return { view, read, notify: () => act(() => notify()), t, net, disk, ui };
  }

  it("draws once on mount, redraws on notification without re-rendering React", () => {
    const { view, read, notify, t, net, disk } = setup();
    const paths = view.container.querySelectorAll("svg path");
    const line = [...paths].find((p) => p.getAttribute("stroke") === "red")!;
    expect(line.getAttribute("d")).toMatch(/^M/);
    const before = line.getAttribute("d");
    const calls = read.mock.calls.length;
    t.push(3);
    net.push(40);
    disk.push(8);
    notify();
    expect(read.mock.calls.length).toBe(calls + 1);
    expect(line.getAttribute("d")).not.toBe(before);
  });

  it("names its series (legend), labels the average and the peak, and exposes a summary", () => {
    const { view } = setup();
    expect(screen.getByRole("list", { name: "Series" })).toHaveTextContent("NetworkDisk write");
    expect(view.container.textContent).toContain("Average 20 B/s");
    expect(view.container.textContent).toContain("Peak 30 B/s");
    expect(screen.getByRole("group", { name: /Speed over time\. Network now 30 B\/s, Disk write now 7 B\/s\./ })).toBeInTheDocument();
  });

  it("does not draw or listen while inactive (hidden window), and repaints on activation", () => {
    const { view, read, notify, ui } = setup(false);
    expect(read).not.toHaveBeenCalled();
    notify();
    expect(read).not.toHaveBeenCalled();
    view.rerender(ui(true));
    expect(read).toHaveBeenCalled();
  });

  it("shows a readout on keyboard focus and moves it with the arrow keys", async () => {
    const { view } = setup();
    const plot = screen.getByRole("group", { name: /Speed over time/ });
    act(() => plot.focus());
    const live = view.container.querySelector("[aria-live='polite']")!;
    expect(live.textContent).toBe("2s: Network 30 B/s, Disk write 7 B/s");
    fireEvent.keyDown(plot, { key: "ArrowLeft" });
    expect(live.textContent).toBe("1s: Network 20 B/s, Disk write 6 B/s");
    fireEvent.keyDown(plot, { key: "Home" });
    expect(live.textContent).toBe("0s: Network 10 B/s, Disk write 5 B/s");
  });
});

describe("MiniSparkline", () => {
  it("writes a path from the data and none while inactive", () => {
    const data = [1, 3, 2, 5];
    const { container, rerender } = render(<MiniSparkline read={() => data} subscribe={() => () => {}} />);
    expect(container.querySelector("path[stroke]")!.getAttribute("d")).toMatch(/^M0.0 /);
    rerender(<MiniSparkline read={() => [1]} subscribe={() => () => {}} />);
    expect(container.querySelector("path[stroke]")!.getAttribute("d")).toBe("");
  });
});

describe("PhaseStepper", () => {
  const steps = [
    { id: "a", label: "Preparing" },
    { id: "b", label: "Downloading" },
    { id: "c", label: "Installing" },
  ];
  it("is a list of numbered steps, not a control: finished are done, one is current, no interactive roles", () => {
    render(<PhaseStepper steps={steps} current="b" />);
    const list = screen.getByRole("list", { name: "Steps" });
    const items = screen.getAllByRole("listitem");
    expect(items).toHaveLength(3);
    expect(items[0]).toHaveTextContent("Preparing, done");
    expect(items[1]).toHaveAttribute("aria-current", "step");
    expect(items[1]).toHaveTextContent("Downloading, in progress");
    expect(items[2]).not.toHaveAttribute("aria-current");
    expect(list.querySelector("button, [role=tab], [role=radio], input")).toBeNull();
    expect(items[2]).toHaveTextContent("3"); // upcoming steps show their number
  });
  it("re-divides when the list shrinks and renders nothing for a single step", () => {
    const { rerender, container } = render(<PhaseStepper steps={steps} current="c" />);
    expect(screen.getAllByRole("listitem")).toHaveLength(3);
    rerender(<PhaseStepper steps={[steps[1]!, steps[2]!]} current="c" />);
    expect(screen.getAllByRole("listitem")).toHaveLength(2);
    rerender(<PhaseStepper steps={[steps[0]!]} current="a" />);
    expect(container).toBeEmptyDOMElement();
  });
  it("marks nothing current when the phase is not in the list", () => {
    render(<PhaseStepper steps={steps} current="zzz" />);
    expect(document.querySelector("[aria-current]")).toBeNull();
  });
});

describe("StatGroups", () => {
  it("renders titled groups as label/value definition lists", () => {
    render(
      <StatGroups
        groups={[
          { id: "speed", title: "Speed", rows: [{ key: "now", label: "Now", value: "48.2 MB/s" }] },
          { id: "time", title: "Time", rows: [{ key: "el", label: "Elapsed", value: "4:12" }] },
        ]}
      />,
    );
    expect(screen.getByRole("region", { name: "Speed" })).toHaveTextContent("Now48.2 MB/s");
    expect(screen.getByRole("region", { name: "Time" })).toHaveTextContent("Elapsed4:12");
  });
  it("stacks groups into the requested columns and drops unnamed ones", () => {
    render(
      <StatGroups
        columns={[["b"], ["a"]]}
        groups={[
          { id: "a", title: "A", rows: [{ key: "x", label: "x", value: "1" }] },
          { id: "b", title: "B", rows: [{ key: "y", label: "y", value: "2" }] },
          { id: "c", title: "C", rows: [{ key: "z", label: "z", value: "3" }] },
        ]}
      />,
    );
    expect(screen.getAllByRole("region").map((r) => r.getAttribute("aria-label"))).toEqual(["B", "A"]);
  });
});

describe("ReorderList", () => {
  const items = ["Alpha", "Beta", "Gamma"];
  function Harness({ onMove }: { onMove: (id: string, to: number) => void }) {
    return (
      <ReorderList
        aria-label="Queue"
        items={items}
        getId={(s) => s}
        getLabel={(s) => s}
        onMove={onMove}
        renderItem={(s, { handle }) => (
          <div>
            {handle}
            {s}
          </div>
        )}
      />
    );
  }
  it("picks up with Space, moves with the arrows, drops with Space: one onMove with the new index", async () => {
    const onMove = vi.fn();
    const user = userEvent.setup();
    render(<Harness onMove={onMove} />);
    const handle = screen.getByRole("button", { name: "Reorder Alpha, position 1 of 3" });
    handle.focus();
    await user.keyboard(" ");
    expect(handle).toHaveAttribute("aria-pressed", "true");
    await user.keyboard("{ArrowDown}{ArrowDown}{ArrowDown}");
    expect(screen.getByRole("status")).toHaveTextContent("Moved Alpha, position 3 of 3.");
    await user.keyboard(" ");
    expect(onMove).toHaveBeenCalledExactlyOnceWith("Alpha", 2);
  });
  it("Escape puts the row back and commits nothing", async () => {
    const onMove = vi.fn();
    const user = userEvent.setup();
    render(<Harness onMove={onMove} />);
    screen.getByRole("button", { name: /Reorder Beta/ }).focus();
    await user.keyboard(" {ArrowUp}{Escape}");
    expect(onMove).not.toHaveBeenCalled();
    expect(screen.getByRole("status")).toHaveTextContent("Cancelled, back at Beta, position 2 of 3.");
  });
  it("offers no grip for a single row", () => {
    render(
      <ReorderList aria-label="Queue" items={["Solo"]} getId={(s) => s} getLabel={(s) => s} onMove={() => {}} renderItem={(s, { handle }) => <div>{handle}{s}</div>} />,
    );
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });
  it("computes the slot offset from the rows passed over", () => {
    expect(slotOffset([40, 50, 60], 0, 2)).toBe(110);
    expect(slotOffset([40, 50, 60], 2, 0)).toBe(-90);
    expect(slotOffset([40, 50, 60], 1, 1)).toBe(0);
  });
});
