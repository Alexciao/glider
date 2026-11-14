import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { ArrowLeft, Check, FolderSearch, Gauge, KeyRound, ShieldCheck, Download } from "lucide-react";
import type { DownloadMode, EglInstall, OnboardingStep } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { eglInstallStatus, importableInstalls } from "../../state/derive";
import { formatBytes, prettyVersion } from "../../state/format";
import { useLibrary } from "../../state/library";
import { activeJobs, useApp } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Badge, Button, GliderMark, SegmentedControl, Skeleton, cx, spring } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PathText } from "../common/PathText";

/**
 * Interim onboarding on the v2 steps (Welcome → Sign in → Scan → Folders → Mode → Done). The step is persisted in the
 * backend (`onboarding.setStep`), so the flow resumes where it was after a restart. The real, designed version comes
 * in the onboarding phase; this one is functional and type-safe.
 */

const STEPS: { id: Exclude<OnboardingStep, "welcome">; label: string }[] = [
  { id: "signIn", label: "Sign in" },
  { id: "scan", label: "Find games" },
  { id: "folders", label: "Folders" },
  { id: "mode", label: "Downloads" },
  { id: "done", label: "Ready" },
];

function Stepper({ step }: { step: Exclude<OnboardingStep, "welcome"> }) {
  const idx = STEPS.findIndex((s) => s.id === step);
  return (
    <ol aria-label="Setup progress" className="flex items-center gap-2 text-xs">
      {STEPS.map((s, i) => {
        const done = i < idx;
        const current = i === idx;
        return (
          <li key={s.id} aria-current={current ? "step" : undefined} className="flex items-center gap-2">
            <span
              className={cx(
                "grid size-[18px] place-items-center rounded-full text-2xs font-semibold transition-colors duration-300",
                done ? "bg-success text-primary-fg" : current ? "bg-fg text-primary-fg" : "bg-surface-3 text-fg-subtle",
              )}
            >
              {done ? <Check size={11} strokeWidth={3} /> : i + 1}
            </span>
            <span className={cx("transition-colors duration-300", current ? "font-medium text-fg" : "text-fg-subtle")}>{s.label}</span>
            {i < STEPS.length - 1 && <span aria-hidden className={cx("mx-1 h-px w-5 transition-colors duration-300", done ? "bg-success/60" : "bg-border-strong")} />}
          </li>
        );
      })}
    </ol>
  );
}

function Heading({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="text-center">
      <h1 className="text-balance text-2xl font-semibold tracking-tight text-fg">{title}</h1>
      {children && <p className="mx-auto mt-2.5 max-w-[46ch] text-base leading-[1.5] text-fg-muted">{children}</p>}
    </div>
  );
}

function Point({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <li className="flex items-center gap-3 text-sm text-fg-muted">
      <span className="grid size-7 shrink-0 place-items-center rounded-lg bg-surface-2 text-fg">{icon}</span>
      <span>{children}</span>
    </li>
  );
}

const go = (step: OnboardingStep, skipped = false) => void actions.setOnboardingStep(step, skipped);

/* ------------------------------------------------------------------ screens */

function Welcome() {
  return (
    <div className="flex flex-col items-center">
      <div className="relative mb-8 grid size-28 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-accent-soft blur-2xl" />
        <div className="relative grid size-24 place-items-center rounded-[28px] bg-surface-2 ring-1 ring-inset ring-border-strong [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-md)]">
          <GliderMark size={64} />
        </div>
      </div>
      <h1 className="text-balance text-center text-3xl font-semibold tracking-tighter text-fg max-[1100px]:text-2xl">Your whole Epic library, one launcher.</h1>
      <p className="mx-auto mt-3 max-w-[44ch] text-center text-md leading-[1.5] text-fg-muted">
        Sign in with your Epic account, bring over the games already on this PC, and install, update and play without the Epic Games Launcher.
      </p>
      <ul className="mt-8 flex flex-col gap-3">
        <Point icon={<Gauge size={15} />}>Fast downloads that pause and resume</Point>
        <Point icon={<ShieldCheck size={15} />}>Works beside Epic Games Launcher, so nothing downloads twice</Point>
        <Point icon={<KeyRound size={15} />}>You sign in on Epic's page; Glider never sees your password</Point>
      </ul>
      <Button size="lg" variant="primary" className="mt-9 min-w-40" onClick={() => go("signIn")}>
        Get started
      </Button>
    </div>
  );
}

