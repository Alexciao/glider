"""Print a table of legendary_harness.py runs (one JSON object per line).

Usage: py -I tools/bench/summarize_runs.py runs.jsonl [label-substring ...]
Columns: net = download MB/s during the network phase (first to last chunk), avg = over the whole run including
process start-up and the writer draining; cache = peak shared-memory use and seconds it was >= 90% full; W/FW =
share of time the download workers / file writer spent idle (waiting for work) vs in HTTP / write(); shm = seconds
the scheduler waited for a free shared-memory segment (downloads stalled behind the writer).
"""
import json
import sys


def main():
    path, filters = sys.argv[1], sys.argv[2:]
    rows = [json.loads(line) for line in open(path) if line.strip()]
    if filters:
        rows = [r for r in rows if any(f in r['label'] for f in filters)]
    print(f"{'label':<38} {'MiB':>6} {'wall s':>7} {'net MB/s':>8} {'avg':>6} {'cache':>6} {'full':>7} "
          f"{'W idle':>6} {'W http':>6} {'retry':>5} {'conns':>5} {'FW idle':>7} {'FW wr':>6} {'shm s':>5}")
    for r in rows:
        w, fw, m = r.get('dlworker', {}), r.get('writer', {}), r.get('manager', {})
        print(f"{r['label'][:38]:<38} {r['dl_mib']:6.0f} {r['wall_s']:7.1f} {r['net_phase_mbps']:8.1f} "
              f"{r['avg_dl_mbps']:6.1f} {r['cache_max_mib']:6.0f} {r['cache_full_s']:>3}/{r['progress_s']:<3} "
              f"{w.get('share', {}).get('idle', 0):6.2f} {w.get('share', {}).get('http', 0):6.2f} "
              f"{w.get('counts', {}).get('retries', 0):5d} {w.get('counts', {}).get('conns', 0):5d} "
              f"{fw.get('share', {}).get('idle', 0):7.2f} {fw.get('share', {}).get('write', 0):6.2f} "
              f"{m.get('seconds', {}).get('wait_shm', 0):5.0f}")


if __name__ == '__main__':
    main()
