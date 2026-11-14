import { useEffect } from "react";
import { create } from "zustand";
import type { FreeGame, GliderError } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { toGliderError } from "./actions";
import { useApp } from "./store";

/**
 * "Free this week" (Epic's free-games promotion), fetched once per session and refreshed when it is older than
 * `STALE_MS`. The list is cached in memory, so Home paints it instantly on later visits; a failed refresh keeps showing
 * the last good list and only reports the error when there is nothing to show.
 */
export const STALE_MS = 30 * 60 * 1000;

export type FreeGamesStatus = "idle" | "loading" | "ready" | "error";

interface FreeGamesStore {
  status: FreeGamesStatus;
  items: FreeGame[];
  fetchedAt: number | null;
  error: GliderError | null;
  load: (force?: boolean) => Promise<void>;
}

let inflight: Promise<void> | null = null;

export const useFreeGamesStore = create<FreeGamesStore>((set, get) => ({
  status: "idle",
  items: [],
  fetchedAt: null,
  error: null,
  load: (force = false) => {
    const s = get();
    if (inflight) return inflight;
    if (!force && s.fetchedAt !== null && Date.now() - s.fetchedAt < STALE_MS) return Promise.resolve();
    set({ status: s.items.length > 0 ? "ready" : "loading", error: null });
    inflight = (async () => {
      try {
        const r = await rpc().call("freeGames.get");
        set({ status: "ready", items: r.items, fetchedAt: Date.now(), error: null });
      } catch (e) {
        const keep = get().items.length > 0;
        set({ status: keep ? "ready" : "error", error: keep ? null : toGliderError(e) });
      } finally {
        inflight = null;
      }
    })();
    return inflight;
  },
}));

/** Free-now items first (soonest to end first), then upcoming (soonest to start first). */
export function orderFreeGames(items: readonly FreeGame[]): FreeGame[] {
  const t = (iso: string) => Date.parse(iso) || 0;
  return [...items].sort((a, b) => {
    if (a.status !== b.status) return a.status === "freeNow" ? -1 : 1;
    return a.status === "freeNow" ? t(a.endsAt) - t(b.endsAt) : t(a.startsAt) - t(b.startsAt);
  });
}

/** The free games, loading them when Home mounts and the app is online (nothing is requested while offline). */
export function useFreeGames(): { status: FreeGamesStatus; items: FreeGame[]; error: GliderError | null; offline: boolean; reload: () => void } {
  const status = useFreeGamesStore((s) => s.status);
  const items = useFreeGamesStore((s) => s.items);
  const error = useFreeGamesStore((s) => s.error);
  const offline = useApp((s) => s.snapshot?.connectivity === "offline");
  const load = useFreeGamesStore((s) => s.load);
  useEffect(() => {
    if (!offline) void load();
  }, [offline, load]);
  return { status, items: orderFreeGames(items), error, offline, reload: () => void load(true) };
}

export function resetFreeGamesForTests(): void {
  inflight = null;
  useFreeGamesStore.setState({ status: "idle", items: [], fetchedAt: null, error: null });
}

const DAY = 86_400_000;
const dayFormat = (t: number): string => new Date(t).toLocaleDateString(undefined, { weekday: "short", month: "short", day: "numeric" });

/**
 * When a free game is free, in words: "Until Thu, Nov 20" / "Ends today" for what is free now, "From Thu, Nov 20" for what
 * is coming. Owned games say so instead (the dates no longer matter).
 */
export function freeWindowLabel(g: FreeGame, now: number = Date.now()): string {
  if (g.owned) return "In your library";
  const end = Date.parse(g.endsAt);
  const start = Date.parse(g.startsAt);
  if (g.status === "upcoming") return Number.isNaN(start) ? "Coming soon" : `From ${dayFormat(start)}`;
  if (Number.isNaN(end)) return "Free now";
  return end - now < DAY ? "Ends today" : `Until ${dayFormat(end)}`;
}
