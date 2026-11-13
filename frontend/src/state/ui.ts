import { create } from "zustand";
import type { FixAction, GliderError } from "../ipc/contract.gen";

/** How long the "ready to play" flourish stays on screen. */
export const READY_MOMENT_MS = 2800;

export type Page = "library" | "downloads" | "settings";
export type SettingsSection = "general" | "downloads" | "game" | "egl" | "advanced" | "about";

export type DialogState =
  | { kind: "install" }
  | { kind: "uninstall" }
  | { kind: "move" }
  | { kind: "browserSignIn" }
  | { kind: "cancelJob"; jobId: string }
  | { kind: "error"; error: GliderError; retry?: () => void };

/** A failure that needs a visible, fixable home (inline callout) rather than a toast. */
export interface SurfaceError {
  error: GliderError;
  retry?: () => void;
  /** Free-form context for fix handlers (e.g. a folder that failed validation). */
  path?: string | null;
}

export type ErrorSurface = "hero" | "downloads" | "onboarding" | "settings" | "move" | "uninstall" | "install";

interface UiStore {
  page: Page;
  settingsSection: SettingsSection;
  dialog: DialogState | null;
  palette: boolean;
  pending: Record<string, number>;
  errors: Partial<Record<ErrorSurface, SurfaceError>>;
  /** Set when an install/update/import completes while the app is open; Library plays the moment once. */
  readyAt: number | null;
  /** Bumps to tell the log viewer to scroll into view (openLogs fix action). */
  logsFocus: number;
  /** User changed the theme since boot: stops a ?theme= URL override from fighting them. */
  themeTouched: boolean;

  go: (page: Page, section?: SettingsSection) => void;
  openDialog: (d: DialogState) => void;
  closeDialog: () => void;
  setPalette: (open: boolean) => void;
  begin: (key: string) => void;
  end: (key: string) => void;
  setError: (surface: ErrorSurface, e: SurfaceError | undefined) => void;
  markReady: () => void;
  consumeReady: () => void;
  focusLogs: () => void;
  touchTheme: () => void;
}

export const useUi = create<UiStore>((set) => ({
  page: "library",
  settingsSection: "general",
  dialog: null,
  palette: false,
  pending: {},
  errors: {},
  readyAt: null,
  logsFocus: 0,
  themeTouched: false,

  go: (page, section) => set((s) => ({ page, settingsSection: section ?? s.settingsSection, palette: false })),
  openDialog: (dialog) => set({ dialog, palette: false }),
  closeDialog: () => set({ dialog: null }),
  setPalette: (palette) => set({ palette }),
  begin: (key) => set((s) => ({ pending: { ...s.pending, [key]: (s.pending[key] ?? 0) + 1 } })),
  end: (key) =>
    set((s) => {
      const n = (s.pending[key] ?? 1) - 1;
      const pending = { ...s.pending };
      if (n <= 0) delete pending[key];
      else pending[key] = n;
      return { pending };
    }),
  setError: (surface, e) => set((s) => ({ errors: { ...s.errors, [surface]: e } })),
  markReady: () => {
    const at = Date.now();
    set({ readyAt: at });
    // A one-off: it expires on its own so it never replays on a later visit.
    setTimeout(() => set((s) => (s.readyAt === at ? { readyAt: null } : s)), READY_MOMENT_MS);
  },
  consumeReady: () => set({ readyAt: null }),
  focusLogs: () => set((s) => ({ logsFocus: s.logsFocus + 1 })),
  touchTheme: () => set({ themeTouched: true }),
}));

export const usePending = (key: string): boolean => useUi((s) => (s.pending[key] ?? 0) > 0);

export const FIX_LABELS: Record<FixAction, string> = {
  signIn: "Sign in",
  freeSpace: "Free up space",
  repair: "Repair",
  retry: "Try again",
  chooseFolder: "Choose folder",
  openLogs: "Open logs",
  closeGame: "I've closed it",
  waitForEgl: "Got it",
};
