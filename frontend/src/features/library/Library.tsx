import type { AppSnapshot } from "../../ipc/contract.gen";
import { useHero } from "../../state/bridge";
import { actions } from "../../state/actions";
import { historyJobs, useApp } from "../../state/store";
import { usableCandidates } from "../../state/derive";
import { usePending, useUi } from "../../state/ui";
import { ErrorView } from "../common/ErrorView";
import { HeroArt } from "./HeroArt";
import { PrimaryAction } from "./PrimaryAction";
import { StatusLine } from "./StatusLine";
import { ExistingInstallHint, Facts, JobLine, MoreMenu } from "./LibraryParts";

export function Library() {
  const snap = useApp((s) => s.snapshot as AppSnapshot);
  const hero = useHero();
  const heroError = useUi((s) => s.errors.hero);
  const go = useUi((s) => s.go);
  const openDialog = useUi((s) => s.openDialog);
  const adopting = usePending("adopt");
  const pInstall = usePending("install");
  const pUpdate = usePending("update");
  const pVerify = usePending("verify");
  const pLaunch = usePending("launch");
  const pSignIn = usePending("signIn");
  const starting = pInstall || pUpdate || pVerify || pLaunch || pSignIn;
  // The ready flourish is store-owned and self-expiring; it only shows while the game is playable.
  const readyAt = useUi((s) => s.readyAt);
  const moment = readyAt !== null && hero.kind === "play";

  const { game } = snap;
  const failedJob = historyJobs(snap.jobs).find((j) => j.status === "failed" && j.error);
  const candidate = hero.kind === "install" ? usableCandidates(snap.egl)[0] : undefined;
  const job = hero.job;
  const showJobLine = job && (hero.kind === "working" || hero.kind === "resume");

  const press = () => {
    switch (hero.target) {
      case "signIn":
        void actions.signIn("hero");
        break;
      case "install":
        openDialog({ kind: "install" });
        break;
      case "update":
        void actions.update();
        break;
      case "launch":
        void actions.launch();
        break;
      case "repair":
        void actions.verify();
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

        {(heroError || failedJob?.error) && (
          <div className="g-rise mt-5 w-[min(560px,100%)]" style={{ ["--g-i" as string]: 1 }}>
            {heroError ? (
              <ErrorView
                error={heroError.error}
                retry={heroError.retry}
                path={heroError.path}
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

        <div className="g-rise mt-6 flex items-center gap-0.5" style={{ ["--g-i" as string]: 2 }}>
          <PrimaryAction hero={hero} runningSince={game.runningSince} pending={starting} moment={moment} joined={game.installed !== null} onPress={press} />
          <MoreMenu snap={snap} primary={hero.emphasis === "primary"} />
        </div>

        {candidate && (
          <div className="g-rise" style={{ ["--g-i" as string]: 3 }}>
            <ExistingInstallHint candidate={candidate} pending={adopting} />
          </div>
        )}

        <div className="g-rise" style={{ ["--g-i" as string]: 3 }}>
          <Facts snap={snap} />
        </div>
      </div>
    </div>
  );
}
