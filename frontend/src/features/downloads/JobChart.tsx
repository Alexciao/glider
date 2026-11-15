import { useCallback, useEffect } from "react";
import { formatClock, formatSpeed } from "../../state/format";
import { loadHistory, readChart, subscribeHistory } from "../../state/progress";
import { speedText } from "../../state/transfer";
import { ThroughputChart, type ChartSeries } from "../../ui";

const NET: ChartSeries = { key: "network", label: "Download", color: "var(--g-chart-net)", area: true };
const DISK: ChartSeries = { key: "disk", label: "Disk write", color: "var(--g-chart-disk)", area: true };

/**
 * The job's speed over its lifetime, one series: the download while downloading or repairing, the drive's writes while installing.
 * Drawn against the connection's ceiling when there is one (dashed), so a drive-bound line sits low instead of filling the plot.
 * It lives in Details, so it is only mounted (and only subscribed to the feed) while Details is open.
 */
export function JobChart({ jobId, kind, ceiling, active }: { jobId: string; kind: "network" | "disk"; ceiling: number | null; active: boolean }) {
  useEffect(() => {
    void loadHistory(jobId);
  }, [jobId]);
  const withNetwork = kind === "network";
  const subscribe = useCallback((n: () => void) => subscribeHistory(jobId, n), [jobId]);
  const read = useCallback(() => {
    const d = readChart(jobId, withNetwork);
    return { t: d.t, values: [d.values[0]!] };
  }, [jobId, withNetwork]);
  const markers = useCallback(() => null, []);
  const label = speedText(ceiling);
  return (
    <ThroughputChart
      key={kind}
      series={[withNetwork ? NET : DISK]}
      read={read}
      subscribe={subscribe}
      active={active}
      format={formatSpeed}
      formatTime={formatClock}
      markers={markers}
      legend={false}
      peakLabel={false}
      averageLabel="never"
      ceiling={ceiling}
      ceilingLabel={label ? `Your connection, up to ${label}` : undefined}
      minSpan={20}
      plotClassName="h-16"
      label={withNetwork ? "Download speed over time" : "Disk write speed over time"}
    />
  );
}
