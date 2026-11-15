import { navigate } from "./router";
import type { SettingFocus } from "./transfer";

/**
 * "Take me to that setting": a page that explains a limit (the slow-download dialog) opens Settings → Downloads with one row
 * brought into view, focused and flashed once. It is the same `settings?focus=<row>` route a link can use; Settings consumes
 * the request, so a later visit starts calm.
 */
export function goToSetting(row: SettingFocus): void {
  navigate({ page: "settings", focus: row });
}
