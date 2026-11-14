import { useState } from "react";
import type { DownloadMode, Settings as SettingsT, WhilePlayingPolicy } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { DOWNLOAD_MODES, modeSummary } from "../../state/downloadModes";
import { formatBytes } from "../../state/format";
import { useSnap } from "../../state/store";
import { Disclosure, SegmentedControl, Select, TextField } from "../../ui";
import { AutoNumberRow, Block, Group, Line, Setting, SwitchRow, useSettingsQuery } from "./parts";
import { sectionById } from "./sections";

const SPEED_LIMITS = [
  { value: "none", label: "No limit" },
  { value: String(5_000_000), label: "5 MB/s" },
  { value: String(10_000_000), label: "10 MB/s" },
  { value: String(25_000_000), label: "25 MB/s" },
  { value: String(50_000_000), label: "50 MB/s" },
] as const;

/** A host name like download.example.com: letters, digits and hyphens in dot-separated labels, at least one dot. */
export const isHostName = (v: string): boolean => /^(?=.{3,253}$)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$/i.test(v.trim());

function CdnRow({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  const [manualPicked, setManualPicked] = useState(false);
  const manual = s.preferredCdn !== null || manualPicked;
  const [draft, setDraft] = useState(s.preferredCdn ?? "");
  const [seen, setSeen] = useState(s.preferredCdn);
  if (seen !== s.preferredCdn) {
    setSeen(s.preferredCdn);
    setDraft(s.preferredCdn ?? "");
  }
  const invalid = draft.trim() !== "" && !isHostName(draft);
  const commit = () => {
    const v = draft.trim();
    if (v === "") {
      setManualPicked(false);
      if (s.preferredCdn !== null) void set({ preferredCdn: null });
    } else if (isHostName(v) && v !== s.preferredCdn) void set({ preferredCdn: v });
  };
  return (
    <Setting label="Download server" find="cdn host content delivery manual automatic region">
      <Line
        label="Download server"
        control={
          <SegmentedControl
            aria-label="Download server"
            className="w-[220px]"
            value={manual ? "manual" : "auto"}
            onValueChange={(v) => {
              if (v === "auto") {
                setManualPicked(false);
                setDraft("");
                if (s.preferredCdn !== null) void set({ preferredCdn: null });
              } else setManualPicked(true);
            }}
            options={[
              { value: "auto", label: "Automatic" },
              { value: "manual", label: "Manual" },
            ]}
          />
        }
      >
        {manual && (
          <div className="mt-2">
            <TextField
              aria-label="Download server host name"
              placeholder="download.epicgames.com"
              value={draft}
              invalid={invalid}
              onChange={(e) => setDraft(e.target.value)}
              onBlur={commit}
              onKeyDown={(e) => e.key === "Enter" && commit()}
              wrapperClassName="max-w-[360px]"
            />
            {invalid && <p className="mt-1.5 text-xs text-danger-text">Enter a host name like download.epicgames.com.</p>}
          </div>
        )}
      </Line>
    </Setting>
  );
}

export function DownloadsSection({ s }: { s: SettingsT }) {
  const set = actions.updateSettings;
  const info = useSnap((x) => x.app);
  const searching = useSettingsQuery() !== "";
  const def = sectionById("downloads");
  const limit = s.speedLimitBytesPerSecond === null ? "none" : String(s.speedLimitBytesPerSecond);
  const limitOptions = SPEED_LIMITS.some((l) => l.value === limit) ? SPEED_LIMITS : [...SPEED_LIMITS, { value: limit, label: `${formatBytes(Number(limit))}/s` }];

  return (
    <Block id="downloads" title={def.label}>
      <Group>
        <Setting label="Download mode" find="background balanced full priority cpu disk">
          <Line
            label="Download mode"
            description={modeSummary(s.downloadMode)}
            control={
              <SegmentedControl<DownloadMode>
                aria-label="Download mode"
                className="w-[300px]"
                value={s.downloadMode}
                onValueChange={(v) => void set({ downloadMode: v })}
                options={DOWNLOAD_MODES.map((m) => ({ value: m.value, label: m.label }))}
              />
            }
          />
        </Setting>
        <Setting label="Speed limit" find="cap bandwidth throttle mb/s">
          <Line label="Speed limit" control={<Select aria-label="Speed limit" minWidth={190} value={limit} onValueChange={(v) => void set({ speedLimitBytesPerSecond: v === "none" ? null : Number(v) })} options={limitOptions} />} />
        </Setting>
        <SwitchRow label="Full speed when I'm away" find="idle away afk" description="While the PC is idle and no game is running." checked={s.idleFullSpeed} onChange={(v) => void set({ idleFullSpeed: v })} />
        <Setting label="While a game is running" find="playing pause throttle continue gaming">
          <Line
            label="While a game is running"
            control={
              <SegmentedControl<WhilePlayingPolicy>
                aria-label="While a game is running"
                className="w-[270px]"
                value={s.whilePlaying}
                onValueChange={(v) => void set({ whilePlaying: v })}
                options={[
                  { value: "pause", label: "Pause" },
                  { value: "throttle", label: "Throttle" },
                  { value: "continue", label: "Continue" },
                ]}
              />
            }
          />
        </Setting>
      </Group>

      <div data-group>
        <Disclosure title="Advanced" open={searching ? true : undefined}>
          <CdnRow s={s} />
          <AutoNumberRow
            label="Download workers"
            find="connections parallel threads"
            value={s.downloadWorkers}
            auto={info.autoWorkers}
            min={1}
            max={32}
            slider
            onChange={(v) => void set({ downloadWorkers: v })}
          />
          <AutoNumberRow
            label="Shared memory"
            find="cache ram memory"
            value={s.sharedMemoryMb}
            auto={info.autoSharedMemoryMb}
            min={256}
            max={8192}
            step={256}
            suffix="MB"
            onChange={(v) => void set({ sharedMemoryMb: v })}
          />
        </Disclosure>
      </div>
    </Block>
  );
}
