import { useState } from "react";
import { Info } from "lucide-react";
import type { JobProgress } from "../../ipc/contract.gen";
import { goToSetting } from "../../state/settingsFocus";
import { explainSpeed, type SpeedContext, type SpeedStatus as Status } from "../../state/transfer";
import { Button, Modal, Tooltip, cx } from "../../ui";

/**
 * What is holding the download's speed back, at the right end of the readout row. A slow download (the drive, the
 * processor, the server) is amber; a limit the user chose (speed limit, Background mode, throttled while a game runs) is
 * muted. Hover or focus gives the one-line reason; a click opens the full one with the measured numbers and what to do.
 */
export function SpeedStatus({ status, progress, context }: { status: Status; progress: JobProgress | null; context: SpeedContext }) {
  const [open, setOpen] = useState(false);
  const slow = status.kind === "slow";
  return (
    <>
      <Tooltip content={status.reason} side="top" align="end">
        <button
          type="button"
          aria-haspopup="dialog"
          onClick={() => setOpen(true)}
          className={cx(
            "-mr-2 inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs font-medium transition-colors duration-150",
            slow ? "text-warning-text hover:bg-warning-soft" : "text-fg-muted hover:bg-surface-hover hover:text-fg",
          )}
        >
          <Info size={14} aria-hidden />
          {status.label}
        </button>
      </Tooltip>
      <SpeedDialog open={open} onOpenChange={setOpen} status={status} progress={progress} context={context} />
    </>
  );
}

function SpeedDialog({ open, onOpenChange, status, progress, context }: { open: boolean; onOpenChange: (o: boolean) => void; status: Status; progress: JobProgress | null; context: SpeedContext }) {
  const why = explainSpeed(status, progress, context);
  const link = why.link;
  return (
    <Modal
      open={open}
      onOpenChange={onOpenChange}
      title={why.title}
      description={why.lead}
      footer={
        <>
          <Button variant={link ? "ghost" : "secondary"} onClick={() => onOpenChange(false)}>
            Close
          </Button>
          {link && (
            <Button
              variant="primary"
              onClick={() => {
                onOpenChange(false);
                goToSetting(link.focus);
              }}
            >
              {link.label}
            </Button>
          )}
        </>
      }
    >
      <div className="flex flex-col gap-4 pb-1 text-sm text-fg-muted">
        {why.facts.length > 0 && (
          <section aria-label="Measured">
            <h3 className="mb-1.5 text-xs font-medium text-fg-subtle">Measured</h3>
            <ul className="flex flex-col gap-1 tabular-nums">
              {why.facts.map((f) => (
                <li key={f} className="text-fg">
                  {f}
                </li>
              ))}
            </ul>
          </section>
        )}
        <section aria-label="What you can do">
          <h3 className="mb-1.5 text-xs font-medium text-fg-subtle">What you can do</h3>
          <ul className="flex flex-col gap-1.5">
            {why.advice.map((a) => (
              <li key={a} className="leading-[1.45]">
                {a}
              </li>
            ))}
          </ul>
        </section>
      </div>
    </Modal>
  );
}
