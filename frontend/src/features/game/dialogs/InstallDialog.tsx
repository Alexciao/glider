import { useEffect, useRef, useState } from "react";
import { Checkbox } from "radix-ui";
import { Check, PackageCheck } from "lucide-react";
import type { InstallPlan } from "../../../ipc/contract.gen";
import { actions, pendingKey } from "../../../state/actions";
import { driveOf, formatBytes } from "../../../state/format";
import { useGame } from "../../../state/library";
import { usePending, useUi } from "../../../state/ui";
import { Button, Modal, cx } from "../../../ui";
import { ErrorView } from "../../common/ErrorView";
import { NoticeCard } from "../Notices";
import { FolderChooser, SpaceMeter, useFolderOptions } from "./FolderChooser";

/** One selectable line: a checkbox, a name, a size. */
function ChoiceRow({ label, size, checked, disabled, onChange }: { label: string; size: number | null; checked: boolean; disabled?: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className={cx("flex items-center gap-3 rounded-lg px-2 py-1.5 transition-colors duration-150", disabled ? "cursor-default" : "hover:bg-surface-hover", disabled && "opacity-45")}>
      <Checkbox.Root
        aria-label={label}
        checked={checked}
        disabled={disabled}
        onCheckedChange={(v) => onChange(v === true)}
        className={cx("grid size-4 shrink-0 place-items-center rounded-[5px] border transition-colors duration-150", checked ? "border-accent bg-accent text-accent-fg" : "border-border-strong bg-surface-2")}
      >
        <Checkbox.Indicator>
          <Check size={12} strokeWidth={3} />
        </Checkbox.Indicator>
      </Checkbox.Root>
      <span className="min-w-0 flex-1">
        <span className="block truncate text-sm font-medium text-fg">{label}</span>
      </span>
      <span className="shrink-0 text-xs text-fg-muted tabular-nums">{size != null ? formatBytes(size) : ""}</span>
    </label>
  );
}

function Group({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section aria-label={title}>
      <h3 className="mb-1 text-sm font-semibold text-fg">{title}</h3>
      <div className="-mx-2 flex flex-col">{children}</div>
    </section>
  );
}

const sameFolder = (a: string, b: string) => a.replace(/[\\/]+$/, "").toLowerCase() === b.replace(/[\\/]+$/, "").toLowerCase();

/**
 * Install: where, what and whether it fits, planned live by Core. Every change (folder, DLC) re-plans, so
 * the totals are always Core's numbers, never the UI's arithmetic; the previous numbers stay (dimmed) until the new ones land.
 * Components aren't offered: Core plans none and can't change them yet ("not available yet").
 */
