import { useEffect, useId, useState, type ReactNode } from "react";
import { Cog, Download, FolderOpen, Gamepad2, Info, RefreshCw, ScrollText, ShieldCheck } from "lucide-react";
import type { AutoUpdatePolicy, DownloadMode, EglReadiness, LogLevel, Settings as SettingsT, ThemePreference, WhilePlayingPolicy } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { eglInstallStatus } from "../../state/derive";
import { formatBytes, formatRelativeTime, prettyVersion } from "../../state/format";
import { useSnap } from "../../state/store";
import { useUi, usePending, type SettingsSection } from "../../state/ui";
import {
  Badge,
  Button,
  NumberField,
  Row,
  Section,
  SegmentedControl,
  Select,
  Slider,
  Switch,
  cx,
} from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageHeader, PageScroll } from "../common/Page";
import { PathText } from "../common/PathText";
import { AboutPanel } from "./AboutPanel";
import { LogViewer } from "./LogViewer";

const SECTIONS: { id: SettingsSection; label: string; icon: ReactNode }[] = [
  { id: "general", label: "General", icon: <Cog size={16} /> },
  { id: "downloads", label: "Downloads", icon: <Download size={16} /> },
  { id: "library", label: "Library", icon: <Gamepad2 size={16} /> },
  { id: "egl", label: "Epic Games Launcher", icon: <ShieldCheck size={16} /> },
  { id: "advanced", label: "Advanced", icon: <ScrollText size={16} /> },
  { id: "about", label: "About", icon: <Info size={16} /> },
];

/* ------------------------------------------------------------ row helpers */

function SwitchRow({
  label,
  description,
  checked,
  onChange,
  disabled,
}: {
  label: string;
  description?: ReactNode;
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <Row
      label={label}
      description={description}
      htmlFor={id}
      control={<Switch id={id} checked={checked} onCheckedChange={onChange} disabled={disabled} />}
    />
  );
}

function AutoNumberRow({
  label,
  description,
  value,
  auto,
  min,
  max,
  step = 1,
  suffix,
  slider,
  onChange,
}: {
  label: string;
  description: ReactNode;
  value: number | null;
  auto: number;
  min: number;
  max: number;
  step?: number;
  suffix?: string;
  slider?: boolean;
  onChange: (v: number | null) => void;
}) {
  const custom = value !== null;
  const [draft, setDraft] = useState(value ?? auto);
  const [seen, setSeen] = useState(value ?? auto);
  if (seen !== (value ?? auto)) {
    setSeen(value ?? auto);
    setDraft(value ?? auto);
  }
  return (
    <Row
      label={label}
      description={description}
      control={
        <SegmentedControl
          aria-label={`${label} mode`}
          className="w-[288px]"
          value={custom ? "custom" : "auto"}
          onValueChange={(v) => onChange(v === "auto" ? null : (value ?? auto))}
          options={[
            { value: "auto", label: `Auto (${auto}${suffix ? ` ${suffix}` : ""})` },
            { value: "custom", label: "Custom" },
          ]}
        />
      }
    >
      {custom && (
        <div className="mt-3 flex items-center gap-4 rounded-lg bg-surface-2 px-3.5 py-2.5">
          {slider ? (
            <>
              <span className="w-5 text-xs tabular-nums text-fg-muted">{min}</span>
              <Slider aria-label={label} min={min} max={max} step={step} value={draft} onValueChange={setDraft} onValueCommit={(v) => onChange(v)} className="w-full flex-1" />
              <span className="w-5 text-right text-xs tabular-nums text-fg-muted">{max}</span>
              <span className="w-8 text-right text-sm font-medium tabular-nums text-fg">{draft}</span>
            </>
          ) : (
            <>
              <NumberField aria-label={label} value={value} min={min} max={max} step={step} suffix={suffix} onCommit={(v) => onChange(v)} />
              <span className="text-xs text-fg-subtle tabular-nums">
                {min}–{max} {suffix}
              </span>
            </>
          )}
        </div>
      )}
    </Row>
  );
}

