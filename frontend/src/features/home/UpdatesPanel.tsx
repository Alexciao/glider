import type { GameSummary } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { showInLibrary } from "../../state/libraryPrefs";
import { goGame } from "../../state/router";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, GameCover } from "../../ui";
import { runUpdate } from "../library/status";
import { HomeSection } from "./HomeParts";

const SHOWN = 3;

/** Installed games with an update waiting: three rows (cover, title, Update) and one button for all of them. */
export function UpdatesPanel({ updates }: { updates: readonly GameSummary[] }) {
  const updatingAll = usePending("updateAll");
  const offline = useSnap((s) => s.connectivity === "offline");
  const shown = updates.slice(0, SHOWN);
  const more = updates.length - shown.length;
  return (
    <HomeSection
      title="Updates"
      action={
        <Button size="sm" variant="secondary" loading={updatingAll} disabled={offline} onClick={() => void actions.updateAll()}>
          Update all
        </Button>
      }
    >
      <ul>
        {shown.map((g) => (
          <li key={g.appName} className="flex items-center gap-3 py-1.5">
            <button
              type="button"
              onClick={() => goGame(g.appName)}
              className="flex min-w-0 flex-1 items-center gap-3 rounded-md text-left outline-none focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-focus"
            >
              <GameCover art={g.art} title={g.title} size="thumb" className="h-10 w-[30px] shrink-0 rounded-sm" />
              <span className="truncate text-sm font-medium text-fg">{g.title}</span>
            </button>
            <Button size="sm" variant="ghost" aria-label={`Update ${g.title}`} disabled={offline || g.activeJobId !== null} onClick={() => void runUpdate(g.appName)}>
              Update
            </Button>
          </li>
        ))}
      </ul>
      {more > 0 && (
        <Button size="sm" variant="ghost" className="-ml-2.5 mt-1" onClick={() => showInLibrary({ filter: "updates" })}>
          {more} more
        </Button>
      )}
    </HomeSection>
  );
}
