"""Sustained write rate of a target folder, using the write pattern of Legendary's FileWorker.

Legendary 0.21.1's writer (downloader/mp/workers.py:154-339) is one process doing, per chunk part,
`current_file.write(shm.buf[a:b])` into a file opened with open(path, 'wb') (default buffering), one file at a
time. This script does exactly that from a SharedMemory block, so the number it prints is what the writer can
reach on this disk with nothing else in the way (no network, no IPC).

Usage: py -I tools/bench/disk_write.py <scratch-dir> [--total-gb 8] [--file-gb 1] [--part-kib 1024]
           [--priority normal|below|idle|background] [--fsync-mb 256] [--json out.jsonl] [--label text]
--priority background = PROCESS_MODE_BACKGROUND_BEGIN (very low I/O + memory priority), the others set the
priority class only. Files are deleted at the end.
"""
import argparse
import ctypes
import json
import os
import sys
import time
from multiprocessing.shared_memory import SharedMemory

MiB = 1024 * 1024


def set_priority(mode):
    if sys.platform != 'win32' or mode == 'normal':
        return
    k32 = ctypes.windll.kernel32
    h = k32.GetCurrentProcess()
    value = {'below': 0x4000, 'idle': 0x40, 'background': 0x00100000}[mode]
    if not k32.SetPriorityClass(h, value):
        raise OSError(ctypes.get_last_error(), 'SetPriorityClass failed')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dir')
    ap.add_argument('--total-gb', type=float, default=8)
    ap.add_argument('--file-gb', type=float, default=1)
    ap.add_argument('--part-kib', type=int, default=1024)
    ap.add_argument('--priority', default='normal', choices=['normal', 'below', 'idle', 'background'])
    ap.add_argument('--fsync-mb', type=int, default=0, help='os.fsync every N MiB, so the rate is the device rate, not the file cache')
    ap.add_argument('--json')
    ap.add_argument('--label', default='')
    a = ap.parse_args()

    set_priority(a.priority)
    os.makedirs(a.dir, exist_ok=True)
    shm = SharedMemory(create=True, size=64 * MiB)
    try:
        buf = shm.buf
        buf[:] = os.urandom(64 * MiB)  # incompressible, like Fortnite's pak data
        part = a.part_kib * 1024
        total = int(a.total_gb * 1024 * MiB)
        per_file = int(a.file_gb * 1024 * MiB)
        written, n_file, off = 0, 0, 0
        t0 = last = time.perf_counter()
        last_bytes = 0
        samples = []
        f = None
        close_times = []
        while written < total:
            if f is None or f.tell() >= per_file:
                if f:
                    tc = time.perf_counter()
                    f.close()
                    close_times.append(time.perf_counter() - tc)
                f = open(os.path.join(a.dir, f'w{n_file:03d}.bin'), 'wb')
                n_file += 1
            if off + part > len(buf):
                off = 0
            f.write(buf[off:off + part])
            off += part
            written += part
            if a.fsync_mb and written % (a.fsync_mb * MiB) == 0:
                f.flush()
                os.fsync(f.fileno())
            now = time.perf_counter()
            if now - last >= 1.0:
                samples.append(round((written - last_bytes) / (now - last) / 1e6, 1))
                last, last_bytes = now, written
        tc = time.perf_counter()
        f.close()
        close_times.append(time.perf_counter() - tc)
        secs = time.perf_counter() - t0
        res = {
            'kind': 'disk_write', 'label': a.label, 'dir': a.dir, 'priority': a.priority, 'part_kib': a.part_kib,
            'total_gb': a.total_gb, 'fsync_mb': a.fsync_mb, 'mbps': round(written / secs / 1e6, 1), 'seconds': round(secs, 1),
            'min_1s': min(samples) if samples else None, 'max_1s': max(samples) if samples else None,
            'max_close_s': round(max(close_times), 3), 'per_second': samples,
        }
        print(json.dumps({k: v for k, v in res.items() if k != 'per_second'}))
        print('per-second MB/s:', samples)
        if a.json:
            with open(a.json, 'a') as j:
                j.write(json.dumps(res) + '\n')
    finally:
        shm.close()
        shm.unlink()
        for name in os.listdir(a.dir):
            if name.startswith('w') and name.endswith('.bin'):
                os.remove(os.path.join(a.dir, name))


if __name__ == '__main__':
    main()