function SignIn() {
  const pendingSignIn = usePending("signIn");
  const statusSigningIn = useApp((s) => s.snapshot?.auth.status === "signingIn");
  const signingIn = pendingSignIn || statusSigningIn;
  const error = useUi((s) => s.errors.onboarding);
  return (
    <div className="flex flex-col items-center">
      <Heading title="Sign in with Epic Games">A secure Epic window opens next. Glider uses it once to get your account, and never sees your password.</Heading>
      <div className="mt-8 flex w-full max-w-sm flex-col gap-3">
        {error && <ErrorView error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}
        <Button size="lg" variant="primary" loading={signingIn} onClick={() => void actions.signIn("onboarding").then((a) => a && go("scan"))}>
          {signingIn ? "Waiting for Epic…" : "Sign in with Epic"}
        </Button>
        <Button variant="ghost" icon={<ArrowLeft size={14} />} disabled={signingIn} onClick={() => go("welcome")}>
          Back
        </Button>
      </div>
    </div>
  );
}

function InstallRow({ install, checked, onToggle }: { install: EglInstall; checked: boolean; onToggle: () => void }) {
  const importable = install.status === "importable";
  const status = eglInstallStatus(install);
  return (
    <li>
      <label className={cx("flex items-start gap-3 rounded-xl border p-3 transition-colors duration-150", importable ? "cursor-pointer border-border bg-surface-1 hover:bg-surface-2" : "border-border bg-surface-1 opacity-70")}>
        <input type="checkbox" className="mt-1 size-4 accent-[var(--g-accent)]" checked={checked} disabled={!importable} onChange={onToggle} aria-label={`Import ${install.title}`} />
        <span
          aria-hidden
          className="size-11 shrink-0 rounded-md bg-surface-3 bg-cover bg-center"
          style={{ backgroundImage: install.artUrl ? `url("${install.artUrl}")` : undefined }}
        />
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-2 text-sm font-medium text-fg">
            <span className="truncate">{install.title}</span>
            <Badge tone={status.tone}>{status.text}</Badge>
          </span>
          <span className="mt-0.5 block text-xs text-fg-muted tabular-nums">
            {install.reason ?? `${install.version ? `Version ${prettyVersion(install.version)}` : "Version unknown"}${install.sizeBytes ? `  ·  ${formatBytes(install.sizeBytes)}` : ""}`}
          </span>
          <PathText path={install.installPath} className="mt-0.5 text-xs text-fg-subtle" tailSegments={2} />
        </span>
      </label>
    </li>
  );
}

