import { useCallback, useEffect, useState } from "react";
import type { GameDetail, GameSummary, GliderError } from "../../ipc/contract.gen";
import { toGliderError } from "../../state/actions";
import { rpc } from "../../ipc/rpc";

export interface DetailState {
  detail: GameDetail | null;
  /** The first load has not answered yet. A refresh with data on screen is not "loading". */
  loading: boolean;
  error: GliderError | null;
  reload: () => void;
  /** Optimistic local edit (settings written but not yet echoed); the next fetch replaces it. */
  patch: (fn: (d: GameDetail) => GameDetail) => void;
}

/**
 * `game.getDetail` for one game. It reads Core's files (cheap), so it simply refetches whenever the game's summary
 * changes (an install finished, saves synced, a flag flipped): the summary object only changes identity when the
 * backend sent a real change. Data on screen stays while a refresh is in flight; stale answers are dropped.
 */
export function useGameDetail(appName: string, summary: GameSummary | undefined): DetailState {
  const [state, setState] = useState<{ appName: string; detail: GameDetail | null; error: GliderError | null; done: boolean }>({
    appName,
    detail: null,
    error: null,
    done: false,
  });
  const [tick, setTick] = useState(0);

  useEffect(() => {
    let alive = true;
    rpc()
      .call("game.getDetail", { appName })
      .then((detail) => {
        if (alive) setState({ appName, detail, error: null, done: true });
      })
      .catch((e: unknown) => {
        if (alive) setState((s) => ({ appName, detail: s.appName === appName ? s.detail : null, error: toGliderError(e), done: true }));
      });
    return () => {
      alive = false;
    };
  }, [appName, summary, tick]);

  const reload = useCallback(() => setTick((n) => n + 1), []);
  const patch = useCallback((fn: (d: GameDetail) => GameDetail) => setState((s) => (s.detail ? { ...s, detail: fn(s.detail) } : s)), []);
  const mine = state.appName === appName;
  return { detail: mine ? state.detail : null, loading: !mine || !state.done, error: mine ? state.error : null, reload, patch };
}
