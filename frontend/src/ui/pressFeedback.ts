/**
 * One press feedback for every control, every time. A control that opens a popover on pointer-down (Select, dropdown and
 * split-button triggers) makes Radix put `pointer-events: none` on the page at once; the browser then drops the trigger's
 * `:active` on some presses and keeps it on others, so the squeeze played on the first click and not on the next. Here the
 * press is ours instead: pointer-down (or Space/Enter) puts `data-pressed` on the control, release takes it off after a
 * minimum hold, and the `press:` Tailwind variant (styles/index.css) styles `:active` and `[data-pressed]` alike.
 * Popup triggers themselves are left out of the squeeze there (their popup is placed against them and would follow it).
 * Reduced motion removes the movement in ui.css, so only the colour change remains.
 */

/** Things that are pressed. Menu items, tabs of a list and table rows are not: they highlight instead. */
const PRESSABLE = 'button, a[href], summary, [role="button"], [role="combobox"], [role="radio"], [role="switch"], [role="checkbox"]';

/** A press shorter than this still shows the full squeeze (a quick click is ~60 ms; the squeeze eases in over 140). */
const MIN_HOLD_MS = 130;
/** Never leave a control squeezed: pointer-up can be lost (a window switch, a drag out of the page). */
const MAX_HOLD_MS = 4000;

function pressTarget(node: EventTarget | null): HTMLElement | null {
  if (!(node instanceof Element)) return null;
  const el = node.closest<HTMLElement>(PRESSABLE);
  if (!el || el.matches(":disabled") || el.getAttribute("aria-disabled") === "true") return null;
  return el;
}

/** Holds `data-pressed` on `el` until `release()` is called (and at least `MIN_HOLD_MS`). Exported for tests. */
export function holdPressed(el: HTMLElement): () => void {
  const started = performance.now();
  el.setAttribute("data-pressed", "");
  let done = false;
  let timer = window.setTimeout(release, MAX_HOLD_MS);
  function release() {
    if (done) return;
    done = true;
    window.clearTimeout(timer);
    const wait = Math.max(0, MIN_HOLD_MS - (performance.now() - started));
    timer = window.setTimeout(() => el.removeAttribute("data-pressed"), wait);
  }
  return release;
}

/** Installs the document-level listeners once. Returns the cleanup (tests). */
export function installPressFeedback(doc: Document = document): () => void {
  const onPointerDown = (e: PointerEvent) => {
    if (e.button !== 0) return;
    const el = pressTarget(e.target);
    if (!el) return;
    const release = holdPressed(el);
    const end = () => {
      doc.removeEventListener("pointerup", end, true);
      doc.removeEventListener("pointercancel", end, true);
      window.removeEventListener("blur", end);
      release();
    };
    doc.addEventListener("pointerup", end, true);
    doc.addEventListener("pointercancel", end, true);
    window.addEventListener("blur", end);
  };

  let keyRelease: (() => void) | null = null;
  const onKeyDown = (e: KeyboardEvent) => {
    if (e.repeat || (e.key !== " " && e.key !== "Enter") || keyRelease) return;
    const el = pressTarget(e.target);
    if (el) keyRelease = holdPressed(el);
  };
  const onKeyUp = () => {
    keyRelease?.();
    keyRelease = null;
  };

  doc.addEventListener("pointerdown", onPointerDown, true);
  doc.addEventListener("keydown", onKeyDown, true);
  doc.addEventListener("keyup", onKeyUp, true);
  return () => {
    doc.removeEventListener("pointerdown", onPointerDown, true);
    doc.removeEventListener("keydown", onKeyDown, true);
    doc.removeEventListener("keyup", onKeyUp, true);
  };
}
