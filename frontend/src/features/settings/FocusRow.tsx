import { useEffect, useRef, useState, type ReactNode } from "react";
import { useSettingsFocus } from "../../state/settingsFocus";
import type { SettingFocus } from "../../state/transfer";
import { cx } from "../../ui";

/** How long the row stays tinted after it is brought into view. */
const FLASH_MS = 1600;

/**
 * Wraps one Settings row so another page can send the user straight to it (`goToSetting`): the row scrolls into view,
 * its control takes focus and the row is tinted once, then fades. Costs nothing when nobody asked.
 */
export function FocusRow({ id, children }: { id: SettingFocus; children: ReactNode }) {
  const wanted = useSettingsFocus((s) => s.target === id);
  const ref = useRef<HTMLDivElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [flash, setFlash] = useState(false);

  useEffect(() => {
    if (!wanted) return;
    const row = ref.current;
    useSettingsFocus.getState().clear();
    if (!row) return;
    row.scrollIntoView({ block: "center" });
    row.querySelector<HTMLElement>('[tabindex="0"], button, input, [role="combobox"]')?.focus({ preventScroll: true });
    setFlash(true);
    timer.current = setTimeout(() => setFlash(false), FLASH_MS);
  }, [wanted]);
  // The request is cleared as soon as it is taken, which re-runs the effect above: the timer must outlive that.
  useEffect(() => () => clearTimeout(timer.current), []);

  return (
    <div ref={ref} className={cx("-mx-3 rounded-lg px-3 transition-colors duration-700 ease-out", flash ? "bg-accent-soft" : "bg-transparent")}>
      {children}
    </div>
  );
}
