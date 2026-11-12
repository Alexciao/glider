import { useState } from "react";
import { LogIn, LogOut, Settings as SettingsIcon } from "lucide-react";
import { rpc } from "../ipc/rpc";
import { useApp, useSnap, type MaxButtonState } from "../state/store";
import { useUi } from "../state/ui";
import { actions } from "../state/actions";
import {
  Avatar,
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
  GliderWordmark,
  cx,
} from "../ui";
import { ActivityPill } from "./ActivityPill";

/** Windows caption glyphs, 10×10 hairlines like Segoe Fluent Icons. */
const glyph = {
  minimize: <path d="M0 5.5h10" />,
  maximize: <rect x="0.5" y="0.5" width="9" height="9" rx="1.5" />,
  restore: (
    <>
      <path d="M2.5 2.5v-.5A1.5 1.5 0 0 1 4 .5h4.5A1.5 1.5 0 0 1 10 2v4.5A1.5 1.5 0 0 1 8.5 8H8" />
      <rect x="0.5" y="2.5" width="7" height="7" rx="1.5" />
    </>
  ),
  close: <path d="M0.5 0.5l9 9M9.5 0.5l-9 9" />,
};

function CaptionButton({
  kind,
  label,
  onClick,
  dim,
  forced,
}: {
  kind: keyof typeof glyph;
  label: string;
  onClick: () => void;
  dim: boolean;
  /** Host-reported pointer state (the native overlay hides the real one from CSS). */
  forced?: MaxButtonState;
}) {
  return (
    <button
      type="button"
      aria-label={label}
      data-caption={kind === "restore" ? "maximize" : kind}
      data-forced={forced && forced !== "idle" ? forced : undefined}
      onClick={onClick}
      // 46×40 exactly: the host parks a native overlay over the maximize button for Snap Layouts.
      className={cx(
        "app-no-drag grid h-10 w-[46px] place-items-center transition-colors duration-100",
        "focus-visible:outline-offset-[-3px]",
        kind === "close"
          ? "hover:bg-[#c42b1c] hover:text-white active:bg-[#b02a1d] active:text-white"
          : "hover:bg-surface-hover active:bg-surface-active data-[forced=hover]:bg-surface-hover data-[forced=down]:bg-surface-active",
        dim ? "text-fg-disabled" : "text-fg",
      )}
    >
      <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1" aria-hidden>
        {glyph[kind]}
      </svg>
    </button>
  );
}

export function CaptionButtons() {
  const { maximized, focused } = useApp((s) => s.window);
  const maxButton = useApp((s) => s.maxButton);
  const call = (m: "window.minimize" | "window.toggleMaximize" | "window.close") => () => void rpc().call(m).catch(() => undefined);
  return (
    <div className="flex h-10">
      <CaptionButton kind="minimize" label="Minimize" onClick={call("window.minimize")} dim={!focused} />
      <CaptionButton kind={maximized ? "restore" : "maximize"} label={maximized ? "Restore" : "Maximize"} onClick={call("window.toggleMaximize")} dim={!focused} forced={maxButton} />
      <CaptionButton kind="close" label="Close" onClick={call("window.close")} dim={!focused} />
    </div>
  );
}

function AccountMenu() {
  const auth = useSnap((s) => s.auth);
  const go = useUi((s) => s.go);
  const [open, setOpen] = useState(false);

  if (auth.status !== "signedIn" && auth.status !== "expired") {
    return (
      <Button size="sm" variant="ghost" icon={<LogIn size={14} />} className="app-no-drag" onClick={() => void actions.signIn(useUi.getState().page === "library" ? "hero" : "settings")}>
        Sign in
      </Button>
    );
  }
  const name = auth.account?.displayName ?? "Account";
  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={`Account: ${name}`}
          data-open={open || undefined}
          className="app-no-drag relative grid size-7 place-items-center rounded-full transition-transform duration-150 ease-out active:scale-95"
        >
          <Avatar name={name} seed={auth.account?.accountId} src={auth.account?.avatarUrl} size="sm" />
          {auth.status === "expired" && <span className="absolute right-0.5 top-0.5 size-2 rounded-full bg-warning ring-2 ring-[var(--g-bg-elevated)]" />}
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="w-64">
        <div className="flex items-center gap-3 px-2.5 pb-2.5 pt-2">
          <Avatar name={name} seed={auth.account?.accountId} src={auth.account?.avatarUrl} size="lg" />
          <div className="min-w-0">
            <p className="truncate text-sm font-medium text-fg">{name}</p>
            <p className="truncate text-xs text-fg-muted">{auth.status === "expired" ? "Session expired" : "Epic Games account"}</p>
          </div>
        </div>
        <DropdownMenuSeparator />
        {auth.status === "expired" && (
          <DropdownMenuItem icon={<LogIn size={15} />} onSelect={() => void actions.signIn("hero")}>
            Sign in again
          </DropdownMenuItem>
        )}
        <DropdownMenuItem icon={<SettingsIcon size={15} />} shortcut="Ctrl+," onSelect={() => go("settings")}>
          Settings
        </DropdownMenuItem>
        <DropdownMenuLabel>Signing out keeps your install and settings.</DropdownMenuLabel>
        <DropdownMenuItem icon={<LogOut size={15} />} onSelect={() => void actions.signOut()}>
          Sign out
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function TitleBar({ minimal = false }: { minimal?: boolean }) {
  const focused = useApp((s) => s.window.focused);
  return (
    <header className="app-drag relative z-30 flex h-10 shrink-0 select-none items-center">
      <div className={cx("flex items-center pl-[18px] transition-opacity duration-150", !focused && "opacity-60")}>
        <GliderWordmark height={26} />
      </div>
      {!minimal && (
        <div className="pointer-events-none absolute inset-x-0 top-0 flex h-10 items-center justify-center">
          <ActivityPill />
        </div>
      )}
      <div className="ml-auto flex h-10 items-center">
        {!minimal && (
          <div className="mr-2 flex items-center">
            <AccountMenu />
          </div>
        )}
        <CaptionButtons />
      </div>
    </header>
  );
}