export function InstallDialog({ appName, open }: { appName: string; open: boolean }) {
  const close = useUi((s) => s.closeDialog);
  const error = useUi((s) => s.errors.install);
  const game = useGame(appName);
  const title = game?.title ?? "game";
  const starting = usePending(pendingKey("install", appName));

  const [base, setBase] = useState<string | null>(null);
  const [dlc, setDlc] = useState<string[] | null>(null);
  const [answer, setAnswer] = useState<{ key: string; plan: InstallPlan | null } | null>(null);
  const requested = useRef(false);

  const key = JSON.stringify([base, dlc]);
  useEffect(() => {
    let alive = true;
    // The first plan goes out at once; later ones wait a beat so a run of ticks makes one request.
    const t = setTimeout(
      () => {
        void actions.planInstall(appName, base, null, dlc).then((p) => {
          if (alive) setAnswer({ key, plan: p ?? null });
        });
      },
      requested.current ? 140 : 0,
    );
    requested.current = true;
    return () => {
      alive = false;
      clearTimeout(t);
    };
    // `key` encodes base/dlc.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appName, key]);
  useEffect(() => () => useUi.getState().setError("install", undefined), []);

  const plan = answer?.plan ?? null;
  const planning = answer?.key !== key;
  const chosen = base ?? plan?.basePath ?? null;
  const options = useFolderOptions(base ? { path: base, freeBytes: plan && sameFolder(plan.basePath, base) && !planning ? plan.freeBytes : null } : null);
  // The listed folders report their own free space; the plan's drive figure is authoritative for the chosen one.
  const optionsWithPlan = options.map((o) => (plan && chosen && sameFolder(o.path, plan.basePath) && !planning ? { ...o, freeBytes: plan.freeBytes } : o));

  const isDlc = (d: InstallPlan["dlc"][number]) => (dlc ? dlc.includes(d.appName) : d.selected);
  const toggleDlc = (id: string, on: boolean) => {
    if (!plan) return;
    setDlc(plan.dlc.filter((d) => (d.appName === id ? on : isDlc(d))).map((d) => d.appName));
  };
  const chooseOther = async () => {
    const picked = await actions.pickFolder(chosen, `Choose where to install ${title}`);
    if (picked) setBase(picked);
  };

  const ready = !!plan && !planning;
  const fits = plan?.hasEnoughSpace ?? true;
  const drive = driveOf(plan?.basePath);
  const needed = plan?.installSizeBytes ?? null;
  const free = plan?.freeBytes ?? null;
  const tight = !!plan && !plan.hasEnoughSpace && needed != null && free != null && free >= needed;
  const verdict = !plan
    ? "Checking free space…"
    : fits
      ? `Fits, with ${formatBytes((free ?? 0) - (needed ?? 0))} to spare.`
      : tight
        ? "Too tight: Glider keeps a little space free so Windows keeps working. Choose another folder or free up space."
        : `Needs ${formatBytes((needed ?? 0) - (free ?? 0))} more on ${drive || "this drive"}. Choose another folder or free up space.`;

  const installable = plan?.dlc.filter((d) => d.installable) ?? [];
  const pendingPrereqs = plan?.prerequisites.filter((p) => !p.installed) ?? [];

  const go = () => useUi.getState().go("downloads");
  const install = async () => {
    if (!plan) return;
    const job = await actions.install(appName, plan.basePath, "install", { dlc });
    if (job) {
      close();
      go();
    }
  };
  const importExisting = async () => {
    if (!plan) return;
    const job = await actions.importFolder(plan.gamePath, "auto", "install");
    if (job) {
      close();
      go();
    }
  };

  const existing = plan?.existingInstallFound === true;
  const note = [
    plan && plan.thirdParty !== "none" ? `Plays through ${plan.thirdPartyName ?? "another launcher"}. Activate it on first Play.` : null,
    pendingPrereqs.length > 0 ? `Also sets up ${pendingPrereqs.map((p) => p.name).join(", ")} on first Play.` : null,
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && !starting && close()}
      size="lg"
      title={`Install ${title}`}
      footer={
        <>
          <Button variant="ghost" onClick={close} disabled={starting}>
            Cancel
          </Button>
          {existing ? (
            <>
              <Button variant="secondary" loading={starting} disabled={!ready || !fits} onClick={() => void install()}>
                Install anyway
              </Button>
              <Button variant="primary" loading={starting} disabled={!ready} onClick={() => void importExisting()}>
                Import
              </Button>
            </>
          ) : (
            <Button variant="primary" loading={starting} disabled={!ready || !fits} onClick={() => void install()}>
              Install
            </Button>
          )}
        </>
      }
    >
      <div className="flex flex-col gap-5 pb-2">
        {existing && (
          <NoticeCard tone="info" icon={<PackageCheck size={16} />} title="Already in this folder">
            Import it instead of downloading again. Glider checks the files first.
          </NoticeCard>
        )}

        <SpaceMeter
          loading={planning}
          fits={fits}
          neededBytes={needed}
          freeBytes={free}
          verdict={verdict}
          stats={[
            { label: "Download", value: plan ? formatBytes(plan.downloadSizeBytes) : null },
            { label: "On disk", value: plan ? formatBytes(plan.installSizeBytes) : null },
          ]}
        />

        <section aria-label="Install to">
          <FolderChooser label="Install to" options={optionsWithPlan} value={chosen} neededBytes={needed} onChange={(p) => setBase(p)} onChooseOther={() => void chooseOther()} disabled={starting} />
        </section>

        {installable.length > 0 && (
          <Group title="DLC">
            {installable.map((d) => (
              <ChoiceRow key={d.appName} label={d.title} size={d.installSizeBytes} checked={isDlc(d)} disabled={starting} onChange={(v) => toggleDlc(d.appName, v)} />
            ))}
          </Group>
        )}

        {note && <p className="text-xs leading-[1.5] text-fg-muted">{note}</p>}

        {error && <ErrorView error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("install", undefined)} />}
      </div>
    </Modal>
  );
}
