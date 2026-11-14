"""Drive Legendary 0.21.1's own DLManager (from source, no login) and measure each pipeline stage.

  <venv>/python -I tools/bench/legendary_harness.py --src <legendary checkout> --manifest fn.manifest
      --base https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir   (or http://127.0.0.1:8099 for ChunkServer)
      --dir D:/scratch/install --prefix FortniteGame/Content/Paks/pakchunk10- [--prefix ...]
      [--workers 16] [--shm-mb 3072] [--tree-prio below] [--worker-prio ...] [--writer-prio ...]
      [--label v0.1] [--json runs.jsonl] [--keep]

The venv needs requests + pycryptodomex + filelock (Legendary's runtime deps). --tree-prio is applied to this process
before anything is spawned, so every Legendary process inherits it, exactly like Glider's job object (BELOW_NORMAL)
in v0.1. --worker-prio / --writer-prio apply to only those processes (see lgd_instr.apply_priority for the syntax).
The install folder is deleted afterwards unless --keep.

Output: one JSON line per run with wall time, average network rate, per-stage time shares, shared-memory stalls,
connections opened, retries, and the per-second progress lines Legendary itself printed.
"""
import argparse
import json
import logging
import os
import re
import shutil
import sys
import time
from logging.handlers import QueueListener
from multiprocessing import Queue as MPQueue

MiB = 1024 * 1024
PROGRESS = re.compile(r'Cache usage: (?P<cache>[\d.]+) MiB, active tasks: (?P<active>\d+)')
SPEED = re.compile(r'Download\t- (?P<raw>[\d.]+) MiB/s \(raw\)')
DISK = re.compile(r'Disk\t- (?P<w>[\d.]+) MiB/s \(write\)')


class Collector(logging.Handler):
    def __init__(self, echo, log_path=None):
        super().__init__()
        self.stats, self.lines, self.echo = [], [], echo
        self.log_file = open(log_path, 'w', encoding='utf-8') if log_path else None
        self.cache, self.raw, self.write = [], [], []

    def emit(self, record):
        msg = record.getMessage()
        if record.name == 'STATS':
            self.stats.append(json.loads(msg))
            return
        self.lines.append(f'{record.created:.3f} [{record.name}] {msg}')
        if self.log_file:
            self.log_file.write(self.lines[-1] + chr(10))
            self.log_file.flush()
        if m := PROGRESS.search(msg):
            self.cache.append(float(m['cache']))
        elif m := SPEED.search(msg):
            self.raw.append(float(m['raw']))
        elif m := DISK.search(msg):
            self.write.append(float(m['w']))
        if self.echo and (record.levelno >= logging.WARNING or 'Progress' in msg):
            print(f'[{record.name}] {msg}', flush=True)


