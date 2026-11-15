import { useMemo } from "react";
import { failedJobsOf } from "../../state/failures";
import { useLibrary } from "../../state/library";
import { useLibraryCounts, useLibraryIndex, useRecentlyPlayed } from "../../state/libraryIndex";
import { goGame } from "../../state/router";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { GameCover, Skeleton } from "../../ui";
import { PageScroll } from "../common/Page";
import { LibraryEmpty } from "../common/LibraryEmpty";
import { SignInBanner } from "../common/SignInBanner";
import { ContinueCard } from "./ContinuePlaying";
import { DownloadsStrip } from "./DownloadsStrip";
import { FreeThisWeek } from "./FreeThisWeek";
import { HomeSection } from "./HomeParts";
import { UpdatesPanel } from "./UpdatesPanel";

/** Three cards, a fourth where the frame has room for a fourth column (`@[1180px]`). */
const CARDS = 4;

function HomeSkeleton() {
  return (
    <div role="status" aria-label="Loading Home">
      <Skeleton className="mb-3 h-5 w-36 rounded-sm" />
      <div className="mb-10 grid grid-cols-3 gap-5 @[1180px]:grid-cols-4 [&>li:nth-child(4)]:hidden @[1180px]:[&>li:nth-child(4)]:block">
        {[0, 1, 2, 3].map((i) => (
          <div key={i}>
            <Skeleton className="aspect-video rounded-xl" />
            <Skeleton className="mt-3 h-4 w-[58%] rounded-sm" />
          </div>
        ))}
      </div>
    </div>
  );
}

/** A brand-new library with nothing installed: your newest games, to pick a first one. */
function FirstGame() {
  const index = useLibraryIndex();
  const picks = useMemo(() => index.query({ filter: "notInstalled", sort: "added" }).filter((g) => g.installStatus === "notInstalled").slice(0, 6), [index]);
  return (
    <HomeSection title="Install your first game">
      <ul className="grid grid-cols-6 gap-4">
        {picks.map((g) => (
          <li key={g.appName} className="min-w-0">
            <button
              type="button"
              onClick={() => goGame(g.appName)}
              className="group/pick block w-full rounded-lg text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
            >
              <span className="block rounded-lg transition-transform duration-150 ease-out group-hover/pick:-translate-y-[3px]">
                <GameCover art={g.art} title={g.title} size="thumb" className="rounded-lg shadow-[inset_0_0_0_1px_var(--g-border-strong)]" />
              </span>
              <span className="mt-2 block truncate text-sm font-medium text-fg">{g.title}</span>
            </button>
          </li>
        ))}
      </ul>
    </HomeSection>
  );
}

/** Home: four possible sections (continue, downloads, updates, free games); an empty one is not drawn. */
export function Home() {
  const loaded = useLibrary((s) => s.loaded);
  const syncing = useSnap((s) => s.library.syncing);
  const counts = useLibraryCounts();
  const index = useLibraryIndex();
  const recent = useRecentlyPlayed(CARDS);
  const refreshing = usePending("refreshLibrary");

  const total = counts.all + counts.hidden;
  const signedIn = useSnap((s) => s.auth.status === "signedIn");
  const jobs = useSnap((s) => s.queue.jobs);
  // Updates that are not already waiting in "Couldn't finish": those have their own "Try again" on Downloads, and "Update all" skips them.
  const offers = useMemo(() => {
    const failedUpdates = new Set(failedJobsOf(jobs).filter((j) => j.kind === "update").map((j) => j.appName));
    return index.updates().filter((g) => !failedUpdates.has(g.appName));
  }, [index, jobs]);
  // Installed but never played: show what is ready instead of an empty row.
  const ready = useMemo(
    () => (recent.length > 0 ? [] : index.query({ filter: "installed", sort: "added" }).filter((g) => g.installStatus === "installed").slice(0, CARDS)),
    [index, recent.length],
  );
  const cards = recent.length > 0 ? recent : ready;

  if (!loaded || (total === 0 && (syncing || refreshing))) {
    return (
      <PageScroll full>
        <HomeSkeleton />
      </PageScroll>
    );
  }

  return (
    <PageScroll full>
      {/* No greeting: it said nothing and pushed the games down. The first section leads; the page keeps an h1 for screen readers. */}
      <h1 className="sr-only">Home</h1>
      {total > 0 && <SignInBanner className="mb-8" />}

      {total === 0 ? (
        <div className="mb-10">
          <LibraryEmpty className="py-8" />
        </div>
      ) : cards.length > 0 ? (
        <HomeSection title={recent.length > 0 ? "Continue playing" : "Ready to play"}>
          <ul className="grid grid-cols-3 gap-5 @[1180px]:grid-cols-4 [&>li:nth-child(4)]:hidden @[1180px]:[&>li:nth-child(4)]:block">
            {cards.map((g) => (
              <ContinueCard key={g.appName} game={g} />
            ))}
          </ul>
        </HomeSection>
      ) : (
        <FirstGame />
      )}

      {/* Rows read across, so they keep a reading width at the frame's left edge; the grids around them use the whole frame. */}
      <div className="max-w-[960px]">
        <DownloadsStrip />
        {offers.length > 0 && signedIn && <UpdatesPanel updates={offers} />}
      </div>
      <FreeThisWeek />
    </PageScroll>
  );
}
