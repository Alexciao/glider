import { useEffect, useState } from "react";
import type { LogLevel, Settings as SettingsT, ThemePreference } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { removeOverlay } from "../../state/overlay";
import { useSnap } from "../../state/store";
import { useUi, usePending } from "../../state/ui";
import { Avatar, Badge, Button, Disclosure, SegmentedControl, Select } from "../../ui";
import { LogViewer } from "./LogViewer";
import { Block, Group, Line, Setting, SwitchRow, useSettingsQuery } from "./parts";
import { sectionById } from "./sections";

/* ------------------------------------------------------------------ Epic overlay */

export function OverlaySection() {
  const overlay = useSnap((x) => x.overlay);
  const signedIn = useSnap((x) => x.auth.status === "signedIn");
  const working = usePending("overlay");
  const def = sectionById("overlay");
  const busy = overlay.busy || working;

  const status = busy ? (
    <Badge tone="accent" dot>
      Installing
    </Badge>
  ) : !overlay.installed ? (
    <Badge>Not installed</Badge>
  ) : overlay.updateAvailable ? (
    <Badge tone="warning" dot>
      Update available
    </Badge>
  ) : (
    <Badge tone="success" dot>
      {overlay.version ? `Version ${overlay.version}` : "Installed"}
    </Badge>
  );

  return (
    <Block id="overlay" title={def.label}>
      <Group>
        <Setting label="Friends and achievements" find="epic eos overlay install update remove version status online services">
          <Line
            label="Friends and achievements"
            description="The Epic overlay, used by the few games that support it."
            control={
              <>
                {status}
                {!overlay.installed ? (
                  <Button size="sm" variant="secondary" loading={busy} disabled={!signedIn} onClick={() => void actions.installOverlay()}>
                    Install
                  </Button>
                ) : (
                  <>
                    {overlay.updateAvailable && (
                      <Button size="sm" variant="secondary" loading={busy} disabled={!signedIn} onClick={() => void actions.installOverlay()}>
                        Update
                      </Button>
                    )}
                    <Button size="sm" variant="ghost" disabled={busy} onClick={() => void removeOverlay()}>
                      Remove
                    </Button>
                  </>
                )}
              </>
            }
          />
        </Setting>
        {overlay.installed && <SwitchRow label="Use the overlay in games" find="enable disable on off" checked={overlay.enabled} disabled={busy} onChange={(v) => void actions.setOverlayEnabled(v)} />}
      </Group>
    </Block>
  );
}

/* ------------------------------------------------------------------ links */

const Code = ({ children }: { children: string }) => <code className="font-mono text-2xs text-fg">{children}</code>;

