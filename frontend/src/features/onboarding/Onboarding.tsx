import { useId, useMemo, useState, type ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { RadioGroup } from "radix-ui";
import { ArrowLeft, Check, Download, FolderSearch, Gauge, KeyRound, ShieldCheck } from "lucide-react";
import type { InstallCandidate, InstallPlan, OnboardingStep } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { candidateIssue, candidateStatus, isUsableCandidate } from "../../state/derive";
import { formatBytes, prettyVersion } from "../../state/format";
import { currentJob, useApp } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Badge, Button, GliderMark, Switch, cx, spring } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { InstallPlanner } from "../common/InstallPlanner";
import { PathText } from "../common/PathText";
import { JobLine } from "../library/LibraryParts";

type Step = "welcome" | "signIn" | "install" | "done";

const STEPS: { id: Exclude<Step, "welcome">; label: string }[] = [
  { id: "signIn", label: "Sign in" },
  { id: "install", label: "Your install" },
  { id: "done", label: "Ready" },
];

const fromBackend = (s: OnboardingStep): Step => (s === "chooseInstall" ? "install" : s);

function Stepper({ step }: { step: Exclude<Step, "welcome"> }) {
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
            {i < STEPS.length - 1 && <span aria-hidden className={cx("mx-1 h-px w-8 transition-colors duration-300", done ? "bg-success/60" : "bg-border-strong")} />}
          </li>
        );
      })}
    </ol>
  );
}

