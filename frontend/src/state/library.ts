import { create } from "zustand";
import type { GameSummary, LibraryDelta } from "../ipc/contract.gen";
import { share } from "./share";

/**
 * The owned library, normalized: one `GameSummary` per appName in a Map plus the revision it is at (docs/v0.2-design.md
 * §1). The backend pushes `LibraryDelta` events; the UI applies them in order, and on a revision gap asks for the whole
 * library again (`library.get`). The small `AppSnapshot` never carries per-game data.
 */
export interface LibraryState {
  games: ReadonlyMap<string, GameSummary>;
  /** Revision of `games`; meaningless until `loaded`. */
  revision: number;
  loaded: boolean;
}

export type DeltaResult =
  /** Applied on top of the previous revision. */
  | "applied"
  /** A reset (full library): replaced the state whatever the revision. */
  | "reset"
  /** Already applied or older than what we have: ignored. */
  | "stale"
  /** Revision skipped (or nothing loaded yet): state unchanged, the caller must refetch with `library.get`. */
  | "gap";

/** Dev-only (mock mode): lets the stress toggle rewrite every incoming game summary. */
let gameTransform: ((g: GameSummary) => GameSummary) | null = null;
export const setGameTransform = (fn: typeof gameTransform): void => void (gameTransform = fn);
const incoming = (g: GameSummary): GameSummary => (gameTransform ? gameTransform(g) : g);

export const emptyLibrary = (): LibraryState => ({ games: new Map(), revision: 0, loaded: false });

/** Pure: apply one delta. A reset keeps the identity of every game that did not change. */
export function applyLibraryDelta(state: LibraryState, delta: LibraryDelta): { state: LibraryState; result: DeltaResult } {
  if (delta.reset) {
    const next = new Map<string, GameSummary>();
    for (const raw of delta.upserts) {
      const g = incoming(raw);
      next.set(g.appName, state.loaded ? share(state.games.get(g.appName), g) : g);
    }
    return { state: { games: next, revision: delta.revision, loaded: true }, result: "reset" };
  }
  if (!state.loaded) return { state, result: "gap" };
  if (delta.revision <= state.revision) return { state, result: "stale" };
  if (delta.revision !== state.revision + 1) return { state, result: "gap" };
  const next = new Map(state.games);
  for (const raw of delta.upserts) {
    const g = incoming(raw);
    next.set(g.appName, share(state.games.get(g.appName), g));
  }
  for (const a of delta.removes) next.delete(a);
  return { state: { games: next, revision: delta.revision, loaded: true }, result: "applied" };
}

interface LibraryStore extends LibraryState {
  apply: (delta: LibraryDelta) => DeltaResult;
  /** Optimistic local edit of one game (e.g. a favorite toggle); the backend's delta overwrites it. */
  patchGame: (appName: string, patch: Partial<GameSummary>) => void;
  reset: () => void;
}

export const useLibrary = create<LibraryStore>((set, get) => ({
  ...emptyLibrary(),
  apply: (delta) => {
    const { state, result } = applyLibraryDelta(get(), delta);
    if (state !== get() && (result === "applied" || result === "reset")) set({ games: state.games, revision: state.revision, loaded: state.loaded });
    return result;
  },
  patchGame: (appName, patch) =>
    set((s) => {
      const g = s.games.get(appName);
      if (!g) return s;
      const next = new Map(s.games);
      next.set(appName, { ...g, ...patch });
      return { games: next };
    }),
  reset: () => set(emptyLibrary()),
}));

export const getGame = (appName: string): GameSummary | undefined => useLibrary.getState().games.get(appName);

/** One game, re-rendering only when that game's summary changes. */
export function useGame(appName: string | null | undefined): GameSummary | undefined {
  return useLibrary((s) => (appName ? s.games.get(appName) : undefined));
}
