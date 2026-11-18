import type { GameDetail, GameSummary } from "../../../ipc/contract.gen";
import { actions } from "../../../state/actions";
import { formatBytes, formatPlaytime, formatRelativeTime, prettyVersion } from "../../../state/format";
import { PathText } from "../../common/PathText";
import { formatTimeNear, formatWhen } from "../format";
import { DetailRows, Fact } from "./parts";

const ANTI_CHEAT = { easyAntiCheat: "Easy Anti-Cheat", battlEye: "BattlEye" } as const;

/** Where the playtime was played; only the parts above zero, never none. Absent until the game's detail has loaded. */
function playtimeDetail(detail: GameDetail | null) {
  const p = detail?.playtime;
  if (!p) return undefined;
  const rows = [
    { label: "Epic Games Launcher", value: formatPlaytime(p.epicSeconds ?? 0), seconds: p.epicSeconds ?? 0 },
    { label: "Glider", value: formatPlaytime(p.gliderSeconds), seconds: p.gliderSeconds },
  ].filter((r) => r.seconds > 0);
  if (rows.length === 0) rows.push({ label: "Glider", value: formatPlaytime(0), seconds: 0 });
  return { label: "Playtime details", content: <DetailRows rows={rows} /> };
}

/** The exact moment the game was last opened; when that was not recorded, honestly the last time it was seen running. */
function lastPlayedDetail(game: GameSummary, detail: GameDetail | null) {
  if (!detail) return undefined;
  const started = detail.playtime?.lastStartedAt;
  if (!started) {
    const at = formatWhen(game.lastPlayedAt);
    return at ? { label: "Last played details", content: <DetailRows rows={[{ label: "Last closed", value: at }]} /> } : undefined;
  }
  const rows = [{ label: "Opened", value: formatWhen(started) ?? "" }];
  const closed = game.runState === "idle" && game.lastPlayedAt && Date.parse(game.lastPlayedAt) >= Date.parse(started) ? formatTimeNear(game.lastPlayedAt, started) : null;
  if (closed) rows.push({ label: "Closed", value: closed });
  return { label: "Last played details", content: <DetailRows rows={rows} /> };
}

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
        <Fact key="lp" label="Last played" detail={lastPlayedDetail(game, detail)}>
          <span>{formatRelativeTime(game.lastPlayedAt)}</span>
        </Fact>,
      );
    }
    if (game.playtimeSeconds > 0) {
      items.push(
        <Fact key="pt" label="Playtime" detail={playtimeDetail(detail)}>
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
        <Fact key="size" label="Install size">
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
        <Fact key="pt" label="Playtime" detail={playtimeDetail(detail)}>
          <span className="tabular-nums">{formatPlaytime(game.playtimeSeconds)}</span>
        </Fact>,
      );
    }
    if (ac) items.push(<Fact key="ac" label="Anti-cheat">{ac}</Fact>);
  }
  if (items.length === 0) return null;
  return <dl className="flex flex-wrap items-start gap-x-10 gap-y-3">{items}</dl>;
}
