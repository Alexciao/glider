import { useEffect, useId, useState, type ReactNode } from "react";
import { Cog, Download, FolderOpen, Gamepad2, Info, RefreshCw, ScrollText, ShieldCheck } from "lucide-react";
import type { LogLevel, Settings as SettingsT, ThemePreference } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { isBusy, candidateIssue, candidateStatus } from "../../state/derive";
import { formatRelativeTime, prettyVersion, formatBytes } from "../../state/format";
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
  TextArea,
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
  { id: "game", label: "Game", icon: <Gamepad2 size={16} /> },
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
      <Section title="Updates">
        <SwitchRow label="Update automatically" description="Download new versions of Fortnite in the background." checked={s.autoUpdate} onChange={(v) => void set({ autoUpdate: v })} />
        <SwitchRow label="Only when I'm not playing" description="Wait until the game is closed before starting an automatic update." checked={s.autoUpdateOnlyWhenIdle} disabled={!s.autoUpdate} onChange={(v) => void set({ autoUpdateOnlyWhenIdle: v })} />
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

function Game({ s }: { s: SettingsT }) {
  const installed = useSnap((x) => x.game.installed);
  const snap = useSnap((x) => x);
  const busy = isBusy(snap);
  const openDialog = useUi((x) => x.openDialog);
  const verifying = usePending("verify");
  const [args, setArgs] = useState(s.launchArgs);
  const [seenArgs, setSeenArgs] = useState(s.launchArgs);
  if (seenArgs !== s.launchArgs) {
    setSeenArgs(s.launchArgs);
    setArgs(s.launchArgs);
  }
  const commitArgs = () => args !== s.launchArgs && void actions.updateSettings({ launchArgs: args });

  return (
    <div className="flex flex-col gap-7">
      <Section title="Launch">
        <Row
          label="Launch arguments"
          description="Extra command-line options passed to Fortnite. Leave empty unless you know you need them."
        >
          <div className="mt-2.5">
            <TextArea
              aria-label="Launch arguments"
              placeholder="-windowed -nosplash"
              value={args}
              onChange={(e) => setArgs(e.target.value.replace(/[\r\n]+/g, " "))}
              onBlur={commitArgs}
              onKeyDown={(e) => e.key === "Enter" && (e.preventDefault(), e.currentTarget.blur())}
              className="font-mono text-xs"
            />
          </div>
        </Row>
      </Section>

      <Section title="Install">
        <PathRow
          label="Install location"
          empty="Fortnite isn't installed"
          path={installed?.installPath ?? null}
          actions={
            installed ? (
              <>
                <Button size="sm" variant="secondary" icon={<FolderOpen size={13} />} onClick={() => void actions.openInstallFolder()}>
                  Open
                </Button>
                <Button size="sm" variant="secondary" disabled={busy} onClick={() => openDialog({ kind: "move" })}>
                  Move…
                </Button>
              </>
            ) : (
              <Button size="sm" variant="primary" disabled={snap.auth.status !== "signedIn"} onClick={() => openDialog({ kind: "install" })}>
                Install…
              </Button>
            )
          }
        />
        <PathRow
          label="Default install folder"
          description="Where new installs go. You can still pick a different folder each time."
          path={s.defaultInstallBase}
          empty="Chosen each time"
          actions={
            <Button
              size="sm"
              variant="secondary"
              onClick={async () => {
                const p = await actions.pickFolder(s.defaultInstallBase, "Choose the default install folder");
                if (p) void actions.updateSettings({ defaultInstallBase: p });
              }}
            >
              Change…
            </Button>
          }
        />
        <Row
          label="Verify and repair"
          description="Checks every file against Epic's manifest and re-downloads only what's missing or damaged."
          control={
            <Button size="sm" variant="secondary" disabled={!installed || busy || snap.auth.status !== "signedIn"} loading={verifying} onClick={() => void actions.verify("settings")}>
              Verify files
            </Button>
          }
        />
        <Row
          label="Uninstall"
          description="Remove Fortnite from Glider, with the option to delete its files."
          control={
            <Button size="sm" variant="secondary" className="text-danger-text" disabled={!installed || busy} onClick={() => openDialog({ kind: "uninstall" })}>
              Uninstall…
            </Button>
          }
        />
      </Section>
    </div>
  );
}