function Heading({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="text-center">
      <h1 className="text-2xl font-semibold tracking-tight text-fg">{title}</h1>
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

/* ------------------------------------------------------------------ screens */

function Welcome({ onNext }: { onNext: () => void }) {
  return (
    <div className="flex flex-col items-center">
      <div className="relative mb-8 grid size-28 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-accent-soft blur-2xl" />
        <div className="relative grid size-24 place-items-center rounded-[28px] bg-surface-2 ring-1 ring-inset ring-border-strong [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-md)]">
          <GliderMark size={64} />
        </div>
      </div>
      <h1 className="text-center text-3xl font-semibold tracking-tighter text-fg max-[1100px]:text-2xl">Install, update and play Fortnite.</h1>
      <p className="mx-auto mt-3 max-w-[44ch] text-center text-md leading-[1.5] text-fg-muted">
        Glider is a small launcher that does one job. Sign in with your Epic account and you're a minute from playing.
      </p>
      <ul className="mt-8 flex flex-col gap-3">
        <Point icon={<Gauge size={15} />}>Fast downloads that pause and resume</Point>
        <Point icon={<ShieldCheck size={15} />}>Works beside Epic Games Launcher, so nothing downloads twice</Point>
        <Point icon={<KeyRound size={15} />}>You sign in on Epic's page; Glider never sees your password</Point>
      </ul>
      <Button size="lg" variant="primary" className="mt-9 min-w-40" onClick={onNext}>
        Get started
      </Button>
    </div>
  );
}

function SignIn({ onBack }: { onBack: () => void }) {
  const pendingSignIn = usePending("signIn");
  const statusSigningIn = useApp((s) => s.snapshot?.auth.status === "signingIn");
  const signingIn = pendingSignIn || statusSigningIn;
  const error = useUi((s) => s.errors.onboarding);
  return (
    <div className="flex flex-col items-center">
      <Heading title="Sign in with Epic Games">A secure Epic window opens next. Glider uses it once to get your account, and never sees your password.</Heading>
      <div className="mt-8 flex w-full max-w-sm flex-col gap-3">
        {error && <ErrorView error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}
        <Button size="lg" variant="primary" loading={signingIn} onClick={() => void actions.signIn("onboarding")}>
          {signingIn ? "Waiting for Epic…" : "Sign in with Epic"}
        </Button>
        <Button variant="ghost" icon={<ArrowLeft size={14} />} disabled={signingIn} onClick={onBack}>
          Back
        </Button>
      </div>
    </div>
  );
}

function CandidateCard({ c, latest, selected }: { c: InstallCandidate; latest: string | null; selected: boolean }) {
  const usable = isUsableCandidate(c);
  const status = candidateStatus(c, latest);
  const issue = candidateIssue(c);
  const id = useId();
  return (
    <RadioGroup.Item
      value={c.id}
      id={id}
      disabled={!usable}
      className={cx(
        "flex w-full items-start gap-3.5 rounded-xl border p-4 text-left outline-none transition-[background-color,border-color,opacity] duration-150",
        usable ? "bg-surface-1 hover:bg-surface-2" : "border-border bg-surface-1 opacity-60",
        selected ? "border-accent/70 bg-accent-soft hover:bg-accent-soft" : "border-border",
      )}
    >
      <span
        aria-hidden
        className={cx(
          "mt-0.5 grid size-[18px] shrink-0 place-items-center rounded-full border transition-colors duration-150",
          selected ? "border-accent bg-accent" : "border-border-strong",
        )}
      >
        <span className={cx("size-1.5 rounded-full bg-accent-fg transition-transform duration-150", selected ? "scale-100" : "scale-0")} />
      </span>
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-2.5">
          <PathText path={c.installPath} className="text-sm font-semibold text-fg" tailSegments={2} />
        </span>
        <span className="mt-1 block text-xs text-fg-muted tabular-nums">
          {issue ?? `Version ${prettyVersion(c.version)}${c.sizeBytes ? `  ·  ${formatBytes(c.sizeBytes)}` : ""}`}
        </span>
        <span className="mt-2.5 flex items-center gap-2">
          <Badge tone={status.tone} dot>
            {status.text}
          </Badge>
          <Badge>{c.source === "epicGamesLauncher" ? "Epic Games Launcher" : "Located by you"}</Badge>
        </span>
      </span>
    </RadioGroup.Item>
  );
}

function Reassurance() {
  return (
    <ul className="mt-5 flex flex-col gap-2.5 rounded-xl border border-border bg-surface-1 p-4">
      <Point icon={<Download size={14} />}>Nothing is downloaded again</Point>
      <Point icon={<ShieldCheck size={14} />}>Epic Games Launcher keeps working. Glider keeps it in sync.</Point>
    </ul>
  );
}

function ChooseExisting({ candidates, onFresh }: { candidates: InstallCandidate[]; onFresh: () => void }) {
  const latest = useApp((s) => s.snapshot?.game.latestVersion ?? null);
  const adopting = usePending("adopt");
  const picking = usePending("pick");
  const validating = usePending("validate");
  const locating = picking || validating;
  const error = useUi((s) => s.errors.onboarding);
  const [manual, setManual] = useState<InstallCandidate | null>(null);
  const [checkAfter, setCheckAfter] = useState(false);
  const checkId = useId();

  const all = useMemo(() => (manual ? [manual, ...candidates.filter((c) => c.installPath !== manual.installPath)] : candidates), [manual, candidates]);
  const firstUsable = all.find(isUsableCandidate)?.id ?? null;
  const [picked, setPicked] = useState<string | null>(firstUsable);
  const selected = picked && all.some((c) => c.id === picked && isUsableCandidate(c)) ? picked : firstUsable;
  const chosen = all.find((c) => c.id === selected) ?? null;

  const locate = async () => {
    useUi.getState().setError("onboarding", undefined);
    const path = await actions.pickFolder(null, "Locate your existing Fortnite folder");
    if (!path) return;
    const v = await actions.validateFolder(path);
    if (!v) return;
    if (!v.valid || !v.gamePath) {
      useUi.getState().setError("onboarding", {
        error: {
          code: "invalidFolder",
          title: "Not a Fortnite folder",
          message: v.reason ?? "This folder doesn't contain Fortnite.",
          fix: "chooseFolder",
          detail: path,
        },
        retry: () => void locate(),
      });
      return;
    }
    const c: InstallCandidate = {
      id: `manual:${v.gamePath}`,
      source: "manual",
      installPath: v.gamePath,
      version: v.version,
      folderExists: true,
      executableFound: true,
      isStale: false,
      sizeBytes: null,
    };
    setManual(c);
    setPicked(c.id);
  };

  const use = () => {
    if (!chosen) return;
    void actions.adopt(
      chosen.source === "manual" ? { candidateId: null, path: chosen.installPath, verify: checkAfter ? "background" : "auto" } : { candidateId: chosen.id, path: null, verify: checkAfter ? "background" : "auto" },
      "onboarding",
    );
  };

  return (
    <div>
      <Heading title={all.length > 1 ? `We found ${all.length} Fortnite installs` : "We found your existing Fortnite install"}>
        Use it as it is. Nothing re-downloads, and Epic Games Launcher keeps working.
      </Heading>
      <RadioGroup.Root value={selected ?? ""} onValueChange={setPicked} aria-label="Existing Fortnite installs" className="mt-7 flex flex-col gap-2.5">
        {all.map((c) => (
          <CandidateCard key={c.id} c={c} latest={latest} selected={c.id === selected} />
        ))}
      </RadioGroup.Root>
      <Reassurance />

      {error && <ErrorView className="mt-4" error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}

      <div className="mt-4 flex items-center gap-3 px-1">
        <Switch id={checkId} checked={checkAfter} onCheckedChange={setCheckAfter} />
        <label htmlFor={checkId} className="text-xs text-fg-muted">
          Check the files in the background after linking
        </label>
      </div>

      <div className="mt-6 flex flex-col items-stretch gap-2">
        <Button size="lg" variant="primary" disabled={!chosen} loading={adopting} onClick={use}>
          {adopting ? "Linking your install…" : "Use this install"}
        </Button>
        <div className="flex items-center justify-center gap-1">
          <Button variant="ghost" icon={<FolderSearch size={14} />} loading={locating} disabled={adopting} onClick={() => void locate()}>
            Locate another folder…
          </Button>
          <Button variant="ghost" disabled={adopting} onClick={onFresh}>
            Download a fresh copy
          </Button>
        </div>
      </div>
    </div>
  );
}

function ChooseFresh({ hasExisting, onExisting }: { hasExisting: boolean; onExisting: () => void }) {
  const [plan, setPlan] = useState<InstallPlan | null>(null);
  const installing = usePending("install");
  const error = useUi((s) => s.errors.onboarding);
  const picking = usePending("pick");
  const validating = usePending("validate");
  const locating = picking || validating;

  const locate = async () => {
    const path = await actions.pickFolder(null, "Locate your existing Fortnite folder");
    if (!path) return;
    const v = await actions.validateFolder(path);
    if (!v) return;
    if (v.valid && v.gamePath) void actions.adopt({ candidateId: null, path: v.gamePath, verify: "auto" }, "onboarding");
    else
      useUi.getState().setError("onboarding", {
        error: { code: "invalidFolder", title: "Not a Fortnite folder", message: v.reason ?? "This folder doesn't contain Fortnite.", fix: "chooseFolder", detail: path },
        retry: () => void locate(),
      });
  };

  return (
    <div>
      <Heading title="Choose where to install">
        {plan ? `Fortnite needs about ${formatBytes(plan.installSizeBytes)} of space. Downloads can be paused and resumed.` : "Pick a drive with enough free space."}
      </Heading>
      <div className="mt-7">
        <InstallPlanner onPlan={setPlan} onUseExisting={(p) => void actions.adopt({ candidateId: null, path: p, verify: "auto" }, "onboarding")} />
      </div>
      {error && <ErrorView className="mt-4" error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}
      <div className="mt-6 flex flex-col items-stretch gap-2">
        <Button
          size="lg"
          variant="primary"
          disabled={!plan?.hasEnoughSpace}
          loading={installing}
          onClick={() => plan && void actions.install(plan.basePath, "onboarding")}
        >
          {installing ? "Starting…" : "Install Fortnite"}
        </Button>
        <div className="flex items-center justify-center gap-1">
          {hasExisting && (
            <Button variant="ghost" icon={<ArrowLeft size={14} />} disabled={installing} onClick={onExisting}>
              Back to found installs
            </Button>
          )}
          <Button variant="ghost" icon={<FolderSearch size={14} />} loading={locating} disabled={installing} onClick={() => void locate()}>
            I already have it. Locate folder…
          </Button>
        </div>
      </div>
    </div>
  );
}

function Done() {
  const snap = useApp((s) => s.snapshot!);
  const finishing = usePending("onboarding");
  const job = currentJob(snap.jobs);
  const { game } = snap;
  const installing = job && (job.kind === "install" || job.kind === "import");
  const detail = installing
    ? "Fortnite is installing in the background. You can start exploring while it downloads."
    : game.updateAvailable
      ? `Fortnite is linked. An update${game.downloadSizeBytes ? ` (${formatBytes(game.downloadSizeBytes)})` : ""} is ready whenever you are.`
      : "Fortnite is ready to play.";
  return (
    <div className="flex flex-col items-center">
      <div className="relative mb-7 grid size-20 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-success-soft blur-xl" />
        <svg width="80" height="80" viewBox="0 0 80 80" fill="none" className="relative text-success" aria-hidden>
          <circle cx="40" cy="40" r="36" fill="currentColor" fillOpacity="0.14" />
          <path d="M26 41l9.5 9.5L55 30" pathLength={1} stroke="currentColor" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" className="g-draw" />
        </svg>
      </div>
      <Heading title="You're all set">{detail}</Heading>
      {installing && job && (
        <div className="mt-6 w-full max-w-sm">
          <JobLine job={job} />
        </div>
      )}
      <Button size="lg" variant="primary" className="mt-8 min-w-44" loading={finishing} onClick={() => void actions.completeOnboarding().then(() => useUi.getState().go("library"))}>
        Open Library
      </Button>
    </div>
  );
}

/* --------------------------------------------------------------------- root */

export function Onboarding() {
  const snap = useApp((s) => s.snapshot!);
  // The welcome screen is the only purely local step; everything after it follows real state.
  const [started, setStarted] = useState(() => fromBackend(snap.onboarding.step) !== "welcome");
  const [fresh, setFresh] = useState(false);

  const authed = snap.auth.status === "signedIn";
  const job = currentJob(snap.jobs);
  const hasGame = snap.game.installed !== null || (job !== undefined && (job.kind === "install" || job.kind === "import"));
  const candidates = snap.egl.candidates;
  const hasExisting = candidates.some(isUsableCandidate);

  // Derive the step from real state: signing in advances, a linked/installing game finishes.
  const step: Step = !started ? "welcome" : hasGame ? "done" : !authed ? "signIn" : "install";

  const body = (() => {
    switch (step) {
      case "welcome":
        return <Welcome onNext={() => setStarted(true)} />;
      case "signIn":
        return <SignIn onBack={() => setStarted(false)} />;
      case "install":
        return hasExisting && !fresh ? <ChooseExisting candidates={candidates} onFresh={() => setFresh(true)} /> : <ChooseFresh hasExisting={hasExisting} onExisting={() => setFresh(false)} />;
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
      <div className="relative flex h-14 shrink-0 items-center justify-center">{step !== "welcome" && <Stepper step={step} />}</div>
      <div className="relative min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex min-h-full w-full max-w-[540px] flex-col justify-center px-6 pb-12 pt-2">
          <AnimatePresence mode="wait" initial={false}>
            <m.div
              key={`${step}-${step === "install" ? (hasExisting && !fresh ? "existing" : "fresh") : ""}`}
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
