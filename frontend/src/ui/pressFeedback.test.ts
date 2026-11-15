import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { installPressFeedback } from "./pressFeedback";

describe("pressFeedback", () => {
  let off: () => void;
  beforeEach(() => {
    vi.useFakeTimers();
    off = installPressFeedback();
  });
  afterEach(() => {
    off();
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  const mount = (html: string) => {
    document.body.innerHTML = html;
    return document.body.firstElementChild as HTMLElement;
  };
  const down = (el: Element, init: PointerEventInit = {}) => el.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true, button: 0, ...init }));
  const up = (el: Element) => el.dispatchEvent(new PointerEvent("pointerup", { bubbles: true, button: 0 }));

  it("marks a button pressed on pointer-down and clears it after release", () => {
    const b = mount("<button>Sort</button>");
    down(b);
    expect(b).toHaveAttribute("data-pressed");
    up(b);
    vi.advanceTimersByTime(200);
    expect(b).not.toHaveAttribute("data-pressed");
  });

  it("behaves the same on every press, also when the control steals the pointer (a popover opens and the release lands elsewhere)", () => {
    const b = mount('<button role="combobox">Sort</button><div id="out"></div>');
    for (let i = 0; i < 3; i++) {
      down(b);
      expect(b).toHaveAttribute("data-pressed");
      up(document.getElementById("out")!);
      vi.advanceTimersByTime(200);
      expect(b).not.toHaveAttribute("data-pressed");
    }
  });

  it("holds a very quick click long enough to be seen", () => {
    const b = mount("<button>Sort</button>");
    down(b);
    up(b);
    vi.advanceTimersByTime(60);
    expect(b).toHaveAttribute("data-pressed");
    vi.advanceTimersByTime(100);
    expect(b).not.toHaveAttribute("data-pressed");
  });

  it("presses the control around the click target, not text inside it", () => {
    const b = mount("<button><span>Sort</span></button>");
    down(b.firstElementChild!);
    expect(b).toHaveAttribute("data-pressed");
  });

  it("ignores disabled controls, other mouse buttons and things that are not controls", () => {
    const d = mount("<button disabled>x</button>");
    down(d);
    expect(d).not.toHaveAttribute("data-pressed");
    const b = mount("<button>x</button>");
    down(b, { button: 2 });
    expect(b).not.toHaveAttribute("data-pressed");
    const p = mount("<p>text</p>");
    down(p);
    expect(p).not.toHaveAttribute("data-pressed");
  });

  it("presses on Space and Enter, once per press, and lets go on key-up", () => {
    const b = mount("<button>x</button>");
    b.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true }));
    expect(b).toHaveAttribute("data-pressed");
    b.dispatchEvent(new KeyboardEvent("keydown", { key: " ", bubbles: true, repeat: true }));
    b.dispatchEvent(new KeyboardEvent("keyup", { key: " ", bubbles: true }));
    vi.advanceTimersByTime(200);
    expect(b).not.toHaveAttribute("data-pressed");
    b.dispatchEvent(new KeyboardEvent("keydown", { key: "a", bubbles: true }));
    expect(b).not.toHaveAttribute("data-pressed");
  });

  it("never leaves a control squeezed if the release is lost", () => {
    const b = mount("<button>x</button>");
    down(b);
    vi.advanceTimersByTime(4200);
    expect(b).not.toHaveAttribute("data-pressed");
  });
});
