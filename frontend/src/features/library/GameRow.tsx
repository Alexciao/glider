import { memo } from "react";
import { Star } from "lucide-react";
import type { GameSummary } from "../../ipc/contract.gen";
import { JOB_VERB } from "../../state/derive";
import { goGame } from "../../state/router";
import { Badge, cx, type BadgeTone } from "../../ui";

/** The one-line status a list row shows for a game. */
export function rowStatus(g: GameSummary): { tone: BadgeTone; text: string } {
  if (g.runState !== "idle") return { tone: "success", text: g.runState === "running" ? "Running" : "Starting" };
  if (g.activeJobKind) return { tone: "accent", text: JOB_VERB[g.activeJobKind] };
  switch (g.installStatus) {
    case "installed":
      return g.updateAvailable ? { tone: "accent", text: "Update available" } : { tone: "success", text: "Installed" };
    case "broken":
      return { tone: "danger", text: "Needs repair" };
    case "notInstallable":
      return { tone: "neutral", text: g.thirdPartyName ?? "Included" };
    case "notInstalled":
      return { tone: "neutral", text: "Not installed" };
  }
}

/**
 * A plain list row (art thumbnail, title, status) that opens the game's page. Temporary: the real grid and cards come
 * with the library redesign; this exists so every game is reachable and the routing and stores are exercised.
 */
export const GameRow = memo(function GameRow({ game }: { game: GameSummary }) {
  const st = rowStatus(game);
  const thumb = game.art.tallThumb ?? game.art.tall;
  return (
    <li>
      <button
        type="button"
        onClick={() => goGame(game.appName)}
        className={cx(
          "flex w-full items-center gap-3 rounded-lg px-2.5 py-2 text-left transition-colors duration-150 hover:bg-surface-hover",
          game.hidden && "opacity-60",
        )}
      >
        <span
          aria-hidden
          className="size-11 shrink-0 overflow-hidden rounded-md bg-surface-3 bg-cover bg-center"
          style={{ backgroundColor: game.art.placeholder ?? undefined, backgroundImage: thumb ? `url("${thumb}")` : undefined }}
        />
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1.5 text-sm font-medium text-fg">
            <span className="truncate">{game.title}</span>
            {game.favorite && <Star size={12} aria-label="Favorite" className="shrink-0 fill-current text-warning-text" />}
          </span>
          <span className="block truncate text-xs text-fg-muted">
            {game.kind === "game" ? (game.developer ?? "Unknown developer") : game.kind === "dlc" ? "DLC" : "Add-on"}
            {game.dlcCount > 0 ? `  ·  ${game.dlcCount} DLC` : ""}
          </span>
        </span>
        <Badge tone={st.tone}>{st.text}</Badge>
      </button>
    </li>
  );
});