export function LinksSection({ s }: { s: SettingsT }) {
  const launcherInstalled = useSnap((x) => x.egl.launcherInstalled);
  const def = sectionById("links");
  const recommend = !launcherInstalled && !s.handleEpicLinks;
  return (
    <Block id="links" title={def.label}>
      <Group>
        <SwitchRow
          label="Open Epic Games Launcher links in Glider"
          find="com.epicgames.launcher protocol store buttons shortcuts handler"
          description={
            <>
              <Code>com.epicgames.launcher://</Code>. {recommend ? "Recommended now that the launcher is gone." : "Turn on once the launcher is uninstalled."}
            </>
          }
          checked={s.handleEpicLinks}
          onChange={(v) => void actions.updateSettings({ handleEpicLinks: v })}
        />
        <Setting label="Glider links" find="glider:// protocol url open game">
          <Line
            label={<Code>glider://</Code>}
            control={
              <Badge tone="success" dot>
                Always on
              </Badge>
            }
          />
        </Setting>
      </Group>
    </Block>
  );
}

/* ------------------------------------------------------------------ appearance, startup, account */

export function AppearanceSection({ s }: { s: SettingsT }) {
  const def = sectionById("appearance");
  return (
    <Block id="appearance" title={def.label}>
      <Group>
        <Setting label="Theme" find="dark light system color mode">
          <Line
            label="Theme"
            control={
              <SegmentedControl<ThemePreference>
                aria-label="Theme"
                className="w-[240px]"
                value={s.theme}
                onValueChange={(v) => {
                  useUi.getState().touchTheme();
                  void actions.updateSettings({ theme: v });
                }}
                options={[
                  { value: "system", label: "System" },
                  { value: "dark", label: "Dark" },
                  { value: "light", label: "Light" },
                ]}
              />
            }
          />
        </Setting>
      </Group>
    </Block>
  );
}

export function StartupSection({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  const def = sectionById("startup");
  return (
    <Block id="startup" title={def.label}>
      <Group>
        <SwitchRow label="Start with Windows" find="boot login autostart" checked={s.startWithWindows} onChange={(v) => void set({ startWithWindows: v })} />
        <SwitchRow label="Start minimized" find="tray quiet background" checked={s.startMinimized} onChange={(v) => void set({ startMinimized: v })} />
        <SwitchRow label="Close to tray" find="exit quit background window" description="Keeps updates running." checked={s.closeToTray} onChange={(v) => void set({ closeToTray: v })} />
        <SwitchRow label="Minimize to tray while playing" find="taskbar hide game" checked={s.minimizeToTrayWhilePlaying} onChange={(v) => void set({ minimizeToTrayWhilePlaying: v })} />
        <SwitchRow label="Show notifications" find="toast alerts finished attention" checked={s.notifications} onChange={(v) => void set({ notifications: v })} />
      </Group>
    </Block>
  );
}

export function AccountSection() {
  const auth = useSnap((x) => x.auth);
  const offline = useSnap((x) => x.connectivity === "offline");
  const signingIn = usePending("signIn") || auth.status === "signingIn";
  const signingOut = usePending("signOut");
  const def = sectionById("account");
  const account = auth.account;
  const signedIn = auth.status === "signedIn" && !!account;
  return (
    <Block id="account" title={def.label}>
      <Group>
        <Setting label="Epic account" find="sign in sign out login profile name">
          <div className="flex items-center gap-4 py-2">
            <Avatar size="xl" name={account?.displayName ?? "Epic"} seed={account?.accountId} src={account?.avatarUrl} />
            <div className="min-w-0 flex-1">
              <p className="truncate text-base font-semibold text-fg">{signedIn ? account.displayName : auth.status === "expired" ? "Session expired" : "Not signed in"}</p>
              {(offline || !signedIn) && <p className="mt-0.5 text-sm text-fg-muted">{offline ? "Offline" : "Sign in to see your library."}</p>}
            </div>
            {signedIn ? (
              <Button variant="secondary" loading={signingOut} onClick={() => void actions.signOut()}>
                Sign out
              </Button>
            ) : (
              <Button variant="primary" loading={signingIn} disabled={offline} onClick={() => void actions.signIn("settings")}>
                Sign in
              </Button>
            )}
          </div>
        </Setting>
      </Group>
    </Block>
  );
}

/* ------------------------------------------------------------------ advanced */

const LEVELS: { value: LogLevel; label: string }[] = [
  { value: "error", label: "Errors only" },
  { value: "warning", label: "Warnings" },
  { value: "info", label: "Info (default)" },
  { value: "debug", label: "Debug (verbose)" },
];

export function AdvancedSection({ s }: { s: SettingsT }) {
  const dataDir = useSnap((x) => x.app.dataDirectory);
  const logDir = useSnap((x) => x.app.logDirectory);
  const searching = useSettingsQuery() !== "";
  const [confirmReset, setConfirmReset] = useState(false);
  // "Open logs" fix actions bump `logsFocus`: open the disclosure so the log viewer exists to scroll to.
  const logsFocus = useUi((x) => x.logsFocus);
  const [seenFocus, setSeenFocus] = useState(logsFocus);
  const [moreOpen, setMoreOpen] = useState(logsFocus > 0);
  if (seenFocus !== logsFocus) {
    setSeenFocus(logsFocus);
    setMoreOpen(true);
  }
  const def = sectionById("advanced");
  useEffect(() => {
    if (!confirmReset) return;
    const t = setTimeout(() => setConfirmReset(false), 4000);
    return () => clearTimeout(t);
  }, [confirmReset]);
  return (
    <Block id="advanced" title={def.label}>
      <Group>
        <Setting label="Welcome setup" find="run setup again onboarding welcome first run wizard rescan">
          <Line
            label="Welcome setup"
            control={
              <Button size="sm" variant="secondary" onClick={() => void actions.restartOnboarding()}>
                Run again
              </Button>
            }
          />
        </Setting>
        <Setting label="Folders" find="data folder log folder logs diagnostics open explorer">
          <Line
            label="Data and logs"
            control={
              <>
                <Button size="sm" variant="secondary" onClick={() => void actions.openPath(dataDir)}>
                  Open data folder
                </Button>
                <Button size="sm" variant="secondary" onClick={() => void actions.openPath(logDir)}>
                  Open logs
                </Button>
              </>
            }
          />
        </Setting>
      </Group>
      <div data-group>
        <Disclosure title="More" open={searching || moreOpen} onOpenChange={setMoreOpen}>
          <Setting label="Log level" find="debug verbose logging">
            <Line label="Log level" control={<Select<LogLevel> aria-label="Log level" minWidth={170} value={s.logLevel} onValueChange={(v) => void actions.updateSettings({ logLevel: v })} options={LEVELS} />} />
          </Setting>
          {!searching && <LogViewer />}
          <Setting label="Reset all settings" find="defaults restore">
            <Line
              label="Reset all settings"
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
                  {confirmReset ? "Click again to reset" : "Reset"}
                </Button>
              }
            />
          </Setting>
        </Disclosure>
      </div>
    </Block>
  );
}
