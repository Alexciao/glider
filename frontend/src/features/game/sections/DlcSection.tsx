import { memo, useState } from "react";
import type { GameDetail, GameSummary } from "../../../ipc/contract.gen";
import { actions, pendingKey } from "../../../state/actions";
import { isBusy } from "../../../state/derive";
import { formatBytes } from "../../../state/format";
import { useLibrary } from "../../../state/library";
import { useApp } from "../../../state/store";
import { usePending } from "../../../state/ui";
import { Button } from "../../../ui";
import { Block } from "./parts";

const FOLD_AT = 3;

type DlcState = "installed" | "installable" | "included";

const stateOf = (d: GameSummary): DlcState => (d.installStatus === "installed" ? "installed" : d.installStatus === "notInstallable" ? "included" : "installable");

const Row = memo(function Row({ dlc, state, canChange, working, onToggle }: { dlc: GameSummary; state: DlcState; canChange: boolean; working: boolean; onToggle: (on: boolean) => void }) {
  const thumb = dlc.art.tallThumb ?? dlc.art.tall;
  const size = state === "included" ? null : (dlc.installSizeBytes ?? null);
  const note = state === "included" ? "Included" : state === "installed" ? "Installed" : null;
  return (
    <li className="flex items-center gap-3 rounded-lg px-2 py-1.5 transition-colors duration-150 hover:bg-surface-hover">
      <span
        aria-hidden
        className="size-9 shrink-0 overflow-hidden rounded-md bg-surface-3 bg-cover bg-center"
        style={{ backgroundColor: dlc.art.placeholder ?? undefined, backgroundImage: thumb ? `url("${thumb}")` : undefined }}
      />
      <div className="min-w-0 flex-1">
        <p className="truncate text-sm font-medium text-fg" title={dlc.title}>
          {dlc.title}
        </p>
        <p className="truncate text-xs text-fg-muted tabular-nums">{[note, size ? formatBytes(size) : null].filter(Boolean).join(" · ") || " "}</p>
      </div>
      {canChange && state !== "included" && (
        <Button
          size="sm"
          variant={state === "installed" ? "ghost" : "secondary"}
          loading={working}
          onClick={() => onToggle(state !== "installed")}
          aria-label={`${state === "installed" ? "Uninstall" : "Install"} ${dlc.title}`}
        >
          {state === "installed" ? "Uninstall" : "Install"}
        </Button>
      )}
    </li>
  );
});

/** Only for games that have DLC. Per-row actions only where they can run; nothing explains why they can't. */
export function DlcSection({ game, detail }: { game: GameSummary; detail: GameDetail }) {
  const games = useLibrary((s) => s.games);
  const busy = useApp((s) => (s.snapshot ? isBusy(s.snapshot, game.appName) : false));
  const working = usePending(pendingKey("modify", game.appName));
  const [all, setAll] = useState(false);
  const items = detail.dlc.map((a) => games.get(a)).filter((d): d is GameSummary => !!d);
  if (items.length === 0) return null;

  const canChange = game.installStatus === "installed" && !busy;
  const states = new Map(items.map((d) => [d.appName, stateOf(d)] as const));
  const toggle = (appName: string, on: boolean) => {
    const dlc = items.filter((d) => (d.appName === appName ? on : states.get(d.appName) === "installed")).map((d) => d.appName);
    const components = detail.components.filter((c) => c.selected || c.required).map((c) => c.id);
    void actions.modifyInstall(game.appName, components, dlc);
  };
  const visible = all ? items : items.slice(0, FOLD_AT);
  return (
    <Block id="dlc-h" title="DLC" meta={items.length}>
      <ul className="-mx-2 flex flex-col">
        {visible.map((d) => (
          <Row key={d.appName} dlc={d} state={states.get(d.appName)!} canChange={canChange} working={working} onToggle={(on) => toggle(d.appName, on)} />
        ))}
      </ul>
      {items.length > FOLD_AT && (
        <button type="button" aria-expanded={all} onClick={() => setAll((a) => !a)} className="mt-1 rounded-sm text-sm font-medium text-accent-text transition-opacity duration-150 hover:opacity-80">
          {all ? "Show fewer" : `Show all ${items.length}`}
        </button>
      )}
    </Block>
  );
}
