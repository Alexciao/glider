import { useEffect, useReducer, useSyncExternalStore } from "react";
import { FlaskConical } from "lucide-react";
import { mockController } from "../ipc/rpc";
import { STRESS_MODES, getStressMode, setStressMode, subscribeStress } from "./stress";
import type { ErrorCode } from "../ipc/contract.gen";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  cx,
} from "../ui";

const ERRORS: [ErrorCode, string][] = [
  ["network", "Network lost (job fails)"],
  ["cdnUnavailable", "CDN unavailable"],
  ["insufficientSpace", "Out of disk space"],
  ["filesLocked", "Files locked"],
  ["corrupted", "Corrupted files"],
  ["legendaryFailed", "Task failed"],
  ["offline", "Went offline"],
];

const NEXT_CALL_ERRORS: [ErrorCode, string][] = [
  ["authExpired", "Session expired"],
  ["launchFailed", "Launch fails"],
  ["gameRunning", "Game is running"],
  ["gameBusy", "Game is busy"],
  ["activationRequired", "Needs activation"],
  ["saveConflict", "Saves conflict"],
  ["eglBusy", "Epic Games Launcher busy"],
  ["invalidFolder", "Invalid folder"],
  ["permissionDenied", "Access denied"],
];

/** Plain on purpose: this is chrome, not part of the design under test. Bottom-centre, instant. */
function DataToggle() {
  const mode = useSyncExternalStore(subscribeStress, getStressMode);
  return (
    <div role="radiogroup" aria-label="Data set" className="fixed bottom-3 left-1/2 z-[70] flex -translate-x-1/2 gap-0.5 rounded-full bg-neutral-500/40 p-0.5 font-[system-ui] text-[11px] backdrop-blur">
      {STRESS_MODES.map((m) => (
        <button
          key={m.value}
          type="button"
          role="radio"
          aria-checked={mode === m.value}
          onClick={() => setStressMode(m.value)}
          className={mode === m.value ? "rounded-full bg-white px-2.5 py-1 font-medium text-black" : "rounded-full px-2.5 py-1 text-white/80 hover:text-white"}
        >
          {m.label}
        </button>
      ))}
    </div>
  );
}

/** Dev-only (mock backend): jump between scenarios, inject failures and stress the data without reloading. */
export function DevSwitcher() {
  const controller = mockController();
  const [, bump] = useReducer((n: number) => n + 1, 0);
  useEffect(() => controller?.subscribe(bump), [controller]);
  if (!controller) return null;

  return (
    <>
      <DataToggle />
    <div className="fixed bottom-3 left-3 z-[70]">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <button
            type="button"
            aria-label="Mock scenario"
            className="flex h-7 items-center gap-1.5 rounded-full border border-border-strong bg-popover px-2.5 text-2xs font-medium text-fg-muted shadow-md transition-colors duration-150 hover:text-fg"
          >
            <FlaskConical size={12} />
            {controller.getScenario()}
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" side="top" className="max-h-[70vh] w-72 overflow-y-auto">
          <DropdownMenuLabel>Scenario (mock backend)</DropdownMenuLabel>
          {controller.scenarios.map((s) => (
            <DropdownMenuItem
              key={s.name}
              className={cx(s.name === controller.getScenario() && "bg-surface-2")}
              onSelect={() => controller.loadScenario(s.name)}
            >
              <span className="block">
                <span className="block">{s.label}</span>
                <span className="block text-2xs text-fg-subtle">{s.description}</span>
              </span>
            </DropdownMenuItem>
          ))}
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Fail the running job</DropdownMenuLabel>
          {ERRORS.map(([code, label]) => (
            <DropdownMenuItem key={code} onSelect={() => controller.failActiveJob(code)}>
              {label}
            </DropdownMenuItem>
          ))}
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Fail the next action</DropdownMenuLabel>
          {NEXT_CALL_ERRORS.map(([code, label]) => (
            <DropdownMenuItem key={code} onSelect={() => controller.failNext(code)}>
              {label}
            </DropdownMenuItem>
          ))}
          <DropdownMenuSeparator />
          <DropdownMenuLabel>Simulate</DropdownMenuLabel>
          <DropdownMenuItem onSelect={() => controller.endGame()}>End the running game</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.setEglBusy(true)}>Epic Games Launcher: busy</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.setEglBusy(false)}>Epic Games Launcher: not busy</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.setConnectivity("offline")}>Go offline</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.setConnectivity("online")}>Go online</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.dropNextLibraryDelta()}>Drop the next library delta (gap)</DropdownMenuItem>
          <DropdownMenuItem onSelect={() => controller.emitNotice({ level: "info", title: "Update found", message: "An update is ready.", action: null, appName: null, jobId: null })}>
            Emit a notice
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
    </>
  );
}
