import { useEffect, useRef, useState } from "react";
import { connectRpc, postHostTheme, rpc } from "../ipc/rpc";
import { isHosted } from "../ipc/client";
import { startBridge } from "../state/bridge";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";

let booting: Promise<void> | null = null;

/** Idempotent (StrictMode runs effects twice): connect, wire events, load the first snapshot. */
export function boot(): Promise<void> {
  booting ??= connectRpc().then(startBridge).then(() => undefined);
  return booting;
}

export type BootState = "connecting" | "ready" | "failed";

export function useBoot(): { state: BootState; retry: () => void } {
  const hasSnapshot = useApp((s) => s.snapshot !== null);
  const [attempt, setAttempt] = useState(0);
  const [failedAttempt, setFailedAttempt] = useState<number | null>(null);

  useEffect(() => {
    boot().catch(() => {
      booting = null;
      setFailedAttempt(attempt);
    });
  }, [attempt]);

  const failed = failedAttempt === attempt;
  return { state: hasSnapshot ? "ready" : failed ? "failed" : "connecting", retry: () => setAttempt((n) => n + 1) };
}

/** Tells the host to reveal the window after the first real paint (double rAF), never before. */
export function useRevealWhenPainted(ready: boolean): void {
  useEffect(() => {
    if (!ready) return;
    let cancelled = false;
    const id = requestAnimationFrame(() =>
      requestAnimationFrame(() => {
        if (cancelled) return;
        try {
          void rpc().call("app.ready").catch(() => undefined);
        } catch {
          /* not connected: nothing to reveal */
        }
      }),
    );
    return () => {
      cancelled = true;
      cancelAnimationFrame(id);
    };
  }, [ready]);
}

const params = typeof location === "undefined" ? new URLSearchParams() : new URLSearchParams(location.search);

/** Applies theme + backdrop to <html>. In the host the URL already set them before first paint. */
export function useThemeAndBackdrop(): void {
  const theme = useApp((s) => s.snapshot?.settings.theme);
  const backdrop = useApp((s) => s.snapshot?.app.backdrop);
  const themeTouched = useUi((s) => s.themeTouched);

  useEffect(() => {
    const forced = !isHosted() && !themeTouched ? params.get("theme") : null;
    const pref = forced ?? theme ?? "system";
    const mq = matchMedia("(prefers-color-scheme: light)");
    const apply = () => {
      const resolved = pref === "system" ? (mq.matches ? "light" : "dark") : pref;
      document.documentElement.dataset.theme = resolved;
    };
    apply();
    if (pref !== "system") return;
    mq.addEventListener("change", apply);
    return () => mq.removeEventListener("change", apply);
  }, [theme, themeTouched]);

  useEffect(() => {
    if (backdrop) document.documentElement.dataset.backdrop = backdrop;
  }, [backdrop]);

  // The native frame / Mica tint follows the *setting* (not the resolved theme), so "system" stays live.
  const previousTheme = useRef<typeof theme>(undefined);
  useEffect(() => {
    if (theme && previousTheme.current !== undefined && previousTheme.current !== theme) postHostTheme(theme);
    previousTheme.current = theme;
  }, [theme]);
}
