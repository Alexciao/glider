import type { AppSnapshot, GameDetail, GameSummary, ShortcutState } from "../../ipc/contract.gen";
import type { GameAction } from "./actionModel";
import { GameActions } from "./GameActions";
import { GameStatus } from "./GameStatus";
import { HeroArt } from "./HeroArt";

/**
 * The hero card: key art, the title, one status line, the action dock and the running job's bar. Everything inside is
 * scoped to the dark token set, because AA text on any picture only works against a dark, art-independent scrim.
 * (`data-hero` is what the legibility spec measures.)
 */
export function Hero({
  snap,
  game,
  detail,
  action,
  moment,
  onShortcuts,
}: {
  snap: AppSnapshot;
  game: GameSummary;
  detail: GameDetail | null;
  action: GameAction;
  moment: boolean;
  onShortcuts?: (state: ShortcutState) => void;
}) {
  const logo = game.art.logo;
  const job = action.job;
  const showBar = !!job && (action.kind === "pause" || action.kind === "resume" || action.kind === "cancel") && job.status !== "queued" && job.status !== "waiting";
  return (
    <header data-hero data-theme="dark" className="relative isolate text-fg">
      <HeroArt appName={game.appName} art={game.art} />
      <div className="relative z-10 flex min-h-[clamp(288px,42vh,400px)] flex-col justify-end p-8 max-[1100px]:p-6">
        <div className="g-rise" style={{ ["--g-i" as string]: 0 }}>
          {logo ? (
            <>
              <h1 className="sr-only">{game.title}</h1>
              <img src={logo} alt="" draggable={false} className="mb-3 max-h-24 w-auto max-w-[min(420px,100%)] object-contain object-left [filter:drop-shadow(0_2px_16px_oklch(0_0_0/0.5))]" />
            </>
          ) : (
            <h1
              title={game.title}
              className="line-clamp-2 max-w-[24ch] text-balance text-3xl font-semibold tracking-tighter text-fg [overflow-wrap:anywhere] [text-shadow:0_2px_24px_oklch(0_0_0/0.4)] max-[1100px]:max-w-[28ch] max-[1100px]:text-2xl"
            >
              {game.title}
            </h1>
          )}
          {(!action.status.quiet || moment) && <GameStatus status={action.status} moment={moment} className="mt-2" />}
        </div>

        <div data-hero-actions className="g-rise mt-6 flex items-center gap-2" style={{ ["--g-i" as string]: 1 }}>
          <GameActions snap={snap} game={game} detail={detail} action={action} moment={moment} showBar={showBar} onShortcuts={onShortcuts} />
        </div>
      </div>
    </header>
  );
}
