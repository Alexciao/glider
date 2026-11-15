import type { ReactNode } from "react";
import {
  CircleArrowUp,
  CircleDot,
  Download,
  FolderInput,
  FileText,
  FolderOpen,
  Gamepad2,
  Gauge,
  House,
  LogOut,
  Moon,
  Pause,
  Play,
  RefreshCw,
  Rocket,
  Settings as SettingsIcon,
  Store,
  Sun,
  ShieldCheck,
  Trash2,
  Wrench,
} from "lucide-react";
import type { AppSnapshot, DownloadMode, GameSummary, JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { isBusy } from "../../state/derive";
import { DOWNLOAD_MODES } from "../../state/downloadModes";
import { fuzzyMatchAny, type FuzzyMatch } from "../../state/fuzzy";
import { openInstall } from "../../state/installPlan";
import { goGame } from "../../state/router";
import { activeJobs, jobForGame } from "../../state/store";
import { useUi, type DialogState } from "../../state/ui";
import { SECTIONS } from "../../features/settings/sections";

/**
 * What the command palette can do, as plain data: items (a game, a page, a Settings section, an app action), the
 * primary and secondary actions of a game, and the ranking. The view (CommandPalette.tsx) only renders and routes keys.
 */

export type ItemGroup = "recent" | "suggested" | "games" | "actions" | "pages" | "settings" | "game";

export const GROUP_LABEL: Record<ItemGroup, string> = {
  recent: "Recent",
  suggested: "Suggested",
  games: "Games",
  actions: "Actions",
  pages: "Go to",
  settings: "Settings",
  game: "This game",
};

export interface ItemAction {
  /** Short verb shown on the chip: "Play", "Install", "Open". */
  label: string;
  /** Key that triggers it: "Enter" is the primary; the others are modifier chords. */
  keys: string;
  run: () => void;
}

export interface PaletteItem {
  /** Stable across renders and sessions ("game:Fortnite", "cmd:check"): recents are stored by it. */
  id: string;
  group: ItemGroup;
  label: string;
  /** Muted text after the label (a game's status, "Settings"). */
  hint?: string;
  icon?: ReactNode;
  /** Portrait cover for a game. */
  art?: { src: string | null; title: string; placeholder: string | null };
  /** Extra words the fuzzy matcher accepts. */
  keywords?: string;
  shortcut?: string;
  /** Why it can't run right now; the row shows it and Enter does nothing. */
  disabled?: string;
  /** Primary first, then the secondary actions. A command has exactly one. */
  actions: ItemAction[];
  /** Set on "Update Fortnite"-style commands: the game whose primary action this repeats if that game has a row of its own. */
  forGame?: string;
}

export interface PaletteContext {
  snap: AppSnapshot;
  /** The game whose page is open, if any. */
  currentGame?: GameSummary;
  theme: "dark" | "light";
  go: (page: "home" | "library" | "downloads" | "store" | "settings", section?: (typeof SECTIONS)[number]["id"]) => void;
  openDialog: (d: DialogState) => void;
}

const sz = 15;
const open = (appName: string): ItemAction => ({ label: "Open", keys: "Ctrl+Enter", run: () => goGame(appName) });

/* ------------------------------------------------------------------ games */

/** One short line about a game's state, for the row. */
export function gameStatus(g: GameSummary, job: JobInfo | undefined): string {
  if (g.runState === "running") return "Playing";
  if (g.runState !== "idle") return "Starting";
  if (job) {
    const pct = job.progress ? ` ${Math.round(job.progress.percent)}%` : "";
    switch (job.status) {
      case "paused":
        return `Paused${pct}`;
      case "waiting":
      case "queued":
        return "Queued";
      default:
        return `${job.kind === "update" ? "Updating" : job.kind === "install" ? "Installing" : job.kind === "move" ? "Moving" : job.kind === "import" ? "Importing" : "Working"}${pct}`;
    }
  }
  switch (g.installStatus) {
    case "installed":
      return g.updateAvailable ? "Update available" : "Installed";
    case "broken":
      return "Needs repair";
    case "notInstallable":
      return g.thirdPartyName ?? "Included";
    case "notInstalled":
      return "Not installed";
  }
}

/**
 * What Enter does for a game, and the other things worth a chord. Enter never opens a dead end: when the natural action
 * is blocked (offline, signed out, busy) Enter opens the game's page, where the reason is explained.
 */
export function gameActions(g: GameSummary, ctx: PaletteContext): ItemAction[] {
  const { snap } = ctx;
  const online = snap.connectivity !== "offline";
  const signedIn = snap.auth.status === "signedIn";
  const job = jobForGame(snap.queue.jobs, g.appName);
  const busy = isBusy(snap, g.appName);
  const installed = g.installStatus === "installed";
  const playable = installed || g.installStatus === "broken";
  const canDownload = online && signedIn;
  const page = open(g.appName);
  const openPrimary: ItemAction = { label: "Open", keys: "Enter", run: page.run };

  if (g.runState !== "idle") return [openPrimary];
  if (job?.status === "paused") return [{ label: "Resume", keys: "Enter", run: () => void actions.resumeJob(job.id) }, page];
  if (job) return [openPrimary];
  if (g.installStatus === "notInstallable") return [openPrimary];
  if (g.installStatus === "notInstalled") {
    return canDownload ? [{ label: "Install", keys: "Enter", run: () => void openInstall(g.appName) }, page] : [openPrimary];
  }
  if (g.installStatus === "broken") return canDownload && !busy ? [{ label: "Repair", keys: "Enter", run: () => void actions.verify(g.appName) }, page] : [openPrimary];
  if (installed && g.updateAvailable && canDownload) {
    return [
      { label: "Update", keys: "Enter", run: () => void actions.update(g.appName) },
      { label: "Play", keys: "Shift+Enter", run: () => void actions.launch(g.appName) },
      page,
    ];
  }
  if (playable) return [{ label: "Play", keys: "Enter", run: () => void actions.launch(g.appName) }, page];
  return [openPrimary];
}

export function gameItem(g: GameSummary, ctx: PaletteContext, group: ItemGroup = "games"): PaletteItem {
  const job = jobForGame(ctx.snap.queue.jobs, g.appName);
  return {
    id: `game:${g.appName}`,
    group,
    label: g.title,
    hint: gameStatus(g, job),
    art: { src: g.art.tallThumb ?? g.art.tall, title: g.title, placeholder: g.art.placeholder },
    keywords: g.developer ?? undefined,
    actions: gameActions(g, ctx),
  };
}

/* ------------------------------------------------------------------ commands */

const cmd = (id: string, group: ItemGroup, label: string, icon: ReactNode, run: () => void, extra: Partial<PaletteItem> = {}): PaletteItem => ({
  id: `cmd:${id}`,
  group,
  label,
  icon,
  actions: [{ label: "Run", keys: "Enter", run }],
  ...extra,
});

/** Everything that is not a game. Which ones exist (and which are disabled) follows the live snapshot. */
export function buildCommands(ctx: PaletteContext): PaletteItem[] {
  const { snap, currentGame: g } = ctx;
  const signedIn = snap.auth.status === "signedIn";
  const offline = snap.connectivity === "offline";
  const online = signedIn && !offline;
  const items: PaletteItem[] = [];

  if (g) {
    const busy = isBusy(snap, g.appName);
    const installed = g.installStatus === "installed" || g.installStatus === "broken";
    const primary = gameActions(g, ctx)[0];
    if (primary && primary.label !== "Open") {
      items.push({
        id: `cmd:game-primary`,
        forGame: g.appName,
        group: "game",
        label: `${primary.label} ${g.title}`,
        icon: primary.label === "Play" ? <Play size={sz} /> : primary.label === "Install" ? <Download size={sz} /> : primary.label === "Resume" ? <Play size={sz} /> : primary.label === "Repair" ? <Wrench size={sz} /> : <CircleArrowUp size={sz} />,
        keywords: "launch start download",
        actions: [{ label: primary.label, keys: "Enter", run: primary.run }],
      });
    }
    if (installed) {
      items.push(
        cmd("game-verify", "game", `${g.installStatus === "broken" ? "Repair" : "Verify"} ${g.title}`, g.installStatus === "broken" ? <Wrench size={sz} /> : <ShieldCheck size={sz} />, () => void actions.verify(g.appName), {
          keywords: "check integrity files repair verify",
          disabled: busy ? "Busy right now" : !online ? (offline ? "You're offline" : "Sign in first") : undefined,
        }),
        cmd("game-move", "game", `Move ${g.title}…`, <FolderInput size={sz} />, () => ctx.openDialog({ kind: "move", appName: g.appName }), { disabled: busy ? "Busy right now" : undefined }),
        cmd("game-folder", "game", `Open ${g.title}'s install folder`, <FolderOpen size={sz} />, () => void actions.openInstallFolder(g.appName), { keywords: "explorer files" }),
        cmd("game-uninstall", "game", `Uninstall ${g.title}…`, <Trash2 size={sz} />, () => ctx.openDialog({ kind: "uninstall", appName: g.appName }), { disabled: busy ? "Busy right now" : undefined, keywords: "remove delete" }),
      );
    }
  }

  const unfinished = activeJobs(snap.queue.jobs);
  const running = unfinished.some((j) => j.status === "running" || j.status === "queued" || j.status === "waiting");
  const updates = snap.library.updatesAvailable;
  items.push(
    snap.queue.paused
      ? cmd("resume-all", "actions", "Resume all downloads", <Play size={sz} />, () => void actions.resumeQueue(), { keywords: "queue continue" })
      : cmd("pause-all", "actions", "Pause all downloads", <Pause size={sz} />, () => void actions.pauseQueue(), { keywords: "queue stop", disabled: running ? undefined : "No downloads running" }),
    cmd("check-updates", "actions", "Check for updates", <RefreshCw size={sz} />, () => void actions.checkForUpdates(), { keywords: "refresh library games", disabled: online ? undefined : offline ? "You're offline" : "Sign in first" }),
    cmd("update-all", "actions", "Update all games", <CircleArrowUp size={sz} />, () => void actions.updateAll(), {
      keywords: "install updates",
      disabled: !online ? (offline ? "You're offline" : "Sign in first") : updates === 0 ? "Everything is up to date" : undefined,
    }),
    ...DOWNLOAD_MODES.map((m) =>
      cmd(
        `mode-${m.value}`,
        "actions",
        `Download mode: ${m.label}`,
        <Gauge size={sz} />,
        () => void actions.updateSettings({ downloadMode: m.value as DownloadMode }),
        { keywords: "speed priority switch change", hint: snap.settings.downloadMode === m.value ? "Current" : undefined },
      ),
    ),
    cmd("theme", "actions", ctx.theme === "dark" ? "Switch to light theme" : "Switch to dark theme", ctx.theme === "dark" ? <Sun size={sz} /> : <Moon size={sz} />, () => {
      useUi.getState().touchTheme();
      void actions.updateSettings({ theme: ctx.theme === "dark" ? "light" : "dark" });
    }, { keywords: "appearance mode color" }),
    cmd("rescan", "actions", "Rescan for installed games", <RefreshCw size={sz} />, () => void actions.rescanEgl(), { keywords: "epic games launcher egl import find" }),
    cmd("import", "actions", "Import games from Epic Games Launcher…", <Rocket size={sz} />, () => ctx.go("settings", "egl"), { keywords: "bulk existing installs link" }),
    cmd("setup", "actions", "Run setup again", <CircleDot size={sz} />, () => void actions.restartOnboarding(), { keywords: "onboarding welcome first run wizard" }),
    cmd("logs", "actions", "Copy logs for a bug report", <FileText size={sz} />, () => void actions.copyLogs(), { keywords: "diagnostics support" }),
  );
  if (signedIn) items.push(cmd("sign-out", "actions", "Sign out", <LogOut size={sz} />, () => void actions.signOut(), { keywords: "account logout" }));

  items.push(
    cmd("go-home", "pages", "Home", <House size={sz} />, () => ctx.go("home"), { shortcut: "Ctrl+1", keywords: "go open" }),
    cmd("go-library", "pages", "Library", <Gamepad2 size={sz} />, () => ctx.go("library"), { shortcut: "Ctrl+2", keywords: "go open games" }),
    cmd("go-downloads", "pages", "Downloads", <Download size={sz} />, () => ctx.go("downloads"), { shortcut: "Ctrl+3", keywords: "go open queue" }),
    cmd("go-store", "pages", "Store", <Store size={sz} />, () => ctx.go("store"), { shortcut: "Ctrl+4", keywords: "go open epic games store shop buy free games" }),
    cmd("go-settings", "pages", "Settings", <SettingsIcon size={sz} />, () => ctx.go("settings"), { shortcut: "Ctrl+5", keywords: "go open preferences options" }),
    ...SECTIONS.map((s) => cmd(`settings-${s.id}`, "settings", s.label, s.icon, () => ctx.go("settings", s.id), { hint: "Settings", keywords: `settings preferences ${s.keywords}` })),
  );
  return items;
}

/* ------------------------------------------------------------------ ranking */

export interface Ranked {
  item: PaletteItem;
  match: FuzzyMatch;
}

/** Fuzzy-match items against the query; best first. An empty query keeps the given order. */
export function rankItems(query: string, items: readonly PaletteItem[]): Ranked[] {
  const out: Ranked[] = [];
  for (const item of items) {
    const match = fuzzyMatchAny(query, item.label, item.keywords, item.hint);
    if (match) out.push({ item, match });
  }
  if (query.trim()) out.sort((a, b) => b.match.score - a.match.score);
  return out;
}

export interface Section {
  group: ItemGroup;
  rows: Ranked[];
}

const MAX_PER_GROUP: Partial<Record<ItemGroup, number>> = { games: 6, actions: 5, pages: 5, settings: 5, game: 5 };

/**
 * Typing: results grouped by kind, groups ordered by their best match, each group capped.
 * Empty query: recents, then what is useful right now, then everything else.
 */
/**
 * "Update Fortnite" and the row "Fortnite · Update ↵" are one action twice. When a game has a row of its own whose
 * Enter does that, its command twin ("Suggested" and "This game" alike) is dropped: the row says it once.
 */
export function withoutTwins(rowsShown: readonly PaletteItem[], ...lists: PaletteItem[][]): PaletteItem[][] {
  const covered = new Set(
    rowsShown.filter((i) => i.id.startsWith("game:") && i.actions[0] && i.actions[0].label !== "Open").map((i) => `${i.id.slice(5)}|${i.actions[0]!.label}`),
  );
  return lists.map((list) => list.filter((c) => !(c.forGame && covered.has(`${c.forGame}|${c.actions[0]?.label}`))));
}

export function buildSections(query: string, gamesIn: PaletteItem[], commandsIn: PaletteItem[], recentsIn: PaletteItem[], suggestedIn: PaletteItem[]): Section[] {
  const games = gamesIn;
  const [commands = [], recents = [], suggested = []] = withoutTwins([...gamesIn, ...recentsIn, ...suggestedIn], commandsIn, recentsIn, suggestedIn);
  const q = query.trim();
  if (!q) {
    const taken = new Set([...recents, ...suggested].map((i) => i.id));
    const rest = (g: ItemGroup) => commands.filter((c) => c.group === g && !taken.has(c.id));
    const wrap = (items: PaletteItem[]): Ranked[] => items.map((item) => ({ item, match: { score: 0, ranges: [] } }));
    return [
      { group: "recent" as const, rows: wrap(recents) },
      { group: "suggested" as const, rows: wrap(suggested) },
      { group: "game" as const, rows: wrap(rest("game")) },
      { group: "pages" as const, rows: wrap(rest("pages")) },
      { group: "actions" as const, rows: wrap(rest("actions")) },
      { group: "settings" as const, rows: wrap(rest("settings")) },
    ].filter((s) => s.rows.length > 0);
  }
  const ranked = rankItems(q, [...games, ...commands]);
  const by = new Map<ItemGroup, Ranked[]>();
  for (const r of ranked) {
    const list = by.get(r.item.group) ?? [];
    if (list.length < (MAX_PER_GROUP[r.item.group] ?? 5)) list.push(r);
    by.set(r.item.group, list);
  }
  return [...by.entries()].map(([group, rows]) => ({ group, rows })).sort((a, b) => b.rows[0]!.match.score - a.rows[0]!.match.score);
}

