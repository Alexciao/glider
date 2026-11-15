import { useCallback, useEffect, useRef, useState } from "react";
import { ArrowLeft, ExternalLink, Lock, RefreshCw, Store, WifiOff } from "lucide-react";
import type { GliderError, StoreBounds, StoreTarget } from "../../ipc/contract.gen";
import { isHosted } from "../../ipc/client";
import { rpc } from "../../ipc/rpc";
import { formatRoute, useRoute } from "../../state/router";
import { actions, toGliderError } from "../../state/actions";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, EmptyState, IconButton, Spinner, cx } from "../../ui";
import { useNativeBounds } from "./useNativeBounds";
import { useOverlayWatcher, useStoreView } from "./useStoreView";

/**
 * The Epic Games Store. The page itself is a native WebView2 the host lays over the placeholder below; this component only owns
 * the placeholder (it reports its rectangle), the slim toolbar, and the states the native view can't draw (opening, signed out,
 * offline, error). The UI never names a URL: the route carries a typed target and Core builds the address.
 */
export function StorePage() {
  const route = useRoute();
  const target: StoreTarget | null = route.page === "store" ? (route.target ?? null) : null;
  const targetKey = route.page === "store" ? formatRoute(route) : "store";
  const view = useStoreView();
  const offline = useSnap((s) => s.connectivity === "offline");
  const signingIn = usePending("signIn");
  const hostRef = useRef<HTMLDivElement>(null);
  const [failure, setFailure] = useState<GliderError | null>(null);
  const shown = useRef(false);

  const { measure } = useNativeBounds(hostRef, (b: StoreBounds) => {
    if (shown.current) void rpc().call("store.setBounds", b).catch(() => {});
  });

  // Enter / retarget: show the native view over the placeholder. Leave: hide it (the host suspends and eventually closes it).
  const targetRef = useRef(target);
  useEffect(() => {
    targetRef.current = target;
  });
  const show = useCallback(() => {
    const bounds = measure();
    if (!bounds) return;
    rpc()
      .call("store.show", { target: targetRef.current, bounds })
      .then(() => {
        shown.current = true;
        setFailure(null);
        const latest = measure();
        if (latest && (latest.x !== bounds.x || latest.y !== bounds.y || latest.width !== bounds.width || latest.height !== bounds.height)) {
          void rpc().call("store.setBounds", latest).catch(() => {});
        }
      })
      .catch((e: unknown) => setFailure(toGliderError(e)));
  }, [measure]);

  useEffect(() => {
    show();
    // Re-run only when the destination changes (UI-1 links "Free this week" items here with a product target).
  }, [targetKey, show]);

  useEffect(
    () => () => {
      shown.current = false;
      try {
        void rpc().call("store.hide").catch(() => {});
      } catch {
        // the connection is already gone (app teardown)
      }
    },
    [],
  );

  const setCovered = useCallback((covered: boolean) => {
    if (shown.current) void rpc().call("store.setCovered", { covered }).catch(() => {});
  }, []);
  useOverlayWatcher(true, setCovered);

  const phase = view?.phase ?? "idle";
  const nativeOnScreen = view?.visible === true && (phase === "ready" || phase === "loading");
  const error = failure ?? view?.error ?? null;
  const opening = !error && phase !== "signedOut" && !nativeOnScreen && !view?.covered;

  const openInBrowser = () => void rpc().call("store.openInBrowser").catch(() => {});
  const reload = () => void rpc().call("store.reload").catch(() => {});

  return (
    <div className="flex h-full flex-col">
      <div className="flex h-10 shrink-0 items-center gap-1 border-b px-2">
        <IconButton label="Back" size="sm" disabled={!view?.canGoBack} onClick={() => void rpc().call("store.back").catch(() => {})}>
          <ArrowLeft size={16} />
        </IconButton>
        <IconButton label="Reload" size="sm" onClick={reload}>
          {view?.loading ? <Spinner size={14} /> : <RefreshCw size={14} />}
        </IconButton>
        <div className="ml-2 flex min-w-0 items-center gap-1.5 text-xs text-fg-muted">
          {view?.host && (
            <>
              <Lock size={12} className={cx(view.secure ? "text-fg-subtle" : "text-warning-text")} aria-label={view.secure ? "Secure connection" : "Not secure"} />
              <span className="truncate" title={view.url ?? undefined}>
                {view.host}
              </span>
            </>
          )}
        </div>
        <div className="flex-1" />
        {/* The fallbacks below carry their own "Open in browser" (signed out, a failed load): never two. Offline there is no browser to open it in either. */}
        {!error && phase !== "signedOut" && (
          <IconButton label="Open in browser" size="sm" onClick={openInBrowser}>
            <ExternalLink size={14} />
          </IconButton>
        )}
      </div>

      <div ref={hostRef} data-store-host className="relative min-h-0 flex-1 bg-bg-elevated">
        {view?.covered && view.coverUrl && (
          <img src={view.coverUrl} alt="" className="absolute inset-0 size-full object-cover object-top-left opacity-60" draggable={false} />
        )}
        {!isHosted() && nativeOnScreen && (
          <div className="absolute inset-0 grid place-items-center text-sm text-fg-subtle">The Epic Games Store appears here in the Glider app.</div>
        )}
        {opening && (
          <div className="absolute inset-0 grid place-items-center" role="status" aria-live="polite">
            <div className="flex items-center gap-2.5 text-sm text-fg-muted">
              <Spinner size={16} />
              Opening the Epic Games Store…
            </div>
          </div>
        )}
        {error ? (
          <Fallback
            icon={offline ? <WifiOff size={20} /> : <Store size={20} />}
            title={offline ? "You're offline" : "The Store didn't load"}
            description={offline ? "The Store needs an internet connection. Your library still works." : error.message}
            primary={{ label: "Try again", onPress: show }}
            // The browser is offline too: no way out there.
            secondary={offline ? undefined : { label: "Open in browser", onPress: openInBrowser }}
          />
        ) : (
          phase === "signedOut" && (
            <Fallback
              icon={<Store size={20} />}
              title="Sign in to use the Store"
              primary={{ label: "Sign in", onPress: () => void actions.signIn("settings").then((a) => a && reload()), loading: signingIn }}
              secondary={{ label: "Open in browser", onPress: openInBrowser }}
            />
          )
        )}
      </div>
    </div>
  );
}

interface FallbackAction {
  label: string;
  onPress: () => void;
  loading?: boolean;
}

/** What the native view can't draw: a title, at most one sentence, the one thing to do, and the one other way. */
function Fallback({ icon, title, description, primary, secondary }: { icon: React.ReactNode; title: string; description?: string; primary: FallbackAction; secondary?: FallbackAction }) {
  return (
    <div className="absolute inset-0 grid place-items-center bg-bg-elevated">
      <EmptyState
        icon={icon}
        title={title}
        description={description}
        action={
          <div className="flex items-center gap-2">
            <Button variant="primary" loading={primary.loading} onClick={primary.onPress}>
              {primary.label}
            </Button>
            {secondary && (
              <Button variant="ghost" onClick={secondary.onPress}>
                {secondary.label}
              </Button>
            )}
          </div>
        }
      />
    </div>
  );
}
