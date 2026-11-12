import { useEffect, useState } from "react";

/** Re-renders its caller every `intervalMs` with the current time. Use only in tiny leaf components. */
export function useNow(intervalMs = 1000, enabled = true): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    const id = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(id);
  }, [intervalMs, enabled]);
  return now;
}

/** Calls `fn` when a key chord is pressed; ignores presses while typing unless `allowInInput`. */
export function useHotkey(
  match: (e: KeyboardEvent) => boolean,
  fn: (e: KeyboardEvent) => void,
  { allowInInput = false }: { allowInInput?: boolean } = {},
): void {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!match(e)) return;
      const t = e.target as HTMLElement | null;
      const typing = !!t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.isContentEditable);
      if (typing && !allowInInput) return;
      e.preventDefault();
      fn(e);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [match, fn, allowInInput]);
}