function Scan() {
  const egl = useApp((s) => s.snapshot!.egl);
  const importing = usePending("import");
  const picking = usePending("pick");
  const validating = usePending("validate");
  const error = useUi((s) => s.errors.onboarding);
  const jobs = useApp((s) => s.snapshot!.queue.jobs);
  // The library syncs first, then the scan runs; until both are in there is nothing honest to list.
  const libraryReady = useLibrary((s) => s.loaded);
  const importable = useMemo(() => importableInstalls(egl.installs), [egl.installs]);
  // Everything importable starts selected; the user's changes stick once they touch the list.
  const [touched, setTouched] = useState<ReadonlySet<string> | null>(null);
  const selected = touched ?? new Set(importable.map((i) => i.id));
  const [started, setStarted] = useState<string[] | null>(null);

  const toggle = (id: string) => {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setTouched(next);
  };

  const running = started ? jobs.filter((j) => j.kind === "import" && started.includes(j.id)) : [];
  const finished = started !== null && running.length > 0 && running.every((j) => !["queued", "running", "waiting", "paused"].includes(j.status));

  const locate = async () => {
    useUi.getState().setError("onboarding", undefined);
    const path = await actions.pickFolder(null, "Locate a game folder");
    if (!path) return;
    const v = await actions.validateFolder(path);
    if (!v) return;
    if (!v.valid || !v.gamePath) {
      useUi.getState().setError("onboarding", {
        error: { code: "invalidFolder", title: "Not a game folder", message: v.reason ?? "This folder doesn't contain a game from your library.", fix: "chooseFolder", detail: path, appName: null },
        retry: () => void locate(),
      });
      return;
    }
    const job = await actions.importFolder(v.gamePath, "auto", "onboarding");
    if (job) setStarted((s) => [...(s ?? []), job.id]);
  };

  const run = async () => {
    const r = await actions.importInstalls([...selected], "auto", "onboarding");
    if (r) setStarted((s) => [...(s ?? []), ...r.jobs.map((j) => j.id)]);
  };

  return (
    <div>
      <Heading title={importable.length > 0 ? "We found games on this PC" : "Looking for games on this PC"}>
        Importing links an install Epic Games Launcher already made, with nothing to download. Epic Games Launcher keeps working.
      </Heading>
      <div className="mt-6" aria-live="polite">
        {egl.scanning || !libraryReady || (egl.lastScan === null && egl.installs.length === 0) ? (
          <div className="flex flex-col gap-2" aria-busy="true">
            <Skeleton className="h-16 w-full rounded-xl" />
            <Skeleton className="h-16 w-full rounded-xl" />
            <Skeleton className="h-16 w-full rounded-xl" />
          </div>
        ) : egl.installs.length === 0 ? (
          <p className="rounded-xl border border-border bg-surface-1 p-5 text-center text-sm text-fg-muted">
            {egl.launcherInstalled ? "Epic Games Launcher has no installs to import." : "Epic Games Launcher isn't installed on this PC."} You can install games from your library later.
          </p>
        ) : (
          <>
            <div className="mb-2 flex items-center justify-between px-1 text-xs text-fg-muted">
              <span>
                {selected.size} of {importable.length} selected
              </span>
              <span className="flex gap-1">
                <Button size="sm" variant="ghost" onClick={() => setTouched(new Set(importable.map((i) => i.id)))}>
                  Select all
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setTouched(new Set())}>
                  Select none
                </Button>
              </span>
            </div>
            <ul className="flex max-h-[360px] flex-col gap-2 overflow-y-auto pr-1">
              {egl.installs.map((i) => (
                <InstallRow key={i.id} install={i} checked={selected.has(i.id)} onToggle={() => toggle(i.id)} />
              ))}
            </ul>
          </>
        )}
      </div>

      {running.length > 0 && (
        <ul className="mt-4 flex flex-col gap-1 rounded-xl border border-border bg-surface-1 p-3 text-xs text-fg-muted" aria-label="Import progress">
          {running.map((j) => (
            <li key={j.id} className="flex items-center justify-between gap-3">
              <span className="truncate text-fg">{j.title}</span>
              <span>{j.status === "completed" ? (j.outcome?.kind === "needsRepair" ? "Imported, needs an update" : "Imported") : j.status === "failed" ? "Failed" : "Importing…"}</span>
            </li>
          ))}
        </ul>
      )}
      {error && <ErrorView className="mt-4" error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}

      <div className="mt-6 flex flex-col items-stretch gap-2">
        {finished || (started === null && importable.length === 0) ? (
          <Button size="lg" variant="primary" onClick={() => go("folders")}>
            Continue
          </Button>
        ) : (
          <Button size="lg" variant="primary" disabled={selected.size === 0 || started !== null} loading={importing || (started !== null && !finished)} onClick={() => void run()}>
            {started !== null ? "Importing…" : `Import ${selected.size} ${selected.size === 1 ? "game" : "games"}`}
          </Button>
        )}
        <div className="flex items-center justify-center gap-1">
          <Button variant="ghost" icon={<FolderSearch size={14} />} loading={picking || validating} disabled={importing} onClick={() => void locate()}>
            Locate another folder…
          </Button>
          <Button variant="ghost" disabled={importing || (started !== null && !finished)} onClick={() => go("folders", true)}>
            Skip
          </Button>
        </div>
      </div>
    </div>
  );
}

function Folders() {
  const storage = useApp((s) => s.snapshot!.storage);
  const settings = useApp((s) => s.snapshot!.settings);
  const add = async () => {
    const p = await actions.pickFolder(settings.defaultInstallBase, "Choose a library folder");
    if (!p || settings.libraryFolders.includes(p)) return;
    await actions.updateSettings({ libraryFolders: [...settings.libraryFolders, p], defaultInstallBase: settings.defaultInstallBase ?? p });
  };
  return (
    <div>
      <Heading title="Where should games go?">New installs go to your default folder. You can add more folders on other drives and change this in Settings.</Heading>
      <ul className="mt-6 flex flex-col gap-2">
        {storage.folders.map((f) => (
          <li key={f.path} className="flex items-center gap-3 rounded-xl border border-border bg-surface-1 p-3">
            <div className="min-w-0 flex-1">
              <PathText path={f.path} className="text-sm font-medium text-fg" tailSegments={3} />
              <p className="mt-0.5 text-xs text-fg-muted tabular-nums">
                {formatBytes(f.freeBytes)} free on {f.drive} · {f.gameCount} {f.gameCount === 1 ? "game" : "games"}
              </p>
            </div>
            {f.isDefault ? (
              <Badge tone="accent">Default</Badge>
            ) : (
              <Button size="sm" variant="ghost" onClick={() => void actions.updateSettings({ defaultInstallBase: f.path })}>
                Make default
              </Button>
            )}
          </li>
        ))}
        {storage.folders.length === 0 && <li className="rounded-xl border border-border bg-surface-1 p-4 text-center text-sm text-fg-muted">No folders yet. Add one to install games.</li>}
      </ul>
      <div className="mt-6 flex flex-col items-stretch gap-2">
        <Button size="lg" variant="primary" disabled={storage.folders.length === 0} onClick={() => go("mode")}>
          Continue
        </Button>
        <div className="flex items-center justify-center gap-1">
          <Button variant="ghost" onClick={() => void add()}>
            Add a folder…
          </Button>
          <Button variant="ghost" onClick={() => go("mode", true)}>
            Skip
          </Button>
        </div>
      </div>
    </div>
  );
}

