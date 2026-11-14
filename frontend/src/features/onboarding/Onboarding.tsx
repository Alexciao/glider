import { useEffect, useRef, type ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { ArrowLeft, Check, FolderPlus, FolderSearch, KeyRound, Minus } from "lucide-react";
import type { DownloadMode, OnboardingStep } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { DOWNLOAD_MODES } from "../../state/downloadModes";
import { addLibraryFolder, makeDefaultFolder, removeLibraryFolder } from "../../state/folders";
import { plural } from "../../state/format";
import { useApp } from "../../state/store";
import { usePending, useUi } from "../../state/ui";
import { Badge, Button, Callout, ChoiceGroup, GliderMark, cx, ease, spring, type ChoiceOption } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { FolderList } from "../common/FolderList";
import { ImportList } from "../import/ImportList";
import { batchHeadline } from "../import/model";
import { useImportSession } from "../import/useImportSession";

/**
 * First run, and "Run setup again" from Settings. Welcome → Sign in → Find games → Folders → Downloads → Done.
 *
 * Every step change goes through `onboarding.setStep`, so a restart resumes on the same step (the snapshot's
 * `onboarding.step` is what is rendered, not local state). Find games, Folders and Downloads can be skipped; what was
 * skipped is remembered (`onboarding.skipped`), shown in the stepper, and listed once on the last screen.
 */

type Walk = Exclude<OnboardingStep, "welcome">;

const STEPS: { id: Walk; label: string; where: string }[] = [
  { id: "signIn", label: "Sign in", where: "Settings → Account" },
  { id: "scan", label: "Find games", where: "Settings → Epic Games Launcher" },
  { id: "folders", label: "Folders", where: "Settings → Library folders" },
  { id: "mode", label: "Downloads", where: "Settings → Downloads" },
  { id: "done", label: "Ready", where: "" },
];

const go = (step: OnboardingStep, skipped = false): Promise<void> => actions.setOnboardingStep(step, skipped);

/* ------------------------------------------------------------------ chrome */

function Stepper({ step, skipped }: { step: Walk; skipped: readonly OnboardingStep[] }) {
  const idx = STEPS.findIndex((s) => s.id === step);
  return (
    <nav aria-label="Setup progress">
      <ol className="grid grid-cols-5 gap-2">
        {STEPS.map((s, i) => {
          const current = i === idx;
          const skip = i < idx && skipped.includes(s.id);
          const done = i < idx && !skip;
          return (
            <li key={s.id} aria-current={current ? "step" : undefined}>
              <span aria-hidden className="block h-[3px] overflow-hidden rounded-full bg-surface-3">
                <span
                  className={cx("block h-full origin-left rounded-full transition-transform duration-500 ease-out", skip ? "bg-fg-disabled" : "bg-accent")}
                  style={{ transform: `scaleX(${current || done || skip ? 1 : 0})` }}
                />
              </span>
              <span className={cx("mt-2 flex items-center gap-1.5 text-xs transition-colors duration-300", current ? "font-medium text-fg" : done ? "text-fg-muted" : "text-fg-subtle")}>
                {done && <Check aria-hidden size={12} strokeWidth={2.75} className="text-accent-text" />}
                {skip && <Minus aria-hidden size={12} strokeWidth={2.75} />}
                {s.label}
                {done && <span className="sr-only">, done</span>}
                {skip && <span className="sr-only">, skipped</span>}
              </span>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** A task step: a scrolling body and a footer pinned below it (Back on the left, Skip and the primary action on the right). */
function StepLayout({ children, footer, narrow }: { children: ReactNode; footer: ReactNode; narrow?: boolean }) {
  return (
    <>
      <div className="min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
        <div className={cx("mx-auto flex min-h-full w-full flex-col px-6 pb-6 pt-4", narrow ? "max-w-[520px]" : "max-w-[680px]")}>{children}</div>
      </div>
      <footer className="relative shrink-0 border-t border-border bg-bg-elevated">
        <div className="mx-auto flex h-16 w-full max-w-[680px] items-center gap-2 px-6">{footer}</div>
      </footer>
    </>
  );
}

const Spacer = () => <span className="flex-1" />;

function BackButton({ to }: { to: OnboardingStep }) {
  return (
    <Button variant="ghost" size="lg" icon={<ArrowLeft size={15} />} onClick={() => void go(to)}>
      Back
    </Button>
  );
}

function Heading({ title, children, center }: { title: string; children?: ReactNode; center?: boolean }) {
  return (
    <div className={center ? "text-center" : undefined}>
      <h1 className="text-balance text-2xl font-semibold tracking-tight text-fg">{title}</h1>
      {children && <p className={cx("mt-2 max-w-[56ch] text-md leading-[1.5] text-fg-muted", center && "mx-auto")}>{children}</p>}
    </div>
  );
}

function OfflineNote() {
  const offline = useApp((s) => s.snapshot!.connectivity === "offline");
  if (!offline) return null;
  return <Callout tone="offline" className="mb-5" title="You're offline. Downloads and sync wait for a connection." />;
}

/* ------------------------------------------------------------------ steps */

function Welcome() {
  return (
    <div className="flex flex-col items-center py-6">
      <div className="relative mb-8 grid size-28 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-accent-soft blur-2xl" />
        <div className="relative grid size-24 place-items-center rounded-[28px] bg-surface-2 ring-1 ring-inset ring-border-strong [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-md)]">
          <GliderMark size={64} />
        </div>
      </div>
      <h1 className="text-balance text-center text-3xl font-semibold tracking-tighter text-fg max-[1100px]:text-2xl">Your whole Epic library, one launcher.</h1>
      <p className="mx-auto mt-3 max-w-[44ch] text-balance text-center text-md leading-[1.5] text-fg-muted">Sign in, bring over the games you already have, and play.</p>
      <Button size="xl" variant="primary" className="mt-9 min-w-44" onClick={() => void go("signIn")}>
        Get started
      </Button>
    </div>
  );
}

function SignIn() {
  const pendingSignIn = usePending("signIn");
  const statusSigningIn = useApp((s) => s.snapshot!.auth.status === "signingIn");
  const offline = useApp((s) => s.snapshot!.connectivity === "offline");
  const signingIn = pendingSignIn || statusSigningIn;
  const error = useUi((s) => s.errors.onboarding);
  return (
    <StepLayout
      narrow
      footer={
        <>
          <BackButton to="welcome" />
          <Spacer />
          {offline && (
            <Button
              variant="ghost"
              size="lg"
              onClick={() =>
                // Skipping Sign in and Find games records both as skipped; Folders and Downloads need no account.
                void go("scan", true).then(() => go("folders", true))
              }
            >
              Continue without signing in
            </Button>
          )}
        </>
      }
    >
      <div className="flex flex-1 flex-col items-center justify-center py-4">
        <div className="mb-6 grid size-14 place-items-center rounded-2xl bg-surface-2 text-fg ring-1 ring-inset ring-border-strong [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]">
          <KeyRound size={24} strokeWidth={1.75} />
        </div>
        <Heading center title="Sign in with Epic">
          A secure Epic window opens. Glider never sees your password.
        </Heading>
        <div className="mt-8 flex w-full max-w-sm flex-col gap-3">
          {offline && <Callout tone="offline" title="You're offline. Connect to sign in." />}
          {error && <ErrorView error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError("onboarding", undefined)} />}
          <Button
            size="xl"
            variant="primary"
            loading={signingIn}
            disabled={offline}
            onClick={() => void actions.signIn("onboarding").then((a) => a && go("scan"))}
          >
            {signingIn ? "Waiting for Epic…" : "Sign in with Epic"}
          </Button>
        </div>
      </div>
    </StepLayout>
  );
}

function Scan() {
  const session = useImportSession("onboarding");
  const launcherInstalled = useApp((s) => s.snapshot!.egl.launcherInstalled);
  const { phase, groups, summary } = session;
  const nothingToImport = groups.ready.length === 0;

  const title =
    phase === "scanning" || phase === "empty"
      ? "Finding your installed games"
      : phase === "importing" || phase === "done"
        ? batchHeadline(summary)
        : groups.ready.length > 0
          ? `Found ${plural(groups.ready.length, "game")} on this PC`
          : "Your installed games are already here";

  const next = (skipped: boolean) => {
    session.reset();
    void go("folders", skipped);
  };

  const footer = (
    <>
      <Button
        variant="ghost"
        size="lg"
        icon={<FolderSearch size={15} />}
        loading={session.locating}
        disabled={phase === "scanning" || phase === "importing" || session.starting}
        onClick={() => void session.locate()}
      >
        Locate another folder…
      </Button>
      <Spacer />
      {phase !== "importing" && phase !== "done" && (
        <Button variant="ghost" size="lg" onClick={() => next(true)}>
          Skip
        </Button>
      )}
      {phase === "list" && !nothingToImport ? (
        <Button size="lg" variant="primary" className="min-w-40" disabled={session.selectedCount === 0} loading={session.starting} onClick={() => void session.start()}>
          {session.selectedCount === 0 ? "Import" : `Import ${plural(session.selectedCount, "game")}`}
        </Button>
      ) : (
        <Button size="lg" variant="primary" className="min-w-40" loading={phase === "scanning" || phase === "importing"} onClick={() => next(false)}>
          {phase === "importing" ? "Importing…" : "Continue"}
        </Button>
      )}
    </>
  );

  return (
    <StepLayout footer={footer}>
      <div className="g-step-in py-2">
        <OfflineNote />
        <Heading title={title}>
          {phase === "importing"
            ? undefined
            : phase === "done"
              ? summary.imported + summary.needsRepair > 0
                ? "Nothing was downloaded."
                : undefined
              : "Importing links them where they are, so nothing is downloaded again."}
        </Heading>
        <div className="mt-5" aria-live="polite">
          <ImportList session={session} launcherInstalled={launcherInstalled} />
        </div>
      </div>
    </StepLayout>
  );
}

function Folders() {
  const storage = useApp((s) => s.snapshot!.storage);
  const settings = useApp((s) => s.snapshot!.settings);
  const none = storage.folders.length === 0;
  return (
    <StepLayout
      footer={
        <>
          <BackButton to="scan" />
          <Spacer />
          <Button variant="ghost" size="lg" onClick={() => void go("mode", true)}>
            Skip
          </Button>
          <Button size="lg" variant="primary" className="min-w-40" disabled={none} onClick={() => void go("mode")}>
            Continue
          </Button>
        </>
      }
    >
      <div className="g-step-in py-2">
        <OfflineNote />
        <Heading title="Where should games install?">Pick the default folder for new games.</Heading>
        <div className="mt-6">
          {!none && (
            <FolderList
              variant="cards"
              folders={storage.folders}
              drives={storage.drives}
              defaultPath={settings.defaultInstallBase}
              onDefault={(p) => void makeDefaultFolder(p)}
              onRemove={(p) => void removeLibraryFolder(settings, p)}
            />
          )}
          <button
            type="button"
            onClick={() => void addLibraryFolder(settings)}
            className={cx(
              "flex w-full items-center justify-center gap-2 rounded-xl border border-dashed border-border-strong text-sm font-medium text-fg-muted",
              "transition-[background-color,color,border-color,transform] duration-150 ease-out hover:border-fg-subtle hover:bg-surface-hover hover:text-fg active:scale-[0.995]",
              none ? "h-32 flex-col" : "mt-2 h-12",
            )}
          >
            <FolderPlus size={none ? 22 : 16} strokeWidth={none ? 1.5 : 2} />
            {none ? "Add a library folder" : "Add a folder…"}
          </button>
        </div>
      </div>
    </StepLayout>
  );
}

const MODES: ChoiceOption<DownloadMode>[] = DOWNLOAD_MODES.map((m) => ({
  value: m.value,
  title: m.label,
  description: m.summary,
  badge: m.value === "balanced" ? <Badge tone="accent">Recommended</Badge> : undefined,
}));

function Mode() {
  const mode = useApp((s) => s.snapshot!.settings.downloadMode);
  return (
    <StepLayout
      footer={
        <>
          <BackButton to="folders" />
          <Spacer />
          <Button variant="ghost" size="lg" onClick={() => void go("done", true)}>
            Skip
          </Button>
          <Button size="lg" variant="primary" className="min-w-40" onClick={() => void go("done")}>
            Continue
          </Button>
        </>
      }
    >
      <div className="g-step-in py-2">
        <OfflineNote />
        <Heading title="How should downloads behave?" />
        <ChoiceGroup<DownloadMode> aria-label="Download mode" className="mt-6" value={mode} onValueChange={(v) => void actions.updateSettings({ downloadMode: v })} options={MODES} />
      </div>
    </StepLayout>
  );
}

function Done() {
  const games = useApp((s) => s.snapshot!.library.gameCount);
  const finishing = usePending("onboarding");
  return (
    <div className="flex flex-col items-center py-6">
      <div className="relative mb-7 grid size-20 place-items-center">
        <div aria-hidden className="absolute inset-0 rounded-full bg-success-soft blur-xl" />
        <svg width="80" height="80" viewBox="0 0 80 80" fill="none" className="relative text-success" aria-hidden>
          <circle cx="40" cy="40" r="36" fill="currentColor" fillOpacity="0.14" />
          <path d="M26 41l9.5 9.5L55 30" pathLength={1} stroke="currentColor" strokeWidth="4" strokeLinecap="round" strokeLinejoin="round" className="g-draw" />
        </svg>
      </div>
      <Heading center title="You're all set">
        {games > 0 ? `${plural(games, "game")} in your library.` : "Your library appears after the first sync."}
      </Heading>
      <Button size="xl" variant="primary" className="mt-8 min-w-48" loading={finishing} onClick={() => void actions.completeOnboarding().then(() => useUi.getState().go("home"))}>
        Open Glider
      </Button>
    </div>
  );
}

/* --------------------------------------------------------------------- root */

export function Onboarding() {
  const step = useApp((s) => s.snapshot!.onboarding.step);
  const skipped = useApp((s) => s.snapshot!.onboarding.skipped);
  const authed = useApp((s) => s.snapshot!.auth.status === "signedIn");
  // A resumed flow can sit at Sign in with the account already there (the app closed right after signing in).
  const shown: OnboardingStep = step === "signIn" && authed ? "scan" : step;
  const advanced = useRef(false);
  useEffect(() => {
    if (step === "signIn" && authed && !advanced.current) {
      advanced.current = true;
      void go("scan");
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
      <div aria-hidden className="pointer-events-none absolute inset-x-0 top-0 h-[420px]" style={{ background: "radial-gradient(60% 100% at 50% 0%, var(--g-accent-soft), transparent 75%)" }} />
      {shown !== "welcome" && (
        <div className="relative shrink-0 pb-2 pt-5">
          <div className="mx-auto w-full max-w-[680px] px-6">
            <Stepper step={shown} skipped={skipped} />
          </div>
        </div>
      )}
      <AnimatePresence mode="wait" initial={false}>
        <m.div
          key={shown}
          className="relative flex min-h-0 flex-1 flex-col"
          initial={{ opacity: 0, y: 12 }}
          animate={{ opacity: 1, y: 0 }}
          exit={{ opacity: 0, y: -6, transition: { duration: 0.12, ease: ease.out } }}
          transition={spring.smooth}
        >
          {shown === "welcome" || shown === "done" ? (
            <div className="min-h-0 flex-1 overflow-y-auto [scrollbar-gutter:stable]">
              <div className={cx("mx-auto flex min-h-full w-full flex-col justify-center px-6 pb-6", shown === "welcome" ? "max-w-[760px]" : "max-w-[560px]")}>{body}</div>
            </div>
          ) : (
            body
          )}
        </m.div>
      </AnimatePresence>
    </div>
  );
}
