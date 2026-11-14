import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Dialog } from "radix-ui";
import {
  ArrowDownToLine,
  CornerDownLeft,
  Download,
  FolderInput,
  FolderOpen,
  Gamepad2,
  House,
  LogOut,
  Moon,
  Play,
  RefreshCw,
  Search,
  Settings as SettingsIcon,
  Store,
  Trash2,
  Wrench,
} from "lucide-react";
import { actions } from "../state/actions";
import { isBusy } from "../state/derive";
import { useGame } from "../state/library";
import { useLibraryIndex } from "../state/libraryIndex";
import { goGame, useRoute } from "../state/router";
import { jobForGame, useApp } from "../state/store";
import { useUi, type SettingsSection } from "../state/ui";
import { Kbd, cx } from "../ui";

interface Command {
  id: string;
  group: "Games" | "Game" | "Go to" | "App";
  label: string;
  icon: ReactNode;
  keywords?: string;
  shortcut?: string;
  disabled?: boolean;
  run: () => void;
}

const SETTINGS_SECTIONS: [SettingsSection, string][] = [
  ["general", "General"],
  ["downloads", "Downloads"],
  ["library", "Library"],
  ["egl", "Epic Games Launcher"],
  ["advanced", "Advanced and logs"],
  ["about", "About and licenses"],
];

/** Subsequence match with a bonus for prefixes; enough for ~20 commands without a dependency. */
function score(query: string, text: string): number {
  const q = query.toLowerCase();
  const t = text.toLowerCase();
  if (!q) return 1;
  if (t.startsWith(q)) return 100 - t.length;
  const at = t.indexOf(q);
  if (at >= 0) return 60 - at;
  let i = 0;
  for (const ch of t) if (ch === q[i]) i++;
  return i === q.length ? 10 : 0;
}

