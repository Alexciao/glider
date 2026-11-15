import { useCallback, useEffect } from "react";
import { historyHasNetwork, loadHistory, readHistory, subscribeHistory } from "../../state/progress";
import { MiniSparkline } from "../../ui";

/** A compact speed trend of one job, for rows. Draws nothing until it has two samples. */
export function JobSparkline({ jobId, active = true, label }: { jobId: string; active?: boolean; label?: string }) {
  const subscribe = useCallback((n: () => void) => subscribeHistory(jobId, n), [jobId]);
  const read = useCallback(() => readHistory(jobId, historyHasNetwork(jobId) ? "network" : "disk"), [jobId]);
  useEffect(() => {
    void loadHistory(jobId);
  }, [jobId]);
  return <MiniSparkline read={read} subscribe={subscribe} active={active} label={label} />;
}
