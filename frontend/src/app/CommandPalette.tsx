import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Dialog } from "radix-ui";
import {
  ArrowDownToLine,
  CornerDownLeft,
  Download,
  FolderInput,
  ExternalLink,
  FolderOpen,
  Gamepad2,
  LogOut,
  Moon,
  Play,
  RefreshCw,
  Search,
  Settings as SettingsIcon,
  Trash2,
  Wrench,
} from "lucide-react";
import { useHero } from "../state/bridge";
import { actions } from "../state/actions";
import { isBusy } from "../state/derive";
import { useApp } from "../state/store";
import { useUi, type SettingsSection } from "../state/ui";
import { Kbd, cx } from "../ui";

interface Command {
  id: string;
  group: "Game" | "Go to" | "App";
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
  ["game", "Game"],
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
  const hero = useHero();
  const [query, setQuery] = useState("");
  const [index, setIndex] = useState(0);
  const list = useRef<HTMLDivElement>(null);

  const commands = useMemo<Command[]>(() => {
    if (!snap) return [];
    const busy = isBusy(snap);
    const installed = snap.game.installed !== null;
    const signedIn = snap.auth.status === "signedIn";
    const cmds: Command[] = [];

    const primary: Record<string, Pick<Command, "label" | "icon" | "run"> | undefined> = {
      play: { label: "Play Fortnite", icon: <Play size={15} />, run: () => void actions.launch() },
      update: { label: "Update Fortnite", icon: <ArrowDownToLine size={15} />, run: () => void actions.update() },
      install: { label: "Install Fortnite", icon: <Download size={15} />, run: () => openDialog({ kind: "install" }) },
      resume: { label: "Resume download", icon: <Play size={15} />, run: () => hero.job && void actions.resumeJob(hero.job.id) },
      signIn: { label: "Sign in with Epic", icon: <Play size={15} />, run: () => void actions.signIn("hero") },
      repair: { label: "Repair Fortnite", icon: <Wrench size={15} />, run: () => void actions.verify() },
    };
    const p = primary[hero.kind];
    if (p) cmds.push({ id: "primary", group: "Game", keywords: "launch start", ...p });
    if (installed) {
      cmds.push(
        { id: "verify", group: "Game", label: "Verify and repair files", icon: <Wrench size={15} />, keywords: "check integrity", disabled: busy || !signedIn, run: () => void actions.verify() },
        { id: "check", group: "Game", label: "Check for updates", icon: <RefreshCw size={15} />, disabled: busy || !signedIn, run: () => void actions.checkForUpdates() },
        { id: "move", group: "Game", label: "Move install…", icon: <FolderInput size={15} />, disabled: busy, run: () => openDialog({ kind: "move" }) },
        { id: "folder", group: "Game", label: "Open install folder", icon: <FolderOpen size={15} />, run: () => void actions.openInstallFolder() },
        { id: "uninstall", group: "Game", label: "Uninstall…", icon: <Trash2 size={15} />, disabled: busy, run: () => openDialog({ kind: "uninstall" }) },
      );
    }
    cmds.push(
      { id: "go-library", group: "Go to", label: "Library", icon: <Gamepad2 size={15} />, shortcut: "Ctrl+1", run: () => go("library") },
      { id: "go-downloads", group: "Go to", label: "Downloads", icon: <Download size={15} />, shortcut: "Ctrl+2", run: () => go("downloads") },
      { id: "go-settings", group: "Go to", label: "Settings", icon: <SettingsIcon size={15} />, shortcut: "Ctrl+3", run: () => go("settings") },
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
      { id: "rescan", group: "App", label: "Re-scan for existing installs", icon: <RefreshCw size={15} />, run: () => void actions.rescanEgl() },
      { id: "scanDrives", group: "App", label: "Scan all drives for an existing install", icon: <RefreshCw size={15} />, run: () => void actions.rescanEgl(true) },
      { id: "logs", group: "App", label: "Copy logs for a bug report", icon: <Wrench size={15} />, run: () => void actions.copyLogs() },
    );
    if (snap.auth.status !== "signedIn" && snap.auth.status !== "signingIn") {
      cmds.push({ id: "browserSignIn", group: "App", label: "Sign in with your browser instead", icon: <ExternalLink size={15} />, keywords: "epic captcha login code", run: () => openDialog({ kind: "browserSignIn" }) });
    }
    if (signedIn) cmds.push({ id: "signout", group: "App", label: "Sign out", icon: <LogOut size={15} />, run: () => void actions.signOut() });
    return cmds;
  }, [snap, hero.kind, hero.job, go, openDialog]);

  const results = useMemo(
    () =>
      commands
        .map((c) => ({ c, s: score(query, `${c.label} ${c.keywords ?? ""}`) }))
        .filter((r) => r.s > 0)
        .sort((a, b) => (query ? b.s - a.s : 0))
        .map((r) => r.c),
    [commands, query],
  );

  useEffect(() => {
    list.current?.querySelector<HTMLElement>(`[data-index="${index}"]`)?.scrollIntoView({ block: "nearest" });
  }, [index]);

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
              aria-activedescendant={results[index] ? `cmd-${results[index]!.id}` : undefined}
              value={query}
              onChange={(e) => {
                setQuery(e.target.value);
                setIndex(0);
              }}
              onKeyDown={(e) => {
                if (e.key === "ArrowDown") {
                  e.preventDefault();
                  setIndex((i) => Math.min(results.length - 1, i + 1));
                } else if (e.key === "ArrowUp") {
                  e.preventDefault();
                  setIndex((i) => Math.max(0, i - 1));
                } else if (e.key === "Enter") {
                  e.preventDefault();
                  run(results[index]);
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
                    aria-selected={i === index}
                    aria-disabled={c.disabled || undefined}
                    onMouseMove={() => setIndex(i)}
                    onClick={() => run(c)}
                    className={cx(
                      "flex h-9 items-center gap-3 rounded-lg px-3 text-sm",
                      c.disabled ? "opacity-45" : "text-fg",
                      i === index && "bg-surface-hover",
                    )}
                  >
                    <span className="text-fg-muted">{c.icon}</span>
                    <span className="min-w-0 flex-1 truncate">{c.label}</span>
                    {c.shortcut ? <Kbd>{c.shortcut}</Kbd> : i === index && !c.disabled ? <CornerDownLeft size={13} className="text-fg-subtle" /> : null}
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
