import { FolderPlus, RefreshCw } from "lucide-react";
import type { AutoUpdatePolicy, Settings as SettingsT } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { addLibraryFolder, makeDefaultFolder, removeLibraryFolder } from "../../state/folders";
import { formatRelativeTime, plural } from "../../state/format";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, SegmentedControl, Select } from "../../ui";
import { FolderList } from "../common/FolderList";
import { Block, Group, Line, PathRow, Setting, SwitchRow } from "./parts";
import { sectionById } from "./sections";

/** Library folders, Updates and Cloud saves: the three sections about the games themselves. */

export function LibraryFoldersSection({ s }: { s: SettingsT }) {
  const storage = useSnap((x) => x.storage);
  const def = sectionById("library");
  return (
    <Block id="library" title={def.label}>
      <Group>
        <Setting label="Library folders" find="folders default drive free space remove add install location">
          <FolderList
            folders={storage.folders}
            drives={storage.drives}
            defaultPath={s.defaultInstallBase}
            onDefault={(p) => void makeDefaultFolder(p)}
            onRemove={(p) => void removeLibraryFolder(s, p)}
            onOpen={(p) => void actions.openPath(p)}
          />
          <div className="pt-3">
            <Button size="sm" variant="secondary" icon={<FolderPlus size={13} />} onClick={() => void addLibraryFolder(s)}>
              Add folder…
            </Button>
          </div>
        </Setting>
      </Group>
    </Block>
  );
}

const INTERVALS = [
  { value: "15", label: "Every 15 minutes" },
  { value: "30", label: "Every 30 minutes" },
  { value: "60", label: "Every hour" },
  { value: "120", label: "Every 2 hours" },
  { value: "360", label: "Every 6 hours" },
  { value: "720", label: "Every 12 hours" },
] as const;

export function UpdatesSection({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  const library = useSnap((x) => x.library);
  const signedIn = useSnap((x) => x.auth.status === "signedIn");
  const checking = usePending("check");
  const updatingAll = usePending("updateAll");
  const def = sectionById("updates");
  const policy: Exclude<AutoUpdatePolicy, "default"> = s.autoUpdate === "default" ? "always" : s.autoUpdate;
  const n = library.updatesAvailable;
  return (
    <Block id="updates" title={def.label}>
      <Group>
        <Setting label="Update games automatically" find="auto always ask never patch">
          <Line
            label="Update games automatically"
            control={
              <SegmentedControl<Exclude<AutoUpdatePolicy, "default">>
                aria-label="Update games automatically"
                className="w-[270px]"
                value={policy}
                onValueChange={(v) => void set({ autoUpdate: v })}
                options={[
                  { value: "always", label: "Always" },
                  { value: "ask", label: "Ask" },
                  { value: "never", label: "Never" },
                ]}
              />
            }
          />
        </Setting>
        <SwitchRow label="Only when I'm not playing" find="idle running" checked={s.autoUpdateOnlyWhenIdle} disabled={policy === "never"} onChange={(v) => void set({ autoUpdateOnlyWhenIdle: v })} />
        <Setting label="Check for updates" find="interval frequency schedule">
          <Line
            label="Check for updates"
            control={
              <Select
                aria-label="Check for updates"
                minWidth={190}
                value={String(s.updateCheckIntervalMinutes)}
                onValueChange={(v) => void set({ updateCheckIntervalMinutes: Number(v) })}
                options={INTERVALS.some((i) => i.value === String(s.updateCheckIntervalMinutes)) ? INTERVALS : [...INTERVALS, { value: String(s.updateCheckIntervalMinutes), label: `Every ${s.updateCheckIntervalMinutes} minutes` }]}
              />
            }
          />
        </Setting>
        <Setting label="Check now" find="refresh look for updates update all install">
          <Line
            label={n === 0 ? "Everything is up to date" : `${plural(n, "game")} can be updated`}
            description={library.syncing ? "Checking…" : library.lastSyncedAt ? `Checked ${formatRelativeTime(library.lastSyncedAt)}` : undefined}
            control={
              <>
                <Button size="sm" variant="secondary" icon={<RefreshCw size={13} />} loading={checking || library.syncing} disabled={!signedIn} onClick={() => void actions.checkForUpdates()}>
                  Check now
                </Button>
                {n > 0 && (
                  <Button size="sm" variant="secondary" loading={updatingAll} disabled={!signedIn} onClick={() => void actions.updateAll()}>
                    Update all
                  </Button>
                )}
              </>
            }
          />
        </Setting>
      </Group>
    </Block>
  );
}

export function CloudSavesSection({ s }: { s: SettingsT }) {
  const dataDir = useSnap((x) => x.app.dataDirectory);
  const def = sectionById("saves");
  const backups = `${dataDir.replace(/[\\/]+$/, "")}\\backups\\saves`;
  return (
    <Block id="saves" title={def.label}>
      <Group>
        <SwitchRow
          label="Sync saves with Epic's cloud"
          find="cloud saves upload download progress"
          description="Before and after you play. Each game can opt out."
          checked={s.cloudSaves}
          onChange={(v) => void actions.updateSettings({ cloudSaves: v })}
        />
        <PathRow
          label="Save backups"
          find="backup location folder copy"
          path={backups}
          actions={
            <Button size="sm" variant="secondary" onClick={() => void actions.openPath(backups)}>
              Open folder
            </Button>
          }
        />
      </Group>
    </Block>
  );
}
