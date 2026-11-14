import { create } from "zustand";

/**
 * What the last Play said about a game when nothing (or only a launcher) started. Kept per game for the page that shows
 * it; cleared by the user, by a successful activation or by a normal launch. Never persisted: it describes this session.
 */
export type LaunchNoteKind = "needsActivation" | "thirdPartyLauncher";

export interface LaunchNote {
  kind: LaunchNoteKind;
  message: string | null;
}

interface LaunchNoteStore {
  notes: Readonly<Record<string, LaunchNote>>;
  set: (appName: string, note: LaunchNote) => void;
  clear: (appName: string) => void;
}

export const useLaunchNotes = create<LaunchNoteStore>((set) => ({
  notes: {},
  set: (appName, note) => set((s) => ({ notes: { ...s.notes, [appName]: note } })),
  clear: (appName) =>
    set((s) => {
      if (!(appName in s.notes)) return s;
      const { [appName]: _gone, ...rest } = s.notes;
      return { notes: rest };
    }),
}));

export const useLaunchNote = (appName: string): LaunchNote | undefined => useLaunchNotes((s) => s.notes[appName]);
