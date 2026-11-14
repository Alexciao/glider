import { useMemo } from "react";
import { Gamepad2 } from "lucide-react";
import { actions } from "../../state/actions";
import { useLibrary } from "../../state/library";
import { useLibraryCounts, useLibraryIndex, useRecentlyPlayed } from "../../state/libraryIndex";
import { goGame } from "../../state/router";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, EmptyState, GameCover, Skeleton } from "../../ui";
import { PageScroll } from "../common/Page";
import { ContinueCard } from "./ContinuePlaying";
import { DownloadsStrip } from "./DownloadsStrip";
import { FreeThisWeek } from "./FreeThisWeek";
import { HomeSection } from "./HomeParts";
import { UpdatesPanel } from "./UpdatesPanel";

const CARDS = 3;

function HomeSkeleton() {
  return (
    <div role="status" aria-label="Loading Home">
      <Skeleton className="mb-10 h-7 w-64 rounded-md" />
      <Skeleton className="mb-3 h-5 w-36 rounded-sm" />
      <div className="mb-10 grid grid-cols-3 gap-5">
        {[0, 1, 2].map((i) => (
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
                <GameCover art={g.art} title={g.title} className="rounded-lg shadow-[inset_0_0_0_1px_var(--g-border-strong)]" />
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
  const name = useSnap((s) => s.auth.account?.displayName);
  const loaded = useLibrary((s) => s.loaded);
  const syncing = useSnap((s) => s.library.syncing);
  const counts = useLibraryCounts();
  const index = useLibraryIndex();
  const recent = useRecentlyPlayed(CARDS);
  const refreshing = usePending("refreshLibrary");

  const total = counts.all + counts.hidden;
  const updates = useMemo(() => index.updates(), [index]);
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
      <h1 className="mb-10 text-xl font-semibold tracking-tight text-fg">{name ? `Welcome back, ${name}` : "Home"}</h1>

      {total === 0 ? (
        <div className="mb-10">
          <EmptyState
            className="py-8"
            icon={<Gamepad2 size={20} />}
            title="Your library is empty"
            action={
              <Button variant="primary" onClick={() => void actions.refreshLibrary()}>
                Sync library
              </Button>
            }
          />
        </div>
      ) : cards.length > 0 ? (
        <HomeSection title={recent.length > 0 ? "Continue playing" : "Ready to play"}>
          <ul className="grid grid-cols-3 gap-5">
            {cards.map((g) => (
              <ContinueCard key={g.appName} game={g} />
            ))}
          </ul>
        </HomeSection>
      ) : (
        <FirstGame />
      )}

      <DownloadsStrip />
      {updates.length > 0 && <UpdatesPanel updates={updates} />}
      <FreeThisWeek />
    </PageScroll>
  );
}