function Egl({ s }: { s: SettingsT }) {
  const egl = useSnap((x) => x.egl);
  const game = useSnap((x) => x.game);
  const rescanning = usePending("rescan");
  const installed = game.installed;
  const authed = useSnap((x) => x.auth.status === "signedIn");

  return (
    <div className="flex flex-col gap-7">
      <Section title="Status">
        <Row label="Epic Games Launcher" control={<Badge tone={egl.launcherInstalled ? "success" : "neutral"} dot>{egl.launcherInstalled ? "Detected" : "Not installed"}</Badge>} />
        <Row
          label="Launcher is running"
          description={egl.busyWithGame ? "It's installing or updating Fortnite right now. Glider waits so the two don't collide." : undefined}
          control={<Badge tone={egl.busyWithGame ? "warning" : egl.launcherRunning ? "accent" : "neutral"} dot>{egl.busyWithGame ? "Busy with Fortnite" : egl.launcherRunning ? "Running" : "Closed"}</Badge>}
        />
        <PathRow
          label="Shared install"
          description={installed?.sharedWithEgl ? "Glider and Epic Games Launcher point at the same folder, so the game is stored once." : undefined}
          path={installed?.sharedWithEgl ? installed.installPath : null}
          empty="Glider's install isn't shared with the launcher"
        />
      </Section>

      <Section title="Sync">
        <SwitchRow
          label="Keep Epic Games Launcher in sync"
          description={
            <>
              When Glider updates or moves Fortnite, it tells Epic Games Launcher the new version and location, so the launcher doesn't try to
              re-download or repair it. Glider backs up the launcher's files first and never writes while the launcher is busy with the game.
              {installed?.sharedWithEgl && !egl.syncAvailable && <span className="mt-1 block text-warning-text">Epic Games Launcher has no record of this install yet, so there's nothing to sync.</span>}
            </>
          }
          checked={s.eglSync}
          onChange={(v) => void actions.updateSettings({ eglSync: v })}
        />
      </Section>

      <Section
        title="Existing installs"
        description={egl.lastScan ? `Last scanned ${formatRelativeTime(egl.lastScan)}` : "Looks for Fortnite installs known to Epic Games Launcher."}
        action={
          <Button size="sm" variant="secondary" loading={rescanning} icon={<RefreshCw size={13} />} onClick={() => void actions.rescanEgl()}>
            Re-scan
          </Button>
        }
      >
        {egl.candidates.length === 0 ? (
          <div className="px-4 py-5 text-sm text-fg-muted">No installs found. Epic Games Launcher {egl.launcherInstalled ? "doesn't list Fortnite." : "isn't installed."}</div>
        ) : (
          egl.candidates.map((c) => {
            const status = candidateStatus(c, game.latestVersion);
            const issue = candidateIssue(c);
            const usable = !issue;
            return (
              <div key={c.id} className="flex items-center gap-4 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <PathText path={c.installPath} className="text-sm font-medium text-fg" tailSegments={3} />
                  <p className="mt-0.5 text-xs text-fg-muted tabular-nums">
                    {issue ?? `${c.version ? `Version ${prettyVersion(c.version)}` : "Version unknown"}${c.sizeBytes ? `, ${formatBytes(c.sizeBytes)}` : ""}`}
                  </p>
                </div>
                <Badge tone={status.tone}>{status.text}</Badge>
                {installed === null && usable && authed && (
                  <Button size="sm" variant="secondary" onClick={() => void actions.adopt({ candidateId: c.id, path: null, verify: "auto" }, "settings")}>
                    Use this install
                  </Button>
                )}
              </div>
            );
          })
        )}
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
          {current.id === "game" && <Game s={settings} />}
          {current.id === "egl" && <Egl s={settings} />}
          {current.id === "advanced" && <Advanced s={settings} />}
          {current.id === "about" && <AboutPanel />}
        </div>
      </div>
    </PageScroll>
  );
}