function PaletteBody({ close }: { close: () => void }) {
  const go = useUi((s) => s.go);
  const openDialog = useUi((s) => s.openDialog);
  const snap = useApp((s) => s.snapshot);
  const route = useRoute();
  const currentGame = useGame(route.page === "game" ? route.appName : undefined);
  const index = useLibraryIndex();
  const [query, setQuery] = useState("");
  const [cursor, setCursor] = useState(0);
  const list = useRef<HTMLDivElement>(null);

  const commands = useMemo<Command[]>(() => {
    if (!snap) return [];
    const signedIn = snap.auth.status === "signedIn";
    const cmds: Command[] = [];

    if (currentGame) {
      const g = currentGame;
      const busy = isBusy(snap, g.appName);
      const installed = g.installStatus === "installed" || g.installStatus === "broken";
      const job = jobForGame(snap.queue.jobs, g.appName);
      if (job?.status === "paused") cmds.push({ id: "primary", group: "Game", label: `Resume ${g.title}`, icon: <Play size={15} />, keywords: "continue", run: () => void actions.resumeJob(job.id) });
      else if (installed && g.updateAvailable) cmds.push({ id: "primary", group: "Game", label: `Update ${g.title}`, icon: <ArrowDownToLine size={15} />, keywords: "launch start", run: () => void actions.update(g.appName) });
      else if (installed) cmds.push({ id: "primary", group: "Game", label: `Play ${g.title}`, icon: <Play size={15} />, keywords: "launch start", run: () => void actions.launch(g.appName) });
      else if (g.installStatus === "notInstalled") cmds.push({ id: "primary", group: "Game", label: `Install ${g.title}`, icon: <Download size={15} />, keywords: "download", run: () => openDialog({ kind: "install", appName: g.appName }) });
      if (installed) {
        cmds.push(
          { id: "verify", group: "Game", label: `Verify and repair ${g.title}`, icon: <Wrench size={15} />, keywords: "check integrity", disabled: busy || !signedIn, run: () => void actions.verify(g.appName) },
          { id: "move", group: "Game", label: `Move ${g.title}…`, icon: <FolderInput size={15} />, disabled: busy, run: () => openDialog({ kind: "move", appName: g.appName }) },
          { id: "folder", group: "Game", label: `Open ${g.title}'s install folder`, icon: <FolderOpen size={15} />, run: () => void actions.openInstallFolder(g.appName) },
          { id: "uninstall", group: "Game", label: `Uninstall ${g.title}…`, icon: <Trash2 size={15} />, disabled: busy, run: () => openDialog({ kind: "uninstall", appName: g.appName }) },
        );
      }
    }
    cmds.push(
      { id: "check", group: "App", label: "Check all games for updates", icon: <RefreshCw size={15} />, disabled: !signedIn, run: () => void actions.checkForUpdates() },
      { id: "updateAll", group: "App", label: "Update all games", icon: <ArrowDownToLine size={15} />, disabled: !signedIn || snap.library.updatesAvailable === 0, run: () => void actions.updateAll() },
      { id: "go-home", group: "Go to", label: "Home", icon: <House size={15} />, shortcut: "Ctrl+1", run: () => go("home") },
      { id: "go-library", group: "Go to", label: "Library", icon: <Gamepad2 size={15} />, shortcut: "Ctrl+2", run: () => go("library") },
      { id: "go-downloads", group: "Go to", label: "Downloads", icon: <Download size={15} />, shortcut: "Ctrl+3", run: () => go("downloads") },
      { id: "go-store", group: "Go to", label: "Store", icon: <Store size={15} />, shortcut: "Ctrl+4", run: () => go("store") },
      { id: "go-settings", group: "Go to", label: "Settings", icon: <SettingsIcon size={15} />, shortcut: "Ctrl+5", run: () => go("settings") },
      ...SETTINGS_SECTIONS.map<Command>(([id, label]) => ({
        id: `settings-${id}`,
        group: "Go to",
        label: `Settings: ${label}`,
        icon: <SettingsIcon size={15} />,
        keywords: label,
        run: () => go("settings", id),
      })),
      {
        id: "theme",
        group: "App",
        label: "Toggle light and dark theme",
        icon: <Moon size={15} />,
        keywords: "appearance",
        run: () => {
          useUi.getState().touchTheme();
          const cur = document.documentElement.dataset.theme;
          void actions.updateSettings({ theme: cur === "light" ? "dark" : "light" });
        },
      },
      { id: "rescan", group: "App", label: "Re-scan for Epic Games Launcher installs", icon: <RefreshCw size={15} />, run: () => void actions.rescanEgl() },
      { id: "scanDrives", group: "App", label: "Scan all drives for existing installs", icon: <RefreshCw size={15} />, run: () => void actions.rescanEgl(true) },
      { id: "logs", group: "App", label: "Copy logs for a bug report", icon: <Wrench size={15} />, run: () => void actions.copyLogs() },
    );
    if (signedIn) cmds.push({ id: "signout", group: "App", label: "Sign out", icon: <LogOut size={15} />, run: () => void actions.signOut() });
    return cmds;
  }, [snap, currentGame, go, openDialog]);

  // Typing also searches the library (through the in-memory index): "Open <game>" jumps to its page.
  const games = useMemo<Command[]>(() => {
    if (!query.trim()) return [];
    return index
      .query({ search: query })
      .slice(0, 8)
      .map((g) => ({ id: `game-${g.appName}`, group: "Games" as const, label: g.title, icon: <Gamepad2 size={15} />, run: () => goGame(g.appName) }));
  }, [index, query]);

  const results = useMemo(
    () => [
      ...games,
      ...commands
        .map((c) => ({ c, s: score(query, `${c.label} ${c.keywords ?? ""}`) }))
        .filter((r) => r.s > 0)
        .sort((a, b) => (query ? b.s - a.s : 0))
        .map((r) => r.c),
    ],
    [commands, games, query],
  );

  useEffect(() => {
    list.current?.querySelector<HTMLElement>(`[data-index="${cursor}"]`)?.scrollIntoView({ block: "nearest" });
  }, [cursor]);

  const run = (c: Command | undefined) => {
    if (!c || c.disabled) return;
    close();
    c.run();
  };

  return (
    <>
          <div className="flex items-center gap-3 border-b border-border px-4">
            <Search size={16} className="shrink-0 text-fg-subtle" />
            <input
              autoFocus
              role="combobox"
              aria-expanded
              aria-controls="palette-list"
              aria-activedescendant={results[cursor] ? `cmd-${results[cursor]!.id}` : undefined}
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setCursor(0);
              }}
              onKeyDown={(e) => {
                if (e.key === "ArrowDown") {
                  e.preventDefault();
                  setCursor((i) => Math.min(results.length - 1, i + 1));
                } else if (e.key === "ArrowUp") {
                  e.preventDefault();
                  setCursor((i) => Math.max(0, i - 1));
                } else if (e.key === "Enter") {
                  e.preventDefault();
                  run(results[cursor]);
                }
              }}
              placeholder="Type a command or search…"
              className="h-12 min-w-0 flex-1 bg-transparent text-base text-fg outline-none placeholder:text-fg-subtle"
            />
            <Kbd>Esc</Kbd>
          </div>
          <div ref={list} id="palette-list" role="listbox" aria-label="Commands" className="max-h-[340px] overflow-y-auto p-1.5">
            {results.length === 0 && <p className="px-3 py-8 text-center text-sm text-fg-muted">No commands match “{query}”.</p>}
            {results.map((c, i) => {
              const showGroup = !query && (i === 0 || results[i - 1]!.group !== c.group);
              return (
                <div key={c.id}>
                  {showGroup && <p className="px-3 pb-1 pt-2.5 text-xs text-fg-subtle">{c.group}</p>}
                  <div
                    id={`cmd-${c.id}`}
                    role="option"
                    data-index={i}
                    aria-selected={i === cursor}
                    aria-disabled={c.disabled || undefined}
                    onMouseMove={() => setCursor(i)}
                    onClick={() => run(c)}
                    className={cx(
                      "flex h-9 items-center gap-3 rounded-lg px-3 text-sm",
                      c.disabled ? "opacity-45" : "text-fg",
                      i === cursor && "bg-surface-hover",
                    )}
                  >
                    <span className="text-fg-muted">{c.icon}</span>
                    <span className="min-w-0 flex-1 truncate">{c.label}</span>
                    {c.shortcut ? <Kbd>{c.shortcut}</Kbd> : i === cursor && !c.disabled ? <CornerDownLeft size={13} className="text-fg-subtle" /> : null}
                  </div>
                </div>
              );
            })}
          </div>
    </>
  );
}

export function CommandPalette() {
  const open = useUi((s) => s.palette);
  const setOpen = useUi((s) => s.setPalette);
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        {/* No enter/exit animation: this opens hundreds of times a day, speed is the feature. */}
        <Dialog.Overlay className="fixed inset-0 z-40 bg-overlay" />
        <Dialog.Content
          aria-label="Command palette"
          aria-describedby={undefined}
          className="fixed left-1/2 top-[14vh] z-50 w-[560px] max-w-[calc(100vw-32px)] -translate-x-1/2 overflow-hidden rounded-xl border border-border-strong bg-popover shadow-lg outline-none"
        >
          <Dialog.Title className="sr-only">Command palette</Dialog.Title>
          <PaletteBody close={() => setOpen(false)} />
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