def summarize(stats):
    out = {'prio': {}}
    agg = {}
    for s in stats:
        if s['kind'] == 'prio':
            out['prio'][s['name']] = s['prio']
            if 'segments' in s:
                out['shm_segments'] = s['segments']
            continue
        a = agg.setdefault(s['kind'], {'t': {}, 'n': {}, 'span': 0.0, 'procs': set()})
        a['procs'].add(s['pid'])
        a['span'] += s['span']
        for k, v in s['t'].items():
            a['t'][k] = a['t'].get(k, 0.0) + v
        for k, v in s['n'].items():
            a['n'][k] = a['n'].get(k, 0) + v
    for kind, a in agg.items():
        busy = sum(v for k, v in a['t'].items() if k != 'ttfb')  # ttfb is part of http
        out[kind] = {
            'procs': len(a['procs']),
            'seconds': {k: round(v, 2) for k, v in sorted(a['t'].items())},
            'share': {k: round(v / busy, 3) for k, v in sorted(a['t'].items())} if busy else {},
            'counts': a['n'],
        }
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--base', required=True)
    ap.add_argument('--dir', required=True)
    ap.add_argument('--prefix', action='append', default=[])
    ap.add_argument('--workers', type=int, default=0)
    ap.add_argument('--shm-mb', type=int, default=2048)
    ap.add_argument('--timeout', type=float, default=10)
    ap.add_argument('--tree-prio', default='')
    ap.add_argument('--worker-prio', default='')
    ap.add_argument('--writer-prio', default='')
    ap.add_argument('--writer-buffer', type=int, default=-1)
    ap.add_argument('--label', default='')
    ap.add_argument('--json')
    ap.add_argument('--log')
    ap.add_argument('--echo', action='store_true')
    ap.add_argument('--keep', action='store_true')
    ap.add_argument('--plain', action='store_true', help='stock DLManager/workers, no stage timers (e.g. to test a patched fork)')
    ap.add_argument('--fallback', action='append', default=[], help='patched fork only: fallback CDN base URL')
    ap.add_argument('--control-file', help='patched fork only: control file path')
    ap.add_argument('--max-seconds', type=float, default=0, help='stop the download after this long and report the partial run')
    a = ap.parse_args()

    sys.path.insert(0, a.src)
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    if a.plain:
        from lgd_shim import PlainDLManager as Manager  # noqa: E402
        extra = {k: v for k, v in (('fallback_urls', a.fallback), ('control_file', a.control_file)) if v}
    else:
        import lgd_instr  # noqa: E402  (patches legendary's manager to use the instrumented workers)
        Manager, extra = lgd_instr.IDLManager, {}
        lgd_instr.apply_priority(a.tree_prio)
    from legendary.models.manifest import Manifest  # noqa: E402

    os.environ['GLIDER_BENCH_WORKER_PRIO'] = a.worker_prio
    os.environ['GLIDER_BENCH_WRITER_PRIO'] = a.writer_prio
    os.environ['GLIDER_BENCH_WRITER_BUFFER'] = str(a.writer_buffer)

    if os.path.exists(a.dir):
        raise SystemExit(f'{a.dir} exists; refusing to write into an existing folder')
    os.makedirs(a.dir)

    q = MPQueue(-1)
    col = Collector(a.echo, a.log)
    listener = QueueListener(q, col)
    listener.start()
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    root.handlers = [logging.handlers.QueueHandler(q)]

    manifest = Manifest.read_all(open(a.manifest, 'rb').read())
    dlm = Manager(a.dir, a.base.rstrip('/'), {}, max_workers=a.workers,
                  max_shared_memory=a.shm_mb * MiB, dl_timeout=a.timeout, **extra)
    dlm.logging_queue = q
    t_an = time.perf_counter()
    ares = dlm.run_analysis(manifest=manifest, file_prefix_filter=a.prefix or None)
    t_an = time.perf_counter() - t_an
    print(f'analysis {t_an:.1f}s: download {ares.dl_size / MiB:.0f} MiB, install {ares.install_size / MiB:.0f} MiB, '
          f'min shm {ares.min_memory / MiB:.0f} MiB, workers {dlm.max_workers}', flush=True)

    t0 = time.perf_counter()
    t0_wall = time.time()
    dlm.start()
    dlm.join(timeout=a.max_seconds or None)
    partial = dlm.is_alive()
    if partial:  # kill DLManager and its worker processes (they are not daemons)
        import subprocess
        subprocess.run(['taskkill', '/T', '/F', '/PID', str(dlm.pid)], capture_output=True)
        dlm.join(timeout=10)
    wall = time.perf_counter() - t0
    time.sleep(0.5)
    listener.stop()

    # first/last chunk download timestamps from worker stats give the network phase
    dl_stats = [s for s in col.stats if s['kind'] == 'dlworker' and s['n'].get('bytes')]
    net_start = min((s['ts'] - s['span'] for s in dl_stats), default=0)
    net_end = max((s['ts'] for s in dl_stats), default=0)
    summary = summarize(col.stats)
    got = sum(s['n'].get('bytes', 0) for s in dl_stats)
    dl_bytes = got if partial else ares.dl_size
    res = {
        'kind': 'legendary', 'label': a.label, 'base': a.base, 'dir': a.dir, 'workers': dlm.max_workers,
        'shm_mb': a.shm_mb, 'tree_prio': a.tree_prio, 'worker_prio': a.worker_prio, 'writer_prio': a.writer_prio,
        'writer_buffer': a.writer_buffer,
        'partial': partial, 'dl_mib': round(dl_bytes / MiB, 1), 'install_mib': round(ares.install_size / MiB, 1),
        'wall_s': round(wall, 1), 'net_s': round(net_end - net_start, 1),
        'startup_s': round(net_start - t0_wall, 1) if dl_stats else None,  # start -> first chunk downloaded
        'avg_dl_mbps': round(dl_bytes / wall / 1e6, 1),
        'net_phase_mbps': round(dl_bytes / max(net_end - net_start, 0.001) / 1e6, 1),
        'peak_raw_mibps': max(col.raw, default=0), 'cache_max_mib': max(col.cache, default=0),
        'cache_full_s': sum(1 for c in col.cache if c >= 0.9 * a.shm_mb), 'progress_s': len(col.cache),
        'per_second_raw_mibps': col.raw, 'per_second_write_mibps': col.write, 'per_second_cache_mib': col.cache,
        **summary,
    }
    print(json.dumps({k: v for k, v in res.items() if not k.startswith('per_second')}, indent=1))
    if a.json:
        with open(a.json, 'a') as j:
            j.write(json.dumps(res) + '\n')
    if a.log:
        with open(a.log, 'w', encoding='utf-8') as lf:
            lf.write('\n'.join(col.lines))
    if not a.keep:
        shutil.rmtree(a.dir, ignore_errors=True)


if __name__ == '__main__':
    main()