function PathRow({ label, description, path, actions: rowActions, empty }: { label: string; description?: ReactNode; path: string | null; actions?: ReactNode; empty?: string }) {
  return (
    <Row
      label={label}
      description={description}
      control={rowActions}
    >
      <div className="mt-2 flex min-w-0 rounded-md bg-surface-2 px-3 py-2 text-sm">
        {path ? <PathText path={path} className="text-fg" tailSegments={3} /> : <span className="text-fg-muted">{empty ?? "Not set"}</span>}
      </div>
    </Row>
  );
}

/* ---------------------------------------------------------------- panels */

const CDNS = [
  { value: "auto", label: "Automatic", hint: "Recommended" },
  { value: "download.epicgames.com", label: "Epic", hint: "download.epicgames.com" },
  { value: "epicgames-download1.akamaized.net", label: "Akamai", hint: "akamaized.net" },
  { value: "fastly-download.epicgames.com", label: "Fastly", hint: "fastly-download" },
  { value: "cloudflare.epicgamescdn.com", label: "Cloudflare", hint: "epicgamescdn.com" },
] as const;

const SPEED_LIMITS = [
  { value: "none", label: "No limit" },
  { value: String(5_000_000), label: "5 MB/s" },
  { value: String(10_000_000), label: "10 MB/s" },
  { value: String(25_000_000), label: "25 MB/s" },
  { value: String(50_000_000), label: "50 MB/s" },
] as const;

const INTERVALS = [
  { value: "15", label: "Every 15 minutes" },
  { value: "30", label: "Every 30 minutes" },
  { value: "60", label: "Every hour" },
  { value: "120", label: "Every 2 hours" },
  { value: "360", label: "Every 6 hours" },
  { value: "720", label: "Every 12 hours" },
] as const;

