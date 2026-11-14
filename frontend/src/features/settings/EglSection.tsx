import { useState } from "react";
import { CircleCheck, RefreshCw } from "lucide-react";
import type { EglReadiness, Settings as SettingsT } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { plural } from "../../state/format";
import { goGame } from "../../state/router";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, Disclosure } from "../../ui";
import { groupInstalls } from "../import/model";
import { ImportDialog } from "../import/ImportDialog";
import { Block, Group, Line, Setting, SwitchRow, useSettingsQuery } from "./parts";
import { sectionById } from "./sections";

/* --------------------------------------------------------- "Ready to uninstall?" */

/** One all-clear line when nothing blocks; a compact list (one line per game) only when something does. */
export function ReadinessResult({ result }: { result: EglReadiness }) {
  const blockers = result.items.filter((i) => i.severity === "blocker");
  if (result.ready) {
    return (
      <p className="mt-1 flex items-center gap-2 text-sm text-success-text" role="status">
        <CircleCheck aria-hidden size={15} />
        Nothing depends on it. You can uninstall it.
      </p>
    );
  }
  return (
    <div role="status">
      <p className="mt-1 text-sm text-danger-text">
        {plural(blockers.length, "game")} still {blockers.length === 1 ? "needs" : "need"} it
      </p>
      <ul className="mt-2 flex flex-col">
        {blockers.map((i, n) => (
          <li key={n} className="flex min-h-9 items-center gap-3 text-sm">
            <span className="shrink-0 font-medium text-fg">{i.title}</span>
            <span className="min-w-0 flex-1 truncate text-fg-muted" title={i.message}>
              {i.message}
            </span>
            {i.appName && (
              <Button size="sm" variant="ghost" onClick={() => goGame(i.appName!)}>
                Open
              </Button>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function ReadinessCheck() {
  const checking = usePending("readiness");
  const [result, setResult] = useState<EglReadiness | null>(null);
  const run = () => void actions.checkEglReadiness().then((r) => r && setResult(r));
  return (
    <Setting label="Ready to uninstall Epic Games Launcher?" find="remove check blockers readiness egl">
      <Line
        label="Ready to uninstall Epic Games Launcher?"
        control={
          <Button size="sm" variant="secondary" loading={checking} icon={result ? <RefreshCw size={13} /> : undefined} onClick={run}>
            {result ? "Check again" : "Check"}
          </Button>
        }
      >
        {result && <ReadinessResult result={result} />}
      </Line>
    </Setting>
  );
}

/* ------------------------------------------------------------------ section */

export function EglSection({ s }: { s: SettingsT }) {
  const egl = useSnap((x) => x.egl);
  const signedIn = useSnap((x) => x.auth.status === "signedIn");
  const rescanning = usePending("rescan") || egl.scanning;
  const searching = useSettingsQuery() !== "";
  const [importOpen, setImportOpen] = useState(false);
  const def = sectionById("egl");
  const groups = groupInstalls(egl.installs);
  const ready = groups.ready.length;

  // The section title already says "Epic Games Launcher"; only an unusual state earns a line of its own.
  const note = !egl.launcherInstalled ? "Not installed on this PC" : egl.busyApps.length > 0 ? "Busy with a game right now" : null;

  return (
    <Block id="egl" title={def.label}>
      <Group>
        {note && (
          <Setting label="Epic Games Launcher" find="detected running installed busy">
            <Line label={note} />
          </Setting>
        )}
        <SwitchRow
          label="Keep Epic Games Launcher in sync"
          find="sync version location manifest repair redownload"
          description="So the launcher doesn't download games Glider already updated or moved."
          checked={s.eglSync}
          onChange={(v) => void actions.updateSettings({ eglSync: v })}
        />
        <Setting label="Existing installs" find="import games bulk scan link egl folder locate rescan">
          <Line
            label="Existing installs"
            description={ready > 0 ? `${plural(ready, "game")} ready to import` : "Nothing new to import"}
            control={
              <>
                <Button size="sm" variant="ghost" loading={rescanning} icon={<RefreshCw size={13} />} onClick={() => void actions.rescanEgl()}>
                  Rescan
                </Button>
                <Button size="sm" variant="secondary" disabled={!signedIn} onClick={() => setImportOpen(true)}>
                  Import…
                </Button>
              </>
            }
          />
        </Setting>
        <ReadinessCheck />
      </Group>

      <div data-group>
        <Disclosure title="Advanced" open={searching ? true : undefined}>
          <Setting label="Scan all drives" find="deep scan every drive find installs">
            <Line
              label="Scan all drives"
              description="Looks beyond the launcher's own list."
              control={
                <Button size="sm" variant="secondary" disabled={rescanning} onClick={() => void actions.rescanEgl(true)}>
                  Scan
                </Button>
              }
            />
          </Setting>
        </Disclosure>
      </div>
      {importOpen && <ImportDialog open={importOpen} onOpenChange={setImportOpen} />}
    </Block>
  );
}
