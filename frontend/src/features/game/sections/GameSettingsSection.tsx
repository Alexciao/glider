import { useId, useState, type ReactNode } from "react";
import { ChevronRight } from "lucide-react";
import type { AutoUpdatePolicy, GameDetail, GameSettings, LaunchMode } from "../../../ipc/contract.gen";
import { actions } from "../../../state/actions";
import { useSnap } from "../../../state/store";
import { Button, SegmentedControl, Select, Switch, TextArea, TextField, cx } from "../../../ui";

const MODES = [
  { value: "auto", label: "Auto" },
  { value: "online", label: "Online" },
  { value: "offline", label: "Offline" },
] as const satisfies readonly { value: LaunchMode; label: string }[];

const POLICY_LABEL: Record<AutoUpdatePolicy, string> = { default: "Default", always: "Always", ask: "Ask first", never: "Never" };
const DEFAULT_OPTION = "__default__";

function Field({ label, htmlFor, children }: { label: string; htmlFor?: string; children: ReactNode }) {
  return (
    <div className="grid grid-cols-[150px_minmax(0,1fr)] items-center gap-x-6 max-[1100px]:grid-cols-[130px_minmax(0,1fr)]">
      <label htmlFor={htmlFor} className="text-sm text-fg-muted">
        {label}
      </label>
      <div className="min-w-0">{children}</div>
    </div>
  );
}

/** A text field that commits on blur or Enter, and takes its value from outside when that changes. */
function CommitField({ value, onCommit, ...rest }: { value: string; onCommit: (v: string) => void; id: string; placeholder?: string }) {
  const [text, setText] = useState(value);
  const [seen, setSeen] = useState(value);
  if (seen !== value) {
    setSeen(value);
    setText(value);
  }
  const commit = () => {
    const v = text.trim();
    if (v !== value) onCommit(v);
    setText(v);
  };
  return <TextField {...rest} value={text} onChange={(e) => setText(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === "Enter" && commit()} />;
}

/**
 * Everything advanced about one game, collapsed behind a single disclosure: launch arguments, executable, working
 * folder, the developer's declared launch options, online/offline, and the update policy. Labels carry the meaning,
 * so there is no helper text; placeholders show what "default" is.
 */
export function GameSettingsSection({ detail, onSettings }: { detail: GameDetail; onSettings: (patch: Partial<GameSettings>) => void }) {
  const [open, setOpen] = useState(false);
  const s = detail.settings;
  const globalPolicy = useSnap((st) => st.settings.autoUpdate);
  const globalSaves = useSnap((st) => st.settings.cloudSaves);
  const uid = useId();
  const [argsText, setArgsText] = useState(s.launchArgs);
  const [seenArgs, setSeenArgs] = useState(s.launchArgs);
  if (seenArgs !== s.launchArgs) {
    setSeenArgs(s.launchArgs);
    setArgsText(s.launchArgs);
  }
  const commitArgs = () => {
    // A pasted line break would split one flag in two.
    const v = argsText.replace(/\s*[\r\n]+\s*/g, " ").trim();
    if (v !== s.launchArgs) onSettings({ launchArgs: v });
    setArgsText(v);
  };
  const choose = async () => {
    const picked = await actions.pickFolder(s.workingDirectory, "Choose the working folder");
    if (picked) onSettings({ workingDirectory: picked });
  };
  const changed = s.launchArgs !== "" || s.executableOverride !== null || s.workingDirectory !== null || s.launchOption !== null || s.launchMode !== "auto" || s.autoUpdate !== "default";
  const options = detail.launchOptions;

  return (
    <section aria-label="Game settings">
      <div className="flex items-center justify-between gap-3">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={`${uid}-panel`}
          onClick={() => setOpen((o) => !o)}
          className="-ml-1 inline-flex h-8 items-center gap-1.5 rounded-md px-1 text-sm font-semibold text-fg transition-colors duration-150 hover:text-accent-text"
        >
          <ChevronRight size={14} className={cx("text-fg-muted transition-transform duration-150 ease-out", open && "rotate-90")} aria-hidden />
          Game settings
        </button>
        {open && changed && (
          <Button size="sm" variant="ghost" onClick={() => onSettings({ launchArgs: "", executableOverride: null, workingDirectory: null, launchOption: null, launchMode: "auto", autoUpdate: "default" })}>
            Reset
          </Button>
        )}
      </div>
      <div id={`${uid}-panel`} className="grid transition-[grid-template-rows] duration-200 ease-out" style={{ gridTemplateRows: open ? "1fr" : "0fr" }} inert={!open}>
        <div className="min-h-0 overflow-hidden">
          <div className="flex flex-col gap-3 pb-1 pt-3">
            <Field label="Launch arguments" htmlFor={`${uid}-args`}>
              <TextArea
                id={`${uid}-args`}
                rows={1}
                value={argsText}
                placeholder="-windowed -nosplash"
                onChange={(e) => setArgsText(e.target.value)}
                onBlur={commitArgs}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey) {
                    e.preventDefault();
                    commitArgs();
                  }
                }}
              />
            </Field>
            {options.length > 0 && (
              <Field label="Launch option">
                <Select
                  aria-label="Launch option"
                  value={s.launchOption ?? DEFAULT_OPTION}
                  minWidth={220}
                  onValueChange={(v) => onSettings({ launchOption: v === DEFAULT_OPTION ? null : v })}
                  options={[{ value: DEFAULT_OPTION, label: "Default" }, ...options.map((o) => ({ value: o.name, label: o.name, hint: o.args }))]}
                />
              </Field>
            )}
            <Field label="Executable" htmlFor={`${uid}-exe`}>
              <CommitField id={`${uid}-exe`} value={s.executableOverride ?? ""} placeholder={detail.executable ?? "Default"} onCommit={(v) => onSettings({ executableOverride: v || null })} />
            </Field>
            <Field label="Working folder" htmlFor={`${uid}-cwd`}>
              <div className="flex items-center gap-2">
                <CommitField id={`${uid}-cwd`} value={s.workingDirectory ?? ""} placeholder="Install folder" onCommit={(v) => onSettings({ workingDirectory: v || null })} />
                <Button variant="secondary" onClick={() => void choose()}>
                  Choose…
                </Button>
              </div>
            </Field>
            <Field label="Launch mode">
              <SegmentedControl aria-label="Launch mode" value={s.launchMode} options={MODES} onValueChange={(v) => onSettings({ launchMode: v })} className="w-60" />
            </Field>
            {detail.saves?.supported && (
              <Field label="Cloud saves">
                <div className="flex items-center gap-3">
                  <Switch aria-label="Sync cloud saves" checked={detail.saves.enabled && globalSaves} disabled={!globalSaves} onCheckedChange={(v) => onSettings({ cloudSaves: v })} />
                  {!globalSaves && <span className="text-xs text-fg-muted">Off for every game in Settings</span>}
                </div>
              </Field>
            )}
            <Field label="Updates">
              <Select
                aria-label="Updates"
                value={s.autoUpdate}
                minWidth={160}
                onValueChange={(v) => onSettings({ autoUpdate: v })}
                options={(["default", "always", "ask", "never"] as const).map((value) => ({ value, label: value === "default" ? `Default (${POLICY_LABEL[globalPolicy].toLowerCase()})` : POLICY_LABEL[value] }))}
              />
            </Field>
          </div>
        </div>
      </div>
    </section>
  );
}