function General({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  return (
    <div className="flex flex-col gap-7">
      <Section title="Startup">
        <SwitchRow label="Start with Windows" description="Open Glider when you sign in, so updates are ready before you play." checked={s.startWithWindows} onChange={(v) => void set({ startWithWindows: v })} />
        <SwitchRow label="Start minimized" description="Launch quietly in the tray instead of opening the window." checked={s.startMinimized} onChange={(v) => void set({ startMinimized: v })} />
      </Section>
      <Section title="Window">
        <SwitchRow label="Close to tray" description="Closing the window keeps Glider running so updates continue in the background." checked={s.closeToTray} onChange={(v) => void set({ closeToTray: v })} />
        <SwitchRow label="Minimize to tray while playing" description="Hide Glider from the taskbar while Fortnite is running." checked={s.minimizeToTrayWhilePlaying} onChange={(v) => void set({ minimizeToTrayWhilePlaying: v })} />
      </Section>
      <Section title="Notifications">
        <SwitchRow label="Show notifications" description="Tell me when an install or update finishes, or when something needs attention." checked={s.notifications} onChange={(v) => void set({ notifications: v })} />
      </Section>
      <Section title="Appearance">
        <Row
          label="Theme"
          description="System follows your Windows setting."
          control={
            <SegmentedControl<ThemePreference>
              aria-label="Theme"
              className="w-[240px]"
              value={s.theme}
              onValueChange={(v) => {
                useUi.getState().touchTheme();
                void set({ theme: v });
              }}
              options={[
                { value: "system", label: "System" },
                { value: "dark", label: "Dark" },
                { value: "light", label: "Light" },
              ]}
            />
          }
        />
      </Section>
    </div>
  );
}

function Downloads({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  const info = useSnap((x) => x.app);
  return (
    <div className="flex flex-col gap-7">
      <Section title="Performance" description="Glider tunes these for your PC. Change them only if downloads are slow or your PC struggles.">
        <AutoNumberRow
          label="Download workers"
          description="Parallel connections to Epic's servers. More can help on fast lines, at the cost of CPU."
          value={s.downloadWorkers}
          auto={info.autoWorkers}
          min={1}
          max={32}
          slider
          onChange={(v) => void set({ downloadWorkers: v })}
        />
        <AutoNumberRow
          label="Shared memory"
          description="Cache used while unpacking downloaded chunks. Larger values reduce disk work."
          value={s.sharedMemoryMb}
          auto={info.autoSharedMemoryMb}
          min={256}
          max={8192}
          step={256}
          suffix="MB"
          onChange={(v) => void set({ sharedMemoryMb: v })}
        />
        <Row
          label="Preferred CDN"
          description="Where downloads come from. Automatic picks the fastest for your region."
          control={
            <Select
              aria-label="Preferred CDN"
              minWidth={190}
              value={s.preferredCdn ?? "auto"}
              onValueChange={(v) => void set({ preferredCdn: v === "auto" ? null : v })}
              options={CDNS}
            />
          }
        />
      </Section>
      <Section title="Speed">
        <Row
          label="Download mode"
          description="Balanced keeps the network at full speed but yields the disk and CPU to your other programs."
          control={
            <SegmentedControl<DownloadMode>
              aria-label="Download mode"
              className="w-[300px]"
              value={s.downloadMode}
              onValueChange={(v) => void set({ downloadMode: v })}
              options={[
                { value: "background", label: "Background" },
                { value: "balanced", label: "Balanced" },
                { value: "full", label: "Full" },
              ]}
            />
          }
        />
        <Row
          label="Speed limit"
          description="Caps the whole download queue."
          control={
            <Select
              aria-label="Speed limit"
              minWidth={190}
              value={s.speedLimitBytesPerSecond === null ? "none" : String(s.speedLimitBytesPerSecond)}
              onValueChange={(v) => void set({ speedLimitBytesPerSecond: v === "none" ? null : Number(v) })}
              options={SPEED_LIMITS.some((l) => l.value === String(s.speedLimitBytesPerSecond)) || s.speedLimitBytesPerSecond === null ? SPEED_LIMITS : [...SPEED_LIMITS, { value: String(s.speedLimitBytesPerSecond), label: `${formatBytes(s.speedLimitBytesPerSecond)}/s` }]}
            />
          }
        />
        <SwitchRow label="Full speed when I'm away" description="Go to Full mode while the PC is idle and no game is running." checked={s.idleFullSpeed} onChange={(v) => void set({ idleFullSpeed: v })} />
        <Row
          label="While a game is running"
          control={
            <Select<WhilePlayingPolicy>
              aria-label="While a game is running"
              minWidth={190}
              value={s.whilePlaying}
              onValueChange={(v) => void set({ whilePlaying: v })}
              options={[
                { value: "pause", label: "Pause downloads" },
                { value: "throttle", label: "Slow downloads down" },
                { value: "continue", label: "Keep going" },
              ]}
            />
          }
        />
      </Section>
      <Section title="Updates">
        <Row
          label="Update automatically"
          description="Games can override this on their own page."
          control={
            <Select<Exclude<AutoUpdatePolicy, "default">>
              aria-label="Update automatically"
              minWidth={190}
              value={s.autoUpdate === "default" ? "always" : s.autoUpdate}
              onValueChange={(v) => void set({ autoUpdate: v })}
              options={[
                { value: "always", label: "Always" },
                { value: "ask", label: "Ask me first" },
                { value: "never", label: "Never" },
              ]}
            />
          }
        />
        <SwitchRow label="Only when I'm not playing" description="Wait until the game is closed before starting an automatic update." checked={s.autoUpdateOnlyWhenIdle} disabled={s.autoUpdate === "never"} onChange={(v) => void set({ autoUpdateOnlyWhenIdle: v })} />
        <Row
          label="Check for updates"
          control={
            <Select
              aria-label="Update check interval"
              minWidth={190}
              value={String(s.updateCheckIntervalMinutes)}
              onValueChange={(v) => void set({ updateCheckIntervalMinutes: Number(v) })}
              options={INTERVALS.some((i) => i.value === String(s.updateCheckIntervalMinutes)) ? INTERVALS : [...INTERVALS, { value: String(s.updateCheckIntervalMinutes), label: `Every ${s.updateCheckIntervalMinutes} minutes` }]}
            />
          }
        />
      </Section>
    </div>
  );
}

function LibrarySettings({ s }: { s: SettingsT }) {
  const folders = useSnap((x) => x.storage.folders);
  const overlay = useSnap((x) => x.overlay);
  const addFolder = async () => {
    const p = await actions.pickFolder(s.defaultInstallBase, "Choose a library folder");
    if (!p || s.libraryFolders.includes(p)) return;
    await actions.updateSettings({ libraryFolders: [...s.libraryFolders, p], defaultInstallBase: s.defaultInstallBase ?? p });
  };
  const remove = (path: string) => {
    const left = s.libraryFolders.filter((f) => f !== path);
    void actions.updateSettings({ libraryFolders: left, defaultInstallBase: s.defaultInstallBase === path ? (left[0] ?? null) : s.defaultInstallBase });
  };
  return (
    <div className="flex flex-col gap-7">
      <Section
        title="Library folders"
        description="Games install into the default folder unless you pick another one."
        action={
          <Button size="sm" variant="secondary" onClick={() => void addFolder()}>
            Add folder…
          </Button>
        }
      >
        {folders.length === 0 && <div className="px-4 py-5 text-sm text-fg-muted">No library folders yet.</div>}
        {folders.map((f) => (
          <div key={f.path} className="flex items-center gap-3 px-4 py-3">
            <div className="min-w-0 flex-1">
              <PathText path={f.path} className="text-sm font-medium text-fg" tailSegments={3} />
              <p className="mt-0.5 text-xs text-fg-muted tabular-nums">
                {formatBytes(f.freeBytes)} free on {f.drive} · {f.gameCount} {f.gameCount === 1 ? "game" : "games"}
                {!f.exists && " · folder missing"}
              </p>
            </div>
            {f.isDefault ? (
              <Badge tone="accent">Default</Badge>
            ) : (
              <Button size="sm" variant="ghost" onClick={() => void actions.updateSettings({ defaultInstallBase: f.path })}>
                Make default
              </Button>
            )}
            <Button size="sm" variant="ghost" icon={<FolderOpen size={13} />} onClick={() => void actions.openPath(f.path)}>
              Open
            </Button>
            <Button size="sm" variant="ghost" disabled={f.gameCount > 0} onClick={() => remove(f.path)}>
              Remove
            </Button>
          </div>
        ))}
      </Section>
      <Section title="Cloud saves">
        <SwitchRow label="Sync saves with Epic's cloud" description="Before and after you play. Games can opt out on their own page." checked={s.cloudSaves} onChange={(v) => void actions.updateSettings({ cloudSaves: v })} />
      </Section>
      <Section title="Epic overlay">
        <Row
          label="Epic Games overlay"
          description={overlay.installed ? `Version ${overlay.version ?? "unknown"}${overlay.updateAvailable ? `, ${overlay.latestVersion ?? "a newer version"} is available` : ""}.` : "Needed by a few games for friends and achievements."}
          control={
            overlay.installed ? (
              <Switch aria-label="Epic overlay" checked={overlay.enabled} onCheckedChange={(v) => void actions.setOverlayEnabled(v)} />
            ) : (
              <Button size="sm" variant="secondary" loading={overlay.busy} onClick={() => void actions.installOverlay()}>
                Install
              </Button>
            )
          }
        />
      </Section>
    </div>
  );
}

function Egl({ s }: { s: SettingsT }) {
  const egl = useSnap((x) => x.egl);
  const rescanning = usePending("rescan") || egl.scanning;
  const authed = useSnap((x) => x.auth.status === "signedIn");
  const importing = usePending("import");
  const [readiness, setReadiness] = useState<EglReadiness | null>(null);

  return (
    <div className="flex flex-col gap-7">
      <Section title="Status">
        <Row label="Epic Games Launcher" control={<Badge tone={egl.launcherInstalled ? "success" : "neutral"} dot>{egl.launcherInstalled ? "Detected" : "Not installed"}</Badge>} />
        <Row
          label="Launcher is running"
          description={egl.busyApps.length > 0 ? "It's installing or updating a game right now. Glider waits so the two don't collide." : undefined}
          control={<Badge tone={egl.busyApps.length > 0 ? "warning" : egl.launcherRunning ? "accent" : "neutral"} dot>{egl.busyApps.length > 0 ? "Busy with a game" : egl.launcherRunning ? "Running" : "Closed"}</Badge>}
        />
      </Section>

      <Section title="Sync">
        <SwitchRow
          label="Keep Epic Games Launcher in sync"
          description="When Glider updates or moves a game, it tells Epic Games Launcher the new version and location, so the launcher doesn't try to re-download or repair it. Glider backs up the launcher's files first and never writes while the launcher is busy with the game."
          checked={s.eglSync}
          onChange={(v) => void actions.updateSettings({ eglSync: v })}
        />
        <SwitchRow
          label="Open Epic links in Glider"
          description="Store buttons and old Epic Games Launcher shortcuts (com.epicgames.launcher://) open here."
          checked={s.handleEpicLinks}
          onChange={(v) => void actions.updateSettings({ handleEpicLinks: v })}
        />
      </Section>

      <Section
        title="Installs found"
        description={egl.lastScan ? `Last scanned ${formatRelativeTime(egl.lastScan)}` : "Looks for games installed by Epic Games Launcher."}
        action={
          <span className="flex gap-1">
            <Button size="sm" variant="secondary" loading={rescanning} icon={<RefreshCw size={13} />} onClick={() => void actions.rescanEgl()}>
              Re-scan
            </Button>
            <Button size="sm" variant="ghost" disabled={rescanning} onClick={() => void actions.rescanEgl(true)}>
              Scan all drives
            </Button>
          </span>
        }
      >
        {egl.installs.length === 0 ? (
          <div className="px-4 py-5 text-sm text-fg-muted">No installs found. Epic Games Launcher {egl.launcherInstalled ? "has nothing to import." : "isn't installed."}</div>
        ) : (
          egl.installs.map((i) => {
            const status = eglInstallStatus(i);
            return (
              <div key={i.id} className="flex items-center gap-4 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-fg">{i.title}</p>
                  <PathText path={i.installPath} className="text-xs text-fg-muted" tailSegments={3} />
                  <p className="mt-0.5 text-xs text-fg-muted tabular-nums">
                    {i.reason ?? `${i.version ? `Version ${prettyVersion(i.version)}` : "Version unknown"}${i.sizeBytes ? `, ${formatBytes(i.sizeBytes)}` : ""}`}
                  </p>
                </div>
                <Badge tone={status.tone}>{status.text}</Badge>
                {i.status === "importable" && authed && (
                  <Button size="sm" variant="secondary" loading={importing} onClick={() => void actions.importInstalls([i.id], "auto", "settings")}>
                    Import
                  </Button>
                )}
              </div>
            );
          })
        )}
      </Section>

      <Section title="Ready to uninstall Epic Games Launcher?">
        <Row
          label="Check what still depends on it"
          description={readiness ? (readiness.ready ? "Nothing blocks removing it." : "Some games still need it.") : undefined}
          control={
            <Button size="sm" variant="secondary" onClick={() => void actions.checkEglReadiness().then((r) => r && setReadiness(r))}>
              Check
            </Button>
          }
        />
        {readiness?.items.map((it, i) => (
          <div key={i} className="px-4 py-2.5 text-sm">
            <Badge tone={it.severity === "blocker" ? "danger" : it.severity === "warning" ? "warning" : "success"}>{it.severity}</Badge>
            <span className="ml-2 text-fg">{it.title}</span>
            <span className="ml-2 text-fg-muted">{it.message}</span>
          </div>
        ))}
      </Section>
    </div>
  );
}

const LEVELS: { value: LogLevel; label: string }[] = [
  { value: "error", label: "Errors only" },
  { value: "warning", label: "Warnings" },
  { value: "info", label: "Info (default)" },
  { value: "debug", label: "Debug (verbose)" },
];

function Advanced({ s }: { s: SettingsT }) {
  const dataDir = useSnap((x) => x.app.dataDirectory);
  const [confirmReset, setConfirmReset] = useState(false);
  useEffect(() => {
    if (!confirmReset) return;
    const t = setTimeout(() => setConfirmReset(false), 4000);
    return () => clearTimeout(t);
  }, [confirmReset]);
  return (
    <div className="flex flex-col gap-7">
      <Section title="Diagnostics">
        <Row
          label="Log level"
          description="Debug records more detail and can be large. Use it when reproducing a problem."
          control={<Select<LogLevel> aria-label="Log level" minWidth={170} value={s.logLevel} onValueChange={(v) => void actions.updateSettings({ logLevel: v })} options={LEVELS} />}
        />
        <PathRow
          label="Data folder"
          description="Settings, logs and cached art live here."
          path={dataDir}
          actions={
            <Button size="sm" variant="secondary" icon={<FolderOpen size={13} />} onClick={() => void actions.openPath(dataDir)}>
              Open
            </Button>
          }
        />
      </Section>
      <LogViewer />
      <Section title="Setup">
        <Row
          label="Run setup again"
          description="Walks through sign-in, finding installed games, library folders and download mode again. Nothing is removed."
          control={
            <Button size="sm" variant="secondary" onClick={() => void actions.restartOnboarding()}>
              Run setup
            </Button>
          }
        />
      </Section>
      <Section title="Reset">
        <Row
          label="Reset all settings"
          description="Restores every setting on this page to its default. Your install and sign-in aren't touched."
          control={
            <Button
              size="sm"
              variant={confirmReset ? "danger" : "secondary"}
              onClick={() => {
                if (confirmReset) {
                  setConfirmReset(false);
                  void actions.resetSettings();
                } else setConfirmReset(true);
              }}
            >
              {confirmReset ? "Click again to confirm" : "Reset settings"}
            </Button>
          }
        />
      </Section>
    </div>
  );
}

/* ------------------------------------------------------------------ page */

export function Settings() {
  const section = useUi((s) => s.settingsSection);
  const go = useUi((s) => s.go);
  const settings = useSnap((s) => s.settings);
  const err = useUi((s) => s.errors.settings);
  const current = SECTIONS.find((s) => s.id === section) ?? SECTIONS[0]!;

  return (
    <PageScroll wide>
      <PageHeader title="Settings" />
      <div className="grid grid-cols-[200px_minmax(0,1fr)] gap-10 max-[1100px]:grid-cols-[188px_minmax(0,1fr)] max-[1100px]:gap-6">
        <nav aria-label="Settings sections" className="sticky top-0 flex h-fit flex-col gap-0.5">
          {SECTIONS.map((s) => {
            const active = s.id === section;
            return (
              <button
                key={s.id}
                type="button"
                aria-current={active ? "page" : undefined}
                onClick={() => go("settings", s.id)}
                className={cx(
                  "flex h-9 items-center gap-2.5 rounded-lg px-3 text-left text-sm font-medium transition-[background-color,color] duration-150 ease-out",
                  active ? "bg-surface-2 text-fg" : "text-fg-muted hover:bg-surface-hover hover:text-fg",
                )}
              >
                <span className={cx("transition-colors duration-150", active ? "text-accent-text" : "text-fg-subtle")}>{s.icon}</span>
                <span className="truncate">{s.label}</span>
              </button>
            );
          })}
        </nav>
        <div key={current.id} className="g-page-in w-[640px] min-w-0 max-w-full" role="region" aria-label={current.label}>
          {err && <ErrorView className="mb-5" error={err.error} retry={err.retry} onDismiss={() => useUi.getState().setError("settings", undefined)} />}
          {current.id === "general" && <General s={settings} />}
          {current.id === "downloads" && <Downloads s={settings} />}
          {current.id === "library" && <LibrarySettings s={settings} />}
          {current.id === "egl" && <Egl s={settings} />}
          {current.id === "advanced" && <Advanced s={settings} />}
          {current.id === "about" && <AboutPanel />}
        </div>
      </div>
    </PageScroll>
  );
}
