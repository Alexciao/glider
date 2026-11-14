"""Foreground CPU load for priority experiments: N processes busy-looping at normal priority for T seconds,
standing in for a game or a compile that keeps every core busy while a download runs.

Usage: py -I tools/bench/cpu_load.py [--procs 12] [--seconds 120]
"""
import argparse
import multiprocessing as mp
import time


def burn(seconds):
    end = time.perf_counter() + seconds
    x = 0
    while time.perf_counter() < end:
        for _ in range(10000):
            x = (x * 1103515245 + 12345) & 0x7FFFFFFF


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--procs', type=int, default=mp.cpu_count())
    ap.add_argument('--seconds', type=float, default=120)
    a = ap.parse_args()
    ps = [mp.Process(target=burn, args=(a.seconds,)) for _ in range(a.procs)]
    for p in ps:
        p.start()
    for p in ps:
        p.join()


if __name__ == '__main__':
    main()
