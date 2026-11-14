import { ArrowLeft } from "lucide-react";
import { useHero } from "../../state/bridge";
import { actions, pendingKey } from "../../state/actions";
import { useGame, useLibrary } from "../../state/library";
import { navigate } from "../../state/router";
import { historyJobs, useSnap } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Button, EmptyState } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { HeroArt } from "./HeroArt";
import { PrimaryAction } from "./PrimaryAction";
import { StatusLine } from "./StatusLine";
import { Facts, JobLine, MoreMenu, SaveConflictCallout } from "./LibraryParts";
import type { GameSummary } from "../../ipc/contract.gen";

/**
 * Interim per-game page: the v0.1 hero (art, title, one big button, facts) for whichever game is selected. The real
 * detail page (DLC, components, saves, settings, shortcuts) is built on top of the same stores later.
 */
export function GameView({ appName }: { appName: string }) {
  const game = useGame(appName);
  const loaded = useLibrary((s) => s.loaded);
  if (!game) {
    return (
      <div className="grid h-full place-items-center">
        <EmptyState
          icon={<ArrowLeft size={20} />}
          title={loaded ? "That game isn't in your library" : "Loading…"}
          description={loaded ? `Nothing is called "${appName}" in your Epic library.` : undefined}
          action={
            <Button variant="secondary" onClick={() => navigate("library")}>
              Back to library
            </Button>
          }
        />
      </div>
    );
  }
  return <GameHero game={game} />;
}

function GameHero({ game }: { game: GameSummary }) {
  const { appName } = game;
  const hero = useHero(game);
  const heroError = useUi((s) => s.errors.hero);
  const go = useUi((s) => s.go);
  const openDialog = useUi((s) => s.openDialog);
  const jobs = useSnap((s) => s.queue.jobs);
  const session = useSnap((s) => s.sessions.find((x) => x.appName === appName));
  const pInstall = usePending(pendingKey("install", appName));
  const pUpdate = usePending(pendingKey("update", appName));
  const pVerify = usePending(pendingKey("verify", appName));
  const pLaunch = usePending(pendingKey("launch", appName));
  const pSignIn = usePending("signIn");
  const starting = pInstall || pUpdate || pVerify || pLaunch || pSignIn;
  // The ready flourish is store-owned and self-expiring; it only shows while this game is playable.
  const readyAt = useUi((s) => s.readyAt);
  const readyApp = useUi((s) => s.readyApp);
  const moment = readyAt !== null && readyApp === appName && hero.kind === "play";

  const failedJob = historyJobs(jobs).find((j) => j.appName === appName && j.status === "failed" && j.error);
  const job = hero.job;
  const showJobLine = job && (hero.kind === "working" || hero.kind === "resume");
  // Errors belong to the game they are about (or to nobody in particular).
  const shownError = heroError && (!heroError.appName || heroError.appName === appName) ? heroError : undefined;

  const press = () => {
    switch (hero.target) {
      case "signIn":
        void actions.signIn("hero");
        break;
      case "install":
        openDialog({ kind: "install", appName });
        break;
      case "update":
        void actions.update(appName);
        break;
      case "launch":
        void actions.launch(appName);
        break;
      case "repair":
        void actions.verify(appName);
        break;
      case "resume":
        if (job) void actions.resumeJob(job.id);
        break;
      case "downloads":
        go("downloads");
        break;
      case "none":
        break;
    }
  };

  return (
    <div className="relative h-full overflow-hidden">
      <HeroArt art={game.art} />
      {/* Everything over art is scoped to the dark token set: AA text on any picture, in either theme. */}
      <div data-theme="dark" className="relative z-10 flex h-full flex-col justify-end px-12 pb-10 max-[1100px]:px-8 max-[1100px]:pb-7">
        <button
          type="button"
          onClick={() => navigate("library")}
          className="absolute left-10 top-6 flex h-8 items-center gap-1.5 rounded-lg px-2.5 text-sm text-fg-muted transition-colors duration-150 hover:bg-surface-hover hover:text-fg max-[1100px]:left-6"
        >
          <ArrowLeft size={15} /> Library
        </button>

        <div className="g-rise" style={{ ["--g-i" as string]: 0 }}>
          <h1
            title={game.title}
            className="line-clamp-2 max-w-[28ch] text-balance text-3xl font-semibold tracking-tighter text-fg [overflow-wrap:anywhere] [text-shadow:0_2px_24px_oklch(0_0_0/0.35)] max-[1100px]:max-w-[34ch] max-[1100px]:text-2xl"
          >
            {game.title}
          </h1>
          <StatusLine status={hero.status} moment={moment} className="mt-2" />
        </div>

        {showJobLine && job && (
          <div className="g-rise mt-4" style={{ ["--g-i" as string]: 1 }}>
            <JobLine job={job} />
          </div>
        )}

        {(shownError || failedJob?.error) && (
          <div className="g-rise mt-5 w-[min(560px,100%)]" style={{ ["--g-i" as string]: 1 }}>
            {shownError ? (
              <ErrorView
                error={shownError.error}
                retry={shownError.retry}
                path={shownError.path}
                onDismiss={() => useUi.getState().setError("hero", undefined)}
              />
            ) : failedJob?.error ? (
              <ErrorView
                error={failedJob.error}
                retry={() => void actions.retryJob(failedJob)}
                path={failedJob.targetPath}
                onDismiss={() => void actions.dismissJob(failedJob.id)}
              />
            ) : null}
          </div>
        )}

        {game.saveStatus === "conflict" && (
          <div className="g-rise" style={{ ["--g-i" as string]: 2 }}>
            <SaveConflictCallout appName={appName} />
          </div>
        )}

        <div className="g-rise mt-6 flex items-center gap-0.5" style={{ ["--g-i" as string]: 2 }}>
          <PrimaryAction
            appName={appName}
            hero={hero}
            runningSince={session?.since ?? null}
            pending={starting}
            moment={moment}
            joined={game.installStatus === "installed" || game.installStatus === "broken"}
            onPress={press}
          />
          <MoreMenu game={game} primary={hero.emphasis === "primary"} />
        </div>

        <div className="g-rise" style={{ ["--g-i" as string]: 3 }}>
          <Facts game={game} />
        </div>
      </div>
    </div>
  );
}
