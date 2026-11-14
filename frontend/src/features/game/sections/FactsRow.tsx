import type { GameDetail, GameSummary } from "../../../ipc/contract.gen";
import { actions } from "../../../state/actions";
import { formatBytes, formatPlaytime, formatRelativeTime, prettyVersion } from "../../../state/format";
import { PathText } from "../../common/PathText";
import { formatDateTime } from "../format";
import { Fact } from "./parts";

const ANTI_CHEAT = { easyAntiCheat: "Easy Anti-Cheat", battlEye: "BattlEye" } as const;

/**
 * Everything about the install, in one row of small label-over-value pairs. Only the facts that exist show; a fact
 * the status line already states (the update's download size) is not repeated here.
 */
export function FactsRow({ game, detail }: { game: GameSummary; detail: GameDetail | null }) {
  const installed = game.installStatus === "installed" || game.installStatus === "broken";
  const ac = detail && detail.antiCheat !== "none" ? ANTI_CHEAT[detail.antiCheat] : null;
  const items: React.ReactNode[] = [];

  if (installed) {
    items.push(
      <Fact key="v" label="Version">
        <span className="tabular-nums">{prettyVersion(game.installedVersion)}</span>
      </Fact>,
      <Fact key="s" label="Size">
        {game.installedSizeBytes == null ? <span className="font-normal text-fg-muted">Measuring…</span> : <span className="tabular-nums">{formatBytes(game.installedSizeBytes)}</span>}
      </Fact>,
    );
    if (game.lastPlayedAt) {
      items.push(
        <Fact key="lp" label="Last played">
          <span title={formatDateTime(game.lastPlayedAt) ?? undefined}>{formatRelativeTime(game.lastPlayedAt)}</span>
        </Fact>,
      );
    }
    if (game.playtimeSeconds > 0) {
      items.push(
        <Fact key="pt" label="Playtime" hint="Only counts time played in Glider.">
          <span className="tabular-nums">{formatPlaytime(game.playtimeSeconds)}</span>
        </Fact>,
      );
    }
    if (ac) items.push(<Fact key="ac" label="Anti-cheat">{ac}</Fact>);
    if (game.installPath) {
      items.push(
        <Fact key="loc" label="Location" grow>
          <button type="button" title="Open folder" onClick={() => void actions.openInstallFolder(game.appName)} className="block w-full min-w-0 rounded-sm text-left transition-colors duration-150 hover:text-accent-text">
            <PathText path={game.installPath} tailSegments={1} />
          </button>
        </Fact>,
      );
    }
  } else {
    if (game.installSizeBytes) {
      items.push(
        <Fact key="size" label="On disk">
          <span className="tabular-nums">{formatBytes(game.installSizeBytes)}</span>
        </Fact>,
      );
    }
    if (game.latestVersion) {
      items.push(
        <Fact key="v" label="Latest version">
          <span className="tabular-nums">{prettyVersion(game.latestVersion)}</span>
        </Fact>,
      );
    }
    if (game.playtimeSeconds > 0) {
      items.push(
        <Fact key="pt" label="Playtime" hint="Only counts time played in Glider.">
          <span className="tabular-nums">{formatPlaytime(game.playtimeSeconds)}</span>
        </Fact>,
      );
    }
    if (ac) items.push(<Fact key="ac" label="Anti-cheat">{ac}</Fact>);
  }
  if (items.length === 0) return null;
  return <dl className="flex flex-wrap items-start gap-x-10 gap-y-3">{items}</dl>;
}
