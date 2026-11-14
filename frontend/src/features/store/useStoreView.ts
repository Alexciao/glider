import { useEffect, useState } from "react";
import type { StoreState } from "../../ipc/contract.gen";
import { rpc } from "../../ipc/rpc";

/** The native Store view's state: `store.getState` once, then every `store` event. */
export function useStoreView(): StoreState | null {
  const [state, setState] = useState<StoreState | null>(null);
  useEffect(() => {
    let alive = true;
    const off = rpc().on("store", (s) => {
      if (alive) setState(s);
    });
    rpc()
      .call("store.getState")
      .then((s) => {
        if (alive) setState((cur) => cur ?? s);
      })
      .catch(() => {});
    return () => {
      alive = false;
      off();
    };
  }, []);
  return state;
}

/** Anything the HTML draws above the page (dialogs, menus, the palette) would sit *under* the native view, so the page covers it. */
export const OVERLAY_SELECTOR = '[role="dialog"],[role="alertdialog"],[role="menu"],[role="listbox"],[data-radix-popper-content-wrapper]';

/** True while any Radix overlay is mounted. Calls `onChange` (deduplicated) as it comes and goes. */
export function useOverlayWatcher(enabled: boolean, onChange: (covered: boolean) => void): void {
  useEffect(() => {
    if (!enabled) return;
    let last = false;
    const check = () => {
      const now = document.querySelector(OVERLAY_SELECTOR) !== null;
      if (now !== last) {
        last = now;
        onChange(now);
      }
    };
    const mo = new MutationObserver(check);
    mo.observe(document.body, { childList: true });
    check();
    return () => {
      mo.disconnect();
      if (last) {
        try {
          onChange(false);
        } catch {
          // teardown
        }
      }
    };
  }, [enabled, onChange]);
}
