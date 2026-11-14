import { useEffect, useState } from "react";
import { RadioGroup } from "radix-ui";
import type { UninstallMode } from "../../../ipc/contract.gen";
import { actions, pendingKey } from "../../../state/actions";
import { formatBytes } from "../../../state/format";
import { useGame } from "../../../state/library";
import { usePending, useUi } from "../../../state/ui";
import { AlertModal, Button, cx } from "../../../ui";
import { ErrorView } from "../../common/ErrorView";
import { PathText } from "../../common/PathText";

const card =
  "flex w-full items-start gap-3 rounded-xl border border-border bg-surface-1 p-3.5 text-left outline-none transition-[background-color,border-color] duration-150 " +
  "hover:bg-surface-2 focus-visible:ring-2 focus-visible:ring-focus";

function Dot({ checked, danger }: { checked: boolean; danger?: boolean }) {
  return (
    <span
      aria-hidden
      className={cx(
        "mt-0.5 grid size-4 shrink-0 place-items-center rounded-full border transition-colors duration-150",
        checked ? (danger ? "border-danger bg-danger" : "border-accent bg-accent") : "border-border-strong",
      )}
    >
      <span className={cx("size-1.5 rounded-full transition-transform duration-150", danger ? "bg-danger-fg" : "bg-accent-fg", checked ? "scale-100" : "scale-0")} />
    </span>
  );
}

/**
 * Uninstall: keep the files or delete them. The folder named in the dialog is the real install folder from Core, and
 * a game that Epic Games Launcher shares defaults to the safe choice.
 */
export function UninstallDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const game = useGame(appName);
  const error = useUi((s) => s.errors.uninstall);
  const working = usePending(pendingKey("uninstall", appName));
  const shared = game?.sharedWithEgl ?? false;
  const [mode, setMode] = useState<UninstallMode>(shared ? "gliderOnly" : "deleteFiles");

  useEffect(() => () => useUi.getState().setError("uninstall", undefined), []);
  if (!game || !game.installPath) return null;

  const destructive = mode === "deleteFiles";
  const size = formatBytes(game.installedSizeBytes ?? game.installSizeBytes ?? 0);
  return (
    <AlertModal
      open={open}
      onOpenChange={(o) => !o && !working && close()}
      size="md"
      title={`Uninstall ${game.title}?`}
      cancel={
        <Button variant="ghost" disabled={working}>
          Cancel
        </Button>
      }
      action={
        <Button
          variant={destructive ? "danger" : "primary"}
          loading={working}
          onClick={(e) => {
            e.preventDefault();
            void actions.uninstall(appName, mode);
          }}
        >
          {destructive ? "Uninstall and delete files" : "Uninstall, keep files"}
        </Button>
      }
    >
      <RadioGroup.Root value={mode} onValueChange={(v) => setMode(v as UninstallMode)} aria-label="What to uninstall" className="flex flex-col gap-2">
        <RadioGroup.Item value="gliderOnly" className={cx(card, "data-[state=checked]:border-accent/60 data-[state=checked]:bg-accent-soft")}>
          <Dot checked={mode === "gliderOnly"} />
          <span className="min-w-0">
            <span className="block text-sm font-medium text-fg">
              Keep the files
            </span>
            <span className="mt-0.5 block text-xs leading-[1.5] text-fg-muted">{shared ? "Glider forgets it. Epic Games Launcher keeps working." : "Glider forgets it. The files stay put."}</span>
          </span>
        </RadioGroup.Item>
        <RadioGroup.Item value="deleteFiles" className={cx(card, "data-[state=checked]:border-danger/60 data-[state=checked]:bg-danger-soft")}>
          <Dot checked={mode === "deleteFiles"} danger />
          <span className="min-w-0 flex-1">
            <span className="block text-sm font-medium text-fg">Delete the files</span>
            <span className="mt-0.5 block text-xs leading-[1.5] text-fg-muted">
              Frees {size}{shared ? ", and Epic Games Launcher loses the game too" : ""}.
            </span>
            <PathText path={game.installPath} className="mt-1 text-xs text-fg" tailSegments={2} />
          </span>
        </RadioGroup.Item>
      </RadioGroup.Root>
      {error && <ErrorView className="mt-3" error={error.error} retry={error.retry} />}
    </AlertModal>
  );
}
