import { ArrowDownToLine } from "lucide-react";
import { actions } from "../../state/actions";
import { useLibraryCounts, useRecentlyPlayed } from "../../state/libraryIndex";
import { useSnap } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Button, Surface } from "../../ui";
import { PageHeader, PageScroll } from "../common/Page";
import { GameRow } from "../library/GameRow";

/** Temporary Home: where to pick up (recently played) and what needs attention. The real Home comes later. */
export function Home() {
  const name = useSnap((s) => s.auth.account?.displayName);
  const recent = useRecentlyPlayed(6);
  const counts = useLibraryCounts();
  const go = useUi((s) => s.go);
  const updating = usePending("updateAll");

  return (
    <PageScroll>
      <PageHeader title={name ? `Welcome back, ${name}` : "Home"} description="Pick up where you left off." />
      {counts.updates > 0 && (
        <Surface className="mb-6 flex items-center gap-3 p-4">
          <span className="grid size-9 shrink-0 place-items-center rounded-lg bg-accent-soft text-accent-text">
            <ArrowDownToLine size={17} />
          </span>
          <p className="min-w-0 flex-1 text-sm text-fg">
            {counts.updates} {counts.updates === 1 ? "game has" : "games have"} an update waiting.
          </p>
          <Button variant="secondary" size="sm" loading={updating} onClick={() => void actions.updateAll()}>
            Update all
          </Button>
        </Surface>
      )}
      <section aria-label="Recently played">
        <div className="mb-2 flex items-center justify-between px-1">
          <h2 className="text-sm font-semibold text-fg">Continue playing</h2>
          <Button size="sm" variant="ghost" onClick={() => go("library")}>
            See library
          </Button>
        </div>
        {recent.length === 0 ? (
          <p className="px-1 text-sm text-fg-muted">Games you play appear here.</p>
        ) : (
          <ul className="flex flex-col">
            {recent.map((g) => (
              <GameRow key={g.appName} game={g} />
            ))}
          </ul>
        )}
      </section>
    </PageScroll>
  );
}
