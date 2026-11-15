import { useCallback, useEffect, useMemo, useRef } from "react";
import { ArrowLeft, LogIn, SearchX } from "lucide-react";
import type { AppSnapshot, GameDetail, GameSettings, GameSummary } from "../../ipc/contract.gen";
import { actions, pendingKey } from "../../state/actions";
import { useGame, useLibrary } from "../../state/library";
import { useProgress } from "../../state/progress";
import { navigate, useRouter } from "../../state/router";
import { useApp } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Button, EmptyState, Skeleton } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageScroll } from "../common/Page";
import { useNeedsSignIn } from "../common/SignInBanner";
import { deriveGameAction } from "./actionModel";
import { Hero } from "./Hero";
import { GameNotices, ThirdPartyNotice, useFailedJob } from "./Notices";
import { AboutSection } from "./sections/AboutSection";
import { DlcSection } from "./sections/DlcSection";
import { FactsRow } from "./sections/FactsRow";
import { GameSettingsSection } from "./sections/GameSettingsSection";
import { RowsSkeleton } from "./sections/parts";
import { SavesSection } from "./sections/SavesSection";
import { useGameDetail } from "./useGameDetail";

/**
 * The page for one game (route `game/<appName>`): the hero with the one primary action, then what the game is, what is
 * installed, its saves and its launch settings. It reads the library summary (always there) and `game.getDetail`
 * (fetched on arrival; the page is complete without it, the sections that need it show a placeholder).
 */
export function GameView({ appName }: { appName: string }) {
  const game = useGame(appName);
  const loaded = useLibrary((s) => s.loaded);
  const needsSignIn = useNeedsSignIn();
  if (!game) {
    return loaded ? (
      <div className="grid h-full place-items-center">
        {needsSignIn ? (
          <EmptyState
            icon={<LogIn size={20} />}
            title="Sign in to see this game"
            description="Your library hasn't loaded on this account yet."
            action={
              <Button variant="primary" onClick={() => void actions.signIn("settings")}>
                Sign in
              </Button>
            }
          />
        ) : (
          <EmptyState
            icon={<SearchX size={20} />}
            title="That game isn't in your library"
            description={`Nothing is called "${appName}" in your Epic library.`}
            action={
              <Button variant="secondary" onClick={() => navigate("library")}>
                Back to library
              </Button>
            }
          />
        )}
      </div>
    ) : (
      <PageSkeleton />
    );
  }
  return <GamePage game={game} />;
}

function BackLink() {
  const previous = useRouter((s) => s.previous);
  const label = previous?.page === "home" ? "Home" : "Library";
  return (
    <Button variant="ghost" size="md" icon={<ArrowLeft size={16} />} onClick={() => navigate(previous?.page === "home" ? "home" : "library")} className="-ml-3 mb-3 text-fg-muted">
      {label}
    </Button>
  );
}

function Frame({ children }: { children: React.ReactNode }) {
  return (
    <PageScroll game className="pb-16 pt-5">
      {children}
    </PageScroll>
  );
}

/** Boot-time placeholder: the hero's shape and the section blocks, no spinner. */
function PageSkeleton() {
  return (
    <Frame>
      <div className="g-skeleton mb-3 h-8 w-24 rounded-md" aria-hidden />
      <Skeleton className="min-h-[clamp(288px,42vh,400px)] w-full rounded-2xl" />
      <div className="mt-6 flex max-w-[720px] flex-col gap-3" aria-hidden>
        <Skeleton className="h-9 w-full" />
        <Skeleton className="h-4 w-full" />
        <Skeleton className="h-4 w-2/3" />
      </div>
    </Frame>
  );
}

function GamePage({ game }: { game: GameSummary }) {
  const { appName } = game;
  const snap = useApp((s) => s.snapshot as AppSnapshot);
  const session = snap.sessions.find((x) => x.appName === appName);
  const { detail, loading, error, reload, patch } = useGameDetail(appName, game);
  const failedJob = useFailedJob(appName);

  const pLaunch = usePending(pendingKey("launch", appName));
  const pUpdate = usePending(pendingKey("update", appName));
  const pVerify = usePending(pendingKey("verify", appName));
  const pSignIn = usePending("signIn");
  const pClose = usePending(pendingKey("close", appName));
  const pPlan = usePending(pendingKey("planning", appName));

  const jobId = game.activeJobId;
  const phase = useProgress((s) => (jobId ? s.latest[jobId]?.phase : undefined));
  const action = useMemo(
    () => deriveGameAction(snap, game, { session, phase, pending: { launch: pLaunch, update: pUpdate, verify: pVerify, signIn: pSignIn, close: pClose, plan: pPlan } }),
    [snap, game, session, phase, pLaunch, pUpdate, pVerify, pSignIn, pClose, pPlan],
  );

  // The "ready" flourish is store-owned and self-expiring; it only shows while this game is playable.
  const readyAt = useUi((s) => s.readyAt);
  const readyApp = useUi((s) => s.readyApp);
  const moment = readyAt !== null && readyApp === appName && action.kind === "play";

  // Settings writes: optimistic, serialized by always sending the latest full object, rolled back (by refetching) on failure.
  const settings = useRef<GameSettings | undefined>(detail?.settings);
  useEffect(() => {
    settings.current = detail?.settings;
  }, [detail?.settings]);
  const onSettings = useCallback(
    (change: Partial<GameSettings>) => {
      const base = settings.current;
      if (!base) return;
      const next = { ...base, ...change };
      settings.current = next;
      patch((d) => ({ ...d, settings: next }));
      void actions.updateGameSettings(appName, next).then((r) => {
        if (!r) reload();
      });
    },
    [appName, patch, reload],
  );

  const installed = game.installStatus === "installed" || game.installStatus === "broken";

  return (
    <Frame>
      <BackLink />
      <Hero snap={snap} game={game} detail={detail} action={action} moment={moment} />

      <div className="mt-5 flex flex-col gap-3 empty:hidden">
        <GameNotices game={game} detail={detail} failedJob={failedJob} session={session} />
        <ThirdPartyNotice game={game} />
      </div>

      {/* One 40 px line is reserved, so nothing below moves when the facts land under a hero that is still settling. */}
      <div className="mt-6 min-h-10">
        <FactsRow game={game} detail={detail} />
      </div>

      <div className="mt-8 flex max-w-[720px] flex-col gap-8">
        {detail && <SavesSection game={game} detail={detail} />}
        <AboutSection game={game} detail={detail} />
        {detail ? <DlcSection game={game} detail={detail} /> : loading && !error && <RowsSkeleton rows={3} />}
        {detail && installed && <GameSettingsSection detail={detail} onSettings={onSettings} />}
      </div>

      {error && !detail && <DetailError error={error} reload={reload} />}
    </Frame>
  );
}

/** The summary above is still right; only the extra sections are missing. Say so, with a way to retry. */
function DetailError({ error, reload }: { error: NonNullable<ReturnType<typeof useGameDetail>["error"]>; reload: () => void }) {
  return (
    <div className="mt-8">
      <ErrorView error={{ ...error, title: "Couldn't load this game's details", fix: "retry" }} retry={reload} />
    </div>
  );
}

export type { GameDetail };
