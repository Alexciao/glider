import { create } from "zustand";
import type { SettingFocus } from "./transfer";
import { useUi } from "./ui";

/**
 * "Take me to that setting": a page that explains a limit (the slow-download dialog) asks Settings → Downloads to bring one
 * row into view, focus it and flash it once. The row consumes the request (`FocusRow`), so a later visit starts calm.
 */
interface SettingsFocusStore {
  target: SettingFocus | null;
  clear: () => void;
}

export const useSettingsFocus = create<SettingsFocusStore>((set) => ({ target: null, clear: () => set({ target: null }) }));

/** Open Settings → Downloads with `row` focused. */
export function goToSetting(row: SettingFocus): void {
  useSettingsFocus.setState({ target: row });
  useUi.getState().go("settings", "downloads");
}
