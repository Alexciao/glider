import { create } from "zustand";
import type { FixAction, GliderError, SaveConflict } from "../ipc/contract.gen";
import { navigate, type Page } from "./router";

/** How long the "ready to play" flourish stays on screen. */
export const READY_MOMENT_MS = 2800;

export type { Page };
export type SettingsSection =
  | "downloads"
  | "library"
  | "updates"
  | "saves"
  | "egl"
  | "overlay"
  | "links"
  | "appearance"
  | "startup"
  | "account"
  | "advanced"
  | "about"
  /** v0.1's catch-all page; Settings shows Startup and tray for it. Kept so old deep links keep working. */
  | "general";

export type DialogState =
  | { kind: "install"; appName: string }
  | { kind: "uninstall"; appName: string }
  | { kind: "move"; appName: string }
  /** Cloud and local saves both changed. `conflict` is what a failed launch returned; without it the dialog reads the saves itself. `thenPlay` = it came from Play. */
  | { kind: "saveConflict"; appName: string; conflict?: SaveConflict | null; thenPlay?: boolean }
  | { kind: "cancelJob"; jobId: string }
  | { kind: "error"; error: GliderError; retry?: () => void };

/** A failure that needs a visible, fixable home (inline callout) rather than a toast. */
export interface SurfaceError {
  error: GliderError;
  retry?: () => void;
  /** Free-form context for fix handlers (e.g. a folder that failed validation). */
  path?: string | null;
  /** The game this is about; game pages only show errors that are theirs (or global ones, when null). */
  appName?: string | null;
}

export type ErrorSurface = "hero" | "downloads" | "onboarding" | "settings" | "move" | "uninstall" | "install" | "library";

interface UiStore {
  settingsSection: SettingsSection;
  dialog: DialogState | null;
  palette: boolean;
  pending: Record<string, number>;
  errors: Partial<Record<ErrorSurface, SurfaceError>>;
  /** Set when an install/update/import completes while the app is open; that game's page plays the moment once. */
  readyAt: number | null;
  readyApp: string | null;
  /** Bumps to tell the log viewer to scroll into view (openLogs fix action). */
  logsFocus: number;
  /** User changed the theme since boot: stops a ?theme= URL override from fighting them. */
  themeTouched: boolean;

  /** Navigate to a page (and optionally a Settings section). Games: use `goGame` from the router. */
  go: (page: Exclude<Page, "game">, section?: SettingsSection) => void;
  openDialog: (d: DialogState) => void;
  closeDialog: () => void;
  setPalette: (open: boolean) => void;
  begin: (key: string) => void;
  end: (key: string) => void;
  setError: (surface: ErrorSurface, e: SurfaceError | undefined) => void;
  markReady: (appName: string) => void;
  consumeReady: () => void;
  focusLogs: () => void;
  touchTheme: () => void;
}

export const useUi = create<UiStore>((set) => ({
  settingsSection: "downloads",
  dialog: null,
  palette: false,
  pending: {},
  errors: {},
  readyAt: null,
  readyApp: null,
  logsFocus: 0,
  themeTouched: false,

  go: (page, section) => {
    navigate({ page });
    set((s) => ({ settingsSection: section ?? s.settingsSection, palette: false }));
  },
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
  markReady: (appName) => {
    const at = Date.now();
    set({ readyAt: at, readyApp: appName });
    // A one-off: it expires on its own so it never replays on a later visit.
    setTimeout(() => set((s) => (s.readyAt === at ? { readyAt: null, readyApp: null } : s)), READY_MOMENT_MS);
  },
  consumeReady: () => set({ readyAt: null, readyApp: null }),
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
  activate: "Link account",
  resolveSaves: "Choose saves",
  installPrerequisites: "Set up now",
  playOffline: "Play offline",
  update: "Update",
};