const MODES: { value: DownloadMode; label: string; text: string }[] = [
  { value: "background", label: "Background", text: "Gentle on the PC: lowest priority, fewer connections." },
  { value: "balanced", label: "Balanced", text: "Full network speed, low disk and CPU priority. Recommended." },
  { value: "full", label: "Full speed", text: "Everything uncapped. Best when you're not using the PC." },
];

function Mode() {
  const mode = useApp((s) => s.snapshot!.settings.downloadMode);
  return (
    <div>
      <Heading title="How should downloads behave?">You can change this any time, and Glider can slow down while you play.</Heading>
      <div className="mt-6 flex flex-col items-center gap-4">
        <SegmentedControl<DownloadMode> aria-label="Download mode" className="w-[360px]" value={mode} onValueChange={(v) => void actions.updateSettings({ downloadMode: v })} options={MODES.map((x) => ({ value: x.value, label: x.label }))} />
        <p className="max-w-[46ch] text-center text-sm text-fg-muted">{MODES.find((x) => x.value === mode)?.text}</p>
      </div>
      <div className="mt-8 flex flex-col items-stretch gap-2">
        <Button size="lg" variant="primary" onClick={() => go("done")}>
          Continue
        </Button>
      </div>
    </div>
  );
}

function Done() {
  const snap = useApp((s) => s.snapshot!);
  const finishing = usePending("onboarding");
  const games = snap.library.gameCount;
  const installing = activeJobs(snap.queue.jobs).length;
  return (
    <div className="flex flex-col items-center">
      <div className="relative mb-7 grid size-20 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-success-soft blur-xl" />
        <svg width="80" height="80" viewBox="0 0 80 80" fill="none" className="relative text-success" aria-hidden>
          <circle cx="40" cy="40" r="36" fill="currentColor" fillOpacity="0.14" />
          <path d="M26 41l9.5 9.5L55 30" pathLength={1} stroke="currentColor" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" className="g-draw" />
        </svg>
      </div>
      <Heading title="You're all set">
        {games > 0 ? `${games} ${games === 1 ? "game is" : "games are"} in your library.` : "Your library will appear after your first sync."}
        {installing > 0 ? ` ${installing} ${installing === 1 ? "task is" : "tasks are"} still finishing in the background.` : ""}
      </Heading>
      <Button size="lg" variant="primary" className="mt-8 min-w-44" loading={finishing} icon={<Download size={16} />} onClick={() => void actions.completeOnboarding().then(() => useUi.getState().go("home"))}>
        Open Glider
      </Button>
    </div>
  );
}

/* --------------------------------------------------------------------- root */

export function Onboarding() {
  const step = useApp((s) => s.snapshot!.onboarding.step);
  const authed = useApp((s) => s.snapshot!.auth.status === "signedIn");
  // A resumed flow can be at Sign in with the account already there (the app was closed right after signing in).
  const shown: OnboardingStep = step === "signIn" && authed ? "scan" : step;
  const advanced = useRef(false);
  useEffect(() => {
    if (step === "signIn" && authed && !advanced.current) {
      advanced.current = true;
      go("scan");
    }
  }, [step, authed]);

  const body = (() => {
    switch (shown) {
      case "welcome":
        return <Welcome />;
      case "signIn":
        return <SignIn />;
      case "scan":
        return <Scan />;
      case "folders":
        return <Folders />;
      case "mode":
        return <Mode />;
      case "done":
        return <Done />;
    }
  })();

  return (
    <div className="relative flex h-full flex-col overflow-hidden">
      <div
        aria-hidden
        className="pointer-events-none absolute inset-x-0 top-0 h-[420px]"
        style={{ background: "radial-gradient(60% 100% at 50% 0%, var(--g-accent-soft), transparent 75%)" }}
      />
      <div className="relative flex h-14 shrink-0 items-center justify-center">{shown !== "welcome" && <Stepper step={shown} />}</div>
      <div className="relative min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex min-h-full w-full max-w-[560px] flex-col justify-center px-6 pb-6 pt-2">
          <AnimatePresence mode="wait" initial={false}>
            <m.div
              key={shown}
              initial={{ opacity: 0, y: 14, filter: "blur(4px)" }}
              animate={{ opacity: 1, y: 0, filter: "blur(0px)" }}
              exit={{ opacity: 0, y: -8, filter: "blur(4px)", transition: { duration: 0.12 } }}
              transition={spring.smooth}
            >
              {body}
            </m.div>
          </AnimatePresence>
        </div>
      </div>
    </div>
  );
}
