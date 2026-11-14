"""A stand-in for `legendary install` that Glider's own process runner can drive (no login, no Epic API).

  <venv>/python -I tools/bench/legendary_run.py --src <legendary checkout> --manifest fn.manifest --base <cdn base url>
      --dir <scratch install folder> [--prefix FortniteGame/Content/Paks/pakchunk10-] [--workers 16] [--shm-mb 1024]
      [--max-seconds 0]

It runs Legendary's own DLManager (stock, or a patched fork if --src is one) exactly like the real CLI does and logs in the real
CLI's format on stderr ("[DLManager] INFO: ..."), so Core's ProgressParser, LegendaryOutputFacts and RunTuner see what they would see
from legendary.exe: the "Download Manager running with process-id" line, the "Starting file writing worker" line and the
five-line progress block. tests/Glider.Core.Tests/Benchmarks/DownloadBenchmarkTests.cs uses it to measure the v0.2 download modes
end to end (opt-in: set GLIDER_BENCH_*). The install folder is deleted at the end.
"""
import argparse
import logging
import logging.handlers
import os
import shutil
import sys
import time
from multiprocessing import Queue as MPQueue

MiB = 1024 * 1024


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--src', required=True)
    ap.add_argument('--manifest', required=True)
    ap.add_argument('--base', required=True)
    ap.add_argument('--dir', required=True)
    ap.add_argument('--prefix', action='append', default=[])
    ap.add_argument('--workers', type=int, default=16)
    ap.add_argument('--shm-mb', type=int, default=1024)
    ap.add_argument('--timeout', type=float, default=10)
    ap.add_argument('--max-seconds', type=float, default=0, help='stop after this long (the run reports what it moved)')
    ap.add_argument('--control-file')
    a = ap.parse_args()

    sys.path.insert(0, a.src)
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from lgd_shim import PlainDLManager  # noqa: E402  (stock DLManager, importable from a source checkout)
    from legendary.models.manifest import Manifest  # noqa: E402

    if os.path.exists(a.dir):
        raise SystemExit(f'{a.dir} exists; refusing to write into an existing folder')
    os.makedirs(a.dir)

    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter('[%(name)s] %(levelname)s: %(message)s'))
    q = MPQueue(-1)
    listener = logging.handlers.QueueListener(q, handler)
    listener.start()
    root = logging.getLogger()
    root.setLevel(logging.INFO)
    root.handlers = [logging.handlers.QueueHandler(q)]

    manifest = Manifest.read_all(open(a.manifest, 'rb').read())
    extra = {'control_file': a.control_file} if a.control_file else {}
    dlm = PlainDLManager(a.dir, a.base.rstrip('/'), {}, max_workers=a.workers, max_shared_memory=a.shm_mb * MiB,
                         dl_timeout=a.timeout, **extra)
    dlm.logging_queue = q
    ares = dlm.run_analysis(manifest=manifest, file_prefix_filter=a.prefix or None)
    # The summary Legendary prints before it asks to install (Core reads the sizes from these lines).
    print(f'[cli] INFO: Download size: {ares.dl_size / MiB:.2f} MiB', file=sys.stderr, flush=True)
    t0 = time.perf_counter()
    dlm.start()
    dlm.join(timeout=a.max_seconds or None)
    if dlm.is_alive():
        import subprocess
        subprocess.run(['taskkill', '/T', '/F', '/PID', str(dlm.pid)], capture_output=True)
        dlm.join(timeout=10)
    wall = time.perf_counter() - t0
    time.sleep(0.5)
    listener.stop()
    print(f'bench: wall {wall:.1f}s, download {ares.dl_size / MiB:.0f} MiB', flush=True)
    shutil.rmtree(a.dir, ignore_errors=True)


if __name__ == '__main__':
    main()
