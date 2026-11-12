import { TriangleAlert } from "lucide-react";
import { Button, Skeleton } from "../ui";
import { TitleBar } from "./TitleBar";

/** Shown until the first snapshot arrives: the real shell's silhouette, so the reveal doesn't jump. */
export function Splash() {
  return (
    <div className="flex h-full flex-col" aria-busy="true" aria-label="Loading Glider">
      <TitleBar minimal />
      <div className="flex min-h-0 flex-1">
        <div className="flex w-16 shrink-0 flex-col items-center gap-2 pt-2">
          <Skeleton className="size-[52px] rounded-lg" />
          <Skeleton className="size-[52px] rounded-lg" />
        </div>
        <main className="relative flex-1 overflow-hidden rounded-tl-xl border-l border-t bg-bg-elevated">
          <div className="absolute inset-x-12 bottom-12 flex flex-col gap-4">
            <Skeleton className="h-10 w-64" />
            <Skeleton className="h-4 w-40" />
            <Skeleton className="mt-3 h-12 w-36 rounded-[14px]" />
          </div>
        </main>
      </div>
    </div>
  );
}

export function BootFailed({ onRetry }: { onRetry: () => void }) {
  return (
    <div className="flex h-full flex-col">
      <TitleBar minimal />
      <main className="grid flex-1 place-items-center rounded-t-xl border-t bg-bg-elevated">
        <div className="flex max-w-sm flex-col items-center text-center">
          <div className="mb-4 grid size-11 place-items-center rounded-xl bg-danger-soft text-danger-text">
            <TriangleAlert size={20} />
          </div>
          <h1 className="text-md font-semibold text-fg">Glider couldn't start</h1>
          <p className="mt-1.5 text-sm leading-[1.5] text-fg-muted">The app couldn't reach its background service. Try again; if it keeps happening, reinstall Glider.</p>
          <Button className="mt-5" variant="primary" onClick={onRetry}>
            Try again
          </Button>
        </div>
      </main>
    </div>
  );
}
