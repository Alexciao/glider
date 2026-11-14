# Download performance: diagnosis and design (v0.2)

Question: why are v0.1.x downloads slow "even on fast Wi‑Fi", and what should v0.2 change? Everything below was
measured on the user's PC on 2026-11-14 with the tools in `tools/bench/` (see `tools/bench/README.md`); raw results are
in `docs/research/fixtures/download-perf/`. Legendary = the bundled 0.21.1, driven from its own source.

## TL;DR

- **Network ceiling: ~42 MB/s (≈340 Mbit/s)**. Plain parallel HTTP GETs of real Fortnite chunks from Epic's CDNs plateau
  at 41–46 MB/s with 4–16 keep-alive connections. One connection alone gets 21–29 MB/s. More than 16 connections are
  slightly *worse*: they only add queueing delay.
- **Legendary is not the bottleneck on a fast disk.** With v0.1's exact settings (16 workers, 3 GiB cache,
  BELOW_NORMAL) writing to the NVMe drive, it downloads at **37.5 MB/s, about 89% of the ceiling**. From a local server
  its pipeline reaches 400–600 MB/s.
- **The bottleneck is the install drive.** Fortnite lives on D:, a DRAM-less SATA SSD (Crucial BX500 1 TB, 81% full)
  that **sustains only 3–9 MB/s of writes** once its fast cache is used up. The last real v0.1 job (a 35.5 GB repair)
  averaged **7.3 MiB/s**. It ran at 24–28 MiB/s for the first ~90 s, then fell to the disk's rate, and needed a 6.5-minute
  "writer drain" after the last byte arrived. The same 8 MB/s shows up from a plain Python write test, from Legendary
  driven from a local server, and in the disk performance counters.
- **v0.1 priorities don't throttle the network.** Under a full 12-thread CPU load, the download phase runs at 94–106%
  of Full speed whatever the priority. BELOW_NORMAL only costs **start-up time** under load (+12 s typical, up to ~75 s
  before the first byte). It **does not lower disk or memory priority at all**: Windows reports I/O priority 2 (normal)
  and memory priority 5 for every Legendary process. So it doesn't protect the PC either.
- **The CDN choice matters only in bad moments.** A 4 MB probe reliably finds hosts that are 3–10× slower than the rest
  (Akamai with ~500 ms TTFB at times, Cloudflare at ~12 MB/s). Among good hosts it can't tell the difference (±50%
  between rounds).
- **Legendary 0.21.1 has three latent bugs in its failure path**: retries never count up, the give-up path reports
  twice, and a requeued chunk kills its worker. Together with having no CDN failover, a CDN outage stalls the download
  until Glider's 90 s watchdog kills it. A 79-line patch (`tools/bench/legendary-0.21.1-glider.patch`, tested) fixes
  this and adds failover plus a runtime control file.

Biggest wins for this user, in order: (1) tell them the drive is the limit and offer a faster drive (NVMe here ≈
**5× faster**: 8 → ~38 MB/s); (2) don't re-download what's already on disk (the 35 GB repair right after adopting an
existing install, §3.3); (3) CDN probe and failover so a bad CDN can never cost 3–10×; (4) priority changes that
actually protect the PC (I/O and memory priority on the writer) instead of slowing start-up.

---

## 1. Machine and connection (no personal data)

| | |
|---|---|
| CPU / RAM | Intel i5-11600KF, 6 cores / 12 threads; 16 GB |
| C: | NVMe KIOXIA XG6 512 GB, 96% full (22 GB free). Sequential write measured **1,222 MB/s** |
| D: (Fortnite) | Crucial BX500 1 TB SATA SSD, DRAM-less, 81% full, TRIM enabled. Sustained write measured **3–9 MB/s** after its cache fills |
| Network | Wi‑Fi 6 adapter on an 802.11ac 5 GHz network, PHY 1053/867 Mbit/s, RSSI −57 dBm, RTT to 1.1.1.1 11 ms |
| Reference | `speed.cloudflare.com`: 27 MB/s on one connection, ~44 MB/s on eight |
| Defender | Real-time protection on. It did not show up as a factor: D: reads stayed under 0.3 MB/s while it wrote at 8 MB/s |
| Glider v0.1.5 effective settings | `--max-workers 16 --max-shared-memory 3072` (AutoTune: 12 CPUs, 15.7 GiB), every Legendary process in a job object at BELOW_NORMAL, CDN = Legendary's default (first in Epic's list: `egdownload.fastly-edge.com`) |

Total data downloaded for this study: ≈16.3 GB (CDN sweeps 4.7 GB, probes 0.1 GB, six real Legendary runs 11.4 GB). All
the other runs were served locally.

## 2. Method

1. **Field data**: the Glider log of the last real job (`[legendary]` progress lines every 30 s).
2. **Ceiling** (`CdnBench.cs`): .NET `SocketsHttpHandler`, HTTP/1.1 keep-alive (like Legendary's `requests`),
   N = 1–64 connections, each fetching real 1 MiB chunks from the 43.10 manifest (sequential from a random offset, so
   no two runs fetch the same chunk). Body discarded, 2 s warm-up, 6 s measured. Hosts interleaved per N so drift over
   time doesn't favour one CDN.
3. **Disk** (`disk_write.py`): exactly Legendary's writer pattern (`open(path,'wb')`, 1 MiB `write()` from a
   SharedMemory buffer, incompressible data), with `fsync` every 256 MiB to get the device rate. Plus the performance
   counter `Win32_PerfFormattedData_PerfDisk_PhysicalDisk` sampled during Legendary runs.
4. **Legendary's pipeline** (`legendary_harness.py` + `lgd_instr.py`): Legendary's own `DLManager.run_analysis()` +
   `start()` from the 0.21.1 source, no login (manifest + base URL given directly). `lgd_instr.py` swaps in copies of
   `DLWorker.run`, `FileWorker.run` and `DLManager.download_job_manager` that are line-for-line the 0.21.1 code plus
   timers. They record idle/HTTP/TTFB/parse/decode/shm-copy/result-put per worker, idle/open/write/close per writer,
   scheduler waits for task slots and for free shared memory, urllib3 connections opened per worker, retries, and the
   priority class / I/O / memory priority each process really has. A 1.9 GB file set (`pakchunk0-`, `pakchunk10-`,
   `pakchunk100-`; 2.1 GB on disk) was used for real-CDN and simulated runs, and a 5.1 GB set for local-throughput runs.
5. **Local CDN** (`ChunkServer.cs`): serves one real 1 MiB chunk for every chunk URL (Legendary doesn't hash-check on
   download, so sizes and write pattern are realistic and no bandwidth is spent). It can add per-request latency and
   jitter, a per-connection delay, a per-connection rate cap, a shared link cap, mid-body resets and mid-body stalls.
6. **CPU load** (`cpu_load.py`): 12 busy-loop processes at normal priority, standing in for a game or a compile. The
   local server ran at High priority so it behaved like the network, not like another process competing for CPU.

What the Wi‑Fi simulation **can** emulate: request latency/TTFB, TCP+TLS handshake time per new connection, a per-flow
throughput ceiling (what loss × RTT impose on one TCP flow, per Mathis), a shared radio cap, RSTs and stalls. What it
**can't**: real TCP congestion control (slow start, window collapse after loss, retransmits), Wi‑Fi airtime contention,
and bufferbloat that grows with the number of flows. The real-CDN sweep (§3.1) is the ground truth for those. Timer
resolution on Windows (~15 ms `Task.Delay`) limits the simulated link to ~39 of the configured 45 MB/s, so simulated
results are compared with each other, not with the real ceiling.

## 3. Results

### 3.1 Network ceiling (real CDNs, plain parallel HTTP, MB/s)

| Connections | egdownload.fastly-edge.com | epicgames-download1.akamaized.net | egs-cloudfront-chunks.epicgamescdn.com |
|---:|---:|---:|---:|
| 1 | 29.2 (p50 33 ms/chunk) | 21.3 | 28.1 |
| 4 | 40.7 | **4.5** (TTFB 442 ms) | 36.9 |
| 8 | 41.6 | 26.4 | **42.8** |
| 16 | 37.2 | 18.3 (TTFB 359 ms) | 42.6 |
| 32 | 30.6 | **46.1** | 33.8 |
| 64 | 37.5 | 36.7 | 33.8 |

No errors in any run, and connections were reused (opened = N). Above 8–16 connections, per-chunk latency grows from
~100 ms to 1.5–1.8 s at the median: the link is full and extra flows only queue. Akamai alternates between fine and
very slow edge responses (TTFB 350–500 ms, probably edge cache misses); high concurrency hides that latency.
**Ceiling used below: 42 MB/s. Target for Full speed: ≥ 38 MB/s.**

Job-start probe (`CdnBench --probe`: per host, 4 warm connections, 4 parallel 1 MiB GETs; ~4 MB and ~0.3 s per host), three rounds:

| Host | Round 1 MB/s (TTFB) | Round 2 | Round 3 |
|---|---:|---:|---:|
| egdownload.fastly-edge.com (Epic's default here) | 32.5 (15 ms) | 20.8 (12) | 33.5 (11) |
| epicgames-download1.akamaized.net | **3.4 (481 ms)** | 47.8 (18) | **3.6 (505 ms)** |
| egs-cloudfront-chunks.epicgamescdn.com | 21.9 (40) | 56.8 (17) | 39.3 (26) |
| fastly-download.epicgames.com | 25.0 (13) | 38.3 (13) | 22.2 (21) |
| cloudflare.epicgamescdn.com | **13.6 (135 ms)** | **12.0 (149)** | **12.3 (173)** |
| download.epicgames.com | 33.8 (21) | 10.0 (16) | 35.6 (16) |

All six hosts serve the same `/Builds/Fortnite/CloudDir/ChunksV4/...` paths without auth. Epic's API listed only the
first three for this install (`installed.json` `base_urls`); `download2/3/4.epicgames.com` don't resolve. Verdict:
the probe **does** catch hosts that are 3–10× slower (high TTFB, low rate, every round for Cloudflare). It **does
not** rank good hosts reliably, so rank good hosts by Epic's order and use the probe only to exclude bad ones.

### 3.2 Disk

| Target | Method | Rate |
|---|---|---:|
| C: NVMe | Legendary write pattern, fsync/256 MiB, 4 GB | **1,222 MB/s** |
| D: BX500 | same pattern, no fsync, 8 GB (stopped at 2.7 GB) | user-level 2.7 GB absorbed by the file cache, then `write()` blocked; device **8–9 MB/s**, queue 3, disk time 290–430% |
| D: BX500 | perf counters during Legendary run (local server, 1.2 GB) | device **avg 7.1 MB/s** (4–9 per 2 s sample) |
| D: field | Legendary repair, 35.5 GB | **7.3 MiB/s** average, see 3.3 |

This drive's fast-write cache is small at 81% full. After a few GB it writes at single-digit MB/s, and it hadn't
recovered after ~10 minutes idle between tests. No software setting changes that rate.

### 3.3 Field log (v0.1.5, 2026-11-13, adopting an existing install on D:)

| Phase | Time | Notes |
|---|---:|---|
| `import` + `verify` | 22:17:51 → 22:19:45 | verify flagged 190 corrupted + 247 missing files → repair |
| repair (`--max-workers 16 --max-shared-memory 3072`, fastly-edge) | 22:19:45 → 23:47:09 (5,238 s) | Download size 35,483 MiB, install size 38,214 MiB |
| first 90 s | 22:19:52 → 22:21:22 | 24.0 / 28.5 / 14.8 MiB/s (30 s samples): network + file cache |
| download phase | → 23:40:25 (4,833 s) | **7.3 MiB/s average**, many 30 s samples at 0.0–2 MiB/s (downloads waiting for the writer) |
| writer drain after the last byte | 23:40:25 → 23:46:55 (390 s) | 92.5% → 100% with 0 MiB/s downloaded: ~3 GB of cache written at ~8 MB/s |
| final verify | 23:47:09 → 23:48:47 | |

A full shared-memory cache during the whole download, then a drain at the disk's rate after the last byte, means the
writer was the bottleneck (§3.4 shows why it's the disk, not Legendary's writer).

### 3.4 Legendary's pipeline, stage by stage

Local server, no network limit (writes to C: unless noted). **net** = MB/s from first to last chunk. **W idle** = share
of worker time waiting for a task. **FW write** = share of writer time inside `write()`. **shm wait** = seconds the
scheduler waited for a free cache segment.

| Run | net MB/s | whole run | W idle / http | FW idle / write | shm wait | cache peak |
|---|---:|---:|---|---|---:|---:|
| 4 workers, 5.1 GB | **598** | 519 | 0.20 / 0.68 | 0.45 / 0.40 | 0 s | 187 MiB |
| 8 workers | 516 | 439 | 0.42 / 0.47 | 0.30 / 0.53 | 0 s | 2.9 GiB |
| 16 workers (v0.1) | 468 | 401 | 0.71 / 0.23 | 0.39 / 0.48 | 3 s | 3 GiB (full 4 s) |
| 32 workers | 418 | 363 | 0.87 / 0.10 | 0.37 / 0.49 | 3 s | 3 GiB |
| **v0.1 → D:** 16 w, 1.2 GB | 98 (into cache) | **8.0** | 0.99 / 0.01 | **0.01 / 0.99** | – | 971 MiB |

- Decompression, parse, SHA and shm copies are negligible (< 1% of worker time). Fortnite chunks are 96% stored
  uncompressed (143.2 GiB compressed vs 148.8 GiB window), and Legendary doesn't hash-check on download.
- One `requests.Session` per worker process: urllib3 opened exactly one connection per worker per run (16 for 16), so
  keep-alive works. Requests are sequential per worker, and in-flight work is capped at `2 × max_workers`
  (`manager.py:453`).
- The single writer process manages ~1 GB/s on NVMe, about 24× the network ceiling, so it's not a limit. On D: it's 99%
  blocked inside `write()`: Windows' cache manager throttles the writer once dirty pages pile up, and Legendary's own
  "Disk" line reads 0.00 MiB/s for seconds at a time.
- Each worker is a separate process: ~30 MB private memory each (from source on CPython 3.12), 235 MB for the DLManager.

Real CDN, same 1.9 GB set, writes to C: (results vary ±15% between runs minutes apart):

| Run | net MB/s | % of 42 | Notes |
|---|---:|---:|---|
| **v0.1 exact**: fastly-edge, 16 w, 3 GiB, BELOW_NORMAL | **37.5** | 89% | cache peak 114 MiB; 0 retries; 16 connections |
| Full: fastly-edge, 8 w, 1 GiB | 41.2 | 98% | |
| Full: fastly-edge, 32 w, 1 GiB | 39.0 | 93% | p50 per chunk 0.71 s vs 0.18 s at 8 w |
| Full: cloudfront, 16 w, 1 GiB | 31.5 | 75% | the probe's best host in 2 of 3 rounds |
| Full: akamai, 16 w, 1 GiB | 46.3 | 110% | Akamai on a good minute |
| Balanced candidate: fastly-edge, 16 w, BELOW_NORMAL + writer background mode | 41.3 | 98% | |

The cache never filled in any network-bound run (peak 40–237 MiB). 3 GiB of shared memory only ever holds data when
the disk is slower than the network, and then any size fills up and makes the drain at the end longer.

### 3.5 Simulated Wi‑Fi (local server, 1.9 GB → C:)

Profile "lossy high-latency Wi‑Fi": 50 ± 20 ms per request, 150 ms per new connection, 4 MB/s per connection, 45 MB/s
shared link, 0.2% mid-body resets, 0.5% mid-body 3 s stalls.

| Workers | net MB/s | retries | Comment |
|---:|---:|---:|---|
| 4 | 11.1 | 6 | latency-bound: 4 × (1 MiB at 4 MB/s + 50 ms) |
| 8 | 21.8 | 0 | |
| **16** | **38.9** | 6 | the link is full (≈ the ~39 MB/s simulation ceiling) |
| 32 | 37.5 | 4 | |
| 48 | 36.1 | 5 | |

Resets cost almost nothing: requests retries at once on a new connection. Stalls under 10 s (`--dl-timeout`) just
slow one chunk. Throughput scales linearly with connections until the link is full, then drops slightly. On a link like
this, 16 connections are needed and enough. A machine that gets 6–8 workers from today's AutoTune (2–4 cores, or < 8 GB
RAM) would lose 40–70%.

### 3.6 Self-throttling: Full / Balanced / Background under CPU load

Local server at High priority with a "good Wi‑Fi" profile (15 ± 5 ms, 25 MB/s per connection, 45 MB/s link), 1.9 GB → C:,
12 busy-loop processes at normal priority during the run.

| Variant (16 workers unless noted) | net MB/s | % of Full under load | start → first chunk | whole run MB/s |
|---|---:|---:|---:|---:|
| Full (normal), no load (reference) | 38.9 | – | ~1 s | 37.8 |
| **Full (normal), load** | **36.4** | 100% | **3.8 s** | 33.3 |
| v0.1: BELOW_NORMAL tree, load | 38.6 | 106% | ~16 s (first try: ~75 s incl. manifest parse) | 28.9 |
| IDLE tree, load | 38.7 | 106% | 17.6 s | 28.7 |
| IDLE tree, 4 workers, load | 39.8 | 109% | 16.7 s | 29.7 |
| BELOW_NORMAL + writer `PROCESS_MODE_BACKGROUND` (I/O 0, mem 1), load | 38.8 | 107% | 16.3 s | 29.4 |
| normal + writer background mode, load | 36.6 | 101% | 3.8 s | 33.9 |

- **Full is uncapped.** v0.1.5 passes no rate limit, and Legendary has none. The only "throttle" is the job object's
  BELOW_NORMAL (`NativeMethods.cs:103-118`, `LegendaryProcessRunner.cs:419-435`).
- **The network side doesn't care about CPU priority.** Workers spend 93–96% of their time blocked on sockets.
  Balanced ≥ 90% of Full is met by every variant once downloading.
- **CPU priority costs start-up.** Legendary's CLI (login, manifest download + parse, analysis) and the spawning of 17
  Python processes all run at the lowered priority. Under load that took 16–18 s instead of 4 s, and ~75 s in the first
  BELOW_NORMAL run, which includes parsing the 10 MB manifest. For a 35 GB job that's noise. For a 2 GB update it's 15%.
- **BELOW_NORMAL doesn't lower I/O or memory priority** (verified with `NtQueryInformationProcess(ProcessIoPriority)` /
  `GetProcessInformation(ProcessMemoryPriority)` in every Legendary process: I/O 2, memory 5). So v0.1's "low priority"
  never protected the disk or the standby cache, which is what makes a PC feel slow during a download.
- **Process architecture**: `legendary.exe` (CLI) → `DLManager` (multiprocessing spawn) → N × `DLWorker` (HTTP +
  shm copy) + 1 × `FileWorker` (all disk writes, the only process that touches the install folder). Priorities **can**
  be applied per process: the writer is the last child DLManager spawns, right after the stderr line
  `[DLManager] INFO: Starting file writing worker...`. Low I/O priority belongs on the writer alone. With the patch the
  writer sets it on itself (§5.6).
- Not measured: how much each variant protects the *foreground* (game frame times, disk latency for other apps). The
  priorities are designed for that from Windows' documented behaviour; verify it after implementation (§7).

### 3.7 Failure handling in 0.21.1 (source + tests with the patched fork)

- `workers.py:93-118`: the retry loop never increments `tries`, so a failing chunk is retried forever, back to back,
  with no backoff, on the same CDN. A dead CDN looks like a stall, and Glider's 90 s watchdog
  (`LegendaryOptions.StallTimeout`) kills and restarts the whole job. A restart throws away partially written files:
  resume only records whole files (`manager.py:579-586`), and Fortnite's files are mostly 4 GiB `.ucas`. It also throws
  away everything in the cache.
- `workers.py:119-130`: once the retry limit *is* reachable, the give-up path posts the failure and then falls into
  "Chunk somehow None?", which posts it again.
- `manager.py:551-560`: a failed chunk is requeued by putting the *result* object back on the queue. The worker then
  calls `DownloaderTaskResult(success=…, **job.__dict__)` with `success` already in `__dict__`. The TypeError is raised
  inside its `except` handler, and **the worker process dies silently**. In stock 0.21.1 only parse/decode errors reach
  this path. With failover it would kill every worker; reproduced and fixed.
- Patched fork, local test (8 workers, primary server resets every response, fallback healthy): **switched CDN 9 s
  after start and finished in 27.7 s** (it never finishes on stock). A control file switch (slow → fast server,
  `max_active` 16 → 4) took effect within one progress interval.

## 4. Bottlenecks, ranked

1. **Install disk (this machine): D: sustains 3–9 MB/s.** It limits the download to ~19% of the network ceiling. The
   cache (3 GiB) fills within the first minutes and only makes the end-of-job drain longer. Software can't fix the
   drive. It can **say so** clearly, steer installs to a fast drive, avoid writing more than needed, and keep the
   writer from being starved.
2. **Re-downloading what's already there** (§3.3, 35 GB). Not a throughput problem, but it
   doubles or triples the perceived "slow download". To investigate (checklist item 9).
3. **Bad CDN moments with no way out**: when Epic's first CDN serves at 3–12 MB/s (seen for Akamai and Cloudflare
   here), 0.21.1 stays on it for the whole job, and on hard errors it stalls (§3.7).
4. **Start-up at BELOW_NORMAL under load** (+12–70 s), plus priorities that throttle the wrong resource (CPU, not
   disk or memory).
5. **Worker count on small machines**: AutoTune gives 6–8 workers on 2–4 cores or < 8 GB RAM. On a high-latency link
   that's 40–70% below what 16 reach.
6. Not a bottleneck: decompression, hashing, the Python writer on a fast disk, HTTP keep-alive (already one session
   per worker), shared-memory size (≥ 1 GiB is plenty).

## 5. Design (v0.2)

Config-only changes (5.1–5.3, 5.5, 5.7, 5.8) are enough to reach the target on a fast disk. v0.1 already gets 89% and
the measured gap is within run-to-run noise. The patch (5.4) is for robustness (CDN outages, slow CDNs mid-job), not
peak speed. Recommendation: ship 5.1–5.3 + 5.5 + 5.7 + 5.8 first, then the fork.

### 5.1 CDN probe at job start

- **Candidates**: the Fortnite entry's `base_urls` from Legendary's `installed.json` (update/repair), in Epic's order,
  then these known extra hosts if not already listed: `fastly-download.epicgames.com`, `download.epicgames.com`,
  `cloudflare.epicgamescdn.com`. Path suffix `/Builds/Fortnite/CloudDir` for all. Fresh install with no `installed.json`
  entry: the three hosts this install got from the API (`egdownload.fastly-edge.com`,
  `epicgames-download1.akamaized.net`, `egs-cloudfront-chunks.epicgamescdn.com`) + the extras, Epic's usual order.
- **Probe objects**: 8 chunk paths of full 1 MiB chunks taken from the newest manifest in
  `%LocalAppData%\Glider\legendary\manifests\`. Needs a small C# manifest reader (§6, item 3). With no manifest
  (first-ever install): skip the probe and use Epic's default. With the patch, failover covers this case.
- **Procedure** (C#, `HttpClient`/`SocketsHttpHandler`, HTTP/1.1, the Legendary user agent; mirror
  `CdnBench.cs` `Probe`): hosts **one after another** (parallel probes split the link and bias the result). Per host:
  open 4 connections with one GET each of a probe chunk (not timed), then 4 parallel GETs of 4 other probe chunks,
  timed. Per-host timeout 3 s, total budget 2.5 s (stop early if 3 hosts are done and one passes). Record
  `ttfbMs` = mean TTFB and `mbps` = bytes / wall time.
- **Selection**: a host is *bad* if any request failed, `ttfbMs > 250`, or `mbps < 0.5 × best`. Pick the **first host
  in Epic's order that isn't bad**. Don't pick "the fastest": among good hosts the probe is noise (§3.1).
- **Passing it**: if the pick is in Epic's list, `--preferred-cdn <host>`. If it's an extra host,
  `--base-url https://<host>/Builds/Fortnite/CloudDir`. (`--base-url` overrides Epic's list completely,
  `core.py:1620-1623`, and on stock 0.21.1 it also removes the patch's fallback list, so prefer `--preferred-cdn` when
  possible.) The user's `Settings.PreferredCdn`, if set, wins and skips the probe.
- Log one line per job: `cdn probe: host=… ttfb=… mbps=… verdict=…` for every host, and the pick.

### 5.2 Watching the CDN during the job, failover

Inputs per progress block (1 s): raw MiB/s, cache usage (MiB, from ` - Cache usage:`, parsed but thrown away today:
`ProgressParser.cs:126`), disk write MiB/s.

- **Disk-bound** (§5.7) if cache usage ≥ 85% of `--max-shared-memory` for ≥ 10 of the last 15 s. Never blame the CDN
  then.
- **CDN-slow** if not disk-bound, the 60 s average raw rate is < 40% of the probe's `mbps` for the chosen host (or
  < 5 MB/s if there was no probe), and the job has > 3 GB left.
- **Stock exe**: the only lever is a restart with another CDN. That costs ~10 s of login/manifest/analysis, the
  partially written current file (up to 4 GiB) and the cache contents. Do it only on CDN-slow for **3 minutes in a
  row**, at most once per job: kill the tree (as today) and rerun the same command with the next good host from the
  probe. Keep the 90 s stall watchdog for hard stalls, and on a stall restart with the next host instead of the same one.
- **Patched fork** (§5.4): hard errors fail over inside Legendary (8 failed chunks in 30 s). For CDN-slow, Glider
  first re-probes the next candidate in the background (one 4-chunk probe, ~4 MB) and, if it's ≥ 2× the current rate,
  writes `{"base_url": "<url>"}` to the control file. No restart, no lost data. Check at most every 60 s, with a
  10-minute hold-off after a switch.

### 5.3 Concurrency

- **`--max-workers`: 16 by default on every machine** that has ≥ 4 GB RAM; 12 on < 4 GB. Today it's
  `clamp(2×CPU, 6, 16)`, capped at 8 below 8 GB. Workers are network-bound (93–96% blocked on sockets, ~30 MB each), so
  the CPU count is the wrong input. Measured: 8 workers reach the ceiling on this good link, and 16 are needed on a
  high-latency one (§3.5). Above 16 is slightly worse everywhere (§3.1, §3.4, §3.5). Keep the user override (1–32).
- **Adaptive concurrency: not needed for throughput** given the flat optimum around 8–16. Restarting to change
  `--max-workers` is rejected: it costs a partial 4 GiB file plus the cache, which is far more than the 0–10% it
  could gain. With the patch, `max_active` (in-flight chunks, 1 … 2 × workers) can change at runtime. Use it only for
  the Background bandwidth cap (§5.6), with a simple AIMD on the 10 s average rate: −25% when above the cap, +1 when
  below 90% of it.

### 5.4 Legendary fork patch (`legendary-0.21.1-glider`)

Full diff: `tools/bench/legendary-0.21.1-glider.patch` (79+/9−, four files). It's generated by
`py -I tools/bench/patch_legendary.py <checkout>`, which applies it to a clean 0.21.1 checkout and refuses to run twice.
Every change is marked `# glider:`.

| File | Change |
|---|---|
| `downloader/mp/workers.py` | `tries += 1` in the retry loop, backoff `min(2**(tries-1), 4)`; `continue` after the give-up `o_q.put` (no double report) |
| `downloader/mp/manager.py` `__init__` | new kwargs `fallback_urls`, `control_file`; state `base_urls` (selected first), `base_idx`, `cdn_failures`, `max_active` |
| `manager.py` `download_job_manager` | in-flight cap `self.max_active or self.max_workers * 2`; URL from `self.base_urls[self.base_idx]` |
| `manager.py` `dl_results_handler` | on a failed chunk: `_note_cdn_failure(res.url)` (counts only failures from the current CDN; 8 in 30 s → next CDN, log `CDN switch: "a" -> "b" (...)`), then requeue a **fresh** `DownloaderTask` on the current CDN (fixes the worker-killing TypeError) |
| `manager.py` main loop | extra line per interval `[DLManager] INFO:  + Glider\t- {"cdn":…, "cdn_failures_30s":…, "active":…, "max_active":…, "cache_used_mib":…, "cache_total_mib":…, "dl_bps":…, "write_bps":…, "read_bps":…}` (stock lines unchanged, so `ProgressParser` keeps working); `_poll_control()` reads `{"max_active": n, "base_url": url}` when the file's mtime changes |
| `manager.py` `run_real` | workers built with `max_retries=3` |
| `core.py` `prepare_download` | new kwarg `control_file`; passes Epic's full `base_urls` as `fallback_urls` |
| `cli.py` | `--control-file <path>` on install/update/repair |

Not in this patch, worth a second step: the writer sets its own I/O/memory priority from an env var
(`GLIDER_WRITER_PRIORITY=low|verylow`, ~10 lines in `FileWorker.run`, same calls as `lgd_instr.apply_priority`).

**Build and GPLv3**

- Public fork `glider-legendary` (GPLv3, upstream history kept), branch `glider/0.21.1` from tag `0.21.1` + the
  patch, tags `0.21.1-glider.N`. README section "Changes from upstream" listing the patch.
- CI: upstream's own `.github/workflows/build-base.yml` already builds `legendary.exe` on `windows-2025` with
  `actions/setup-python` (version from `pyproject.toml`), `astral-sh/setup-uv`, `uv sync --extra pyinstaller_build
  --no-install-local`, then `uv run --module PyInstaller --onefile --name legendary -i ../assets/windows_icon.ico cli.py`
  with `PYTHONOPTIMIZE=1`. In the fork, keep that workflow, limit the matrix to `windows-2025` (or `windows-latest`),
  and add a release job on tags that uploads `legendary.exe`, its SHA-256 and the exact source tarball of the tag
  (GPLv3 §6: the corresponding source must be available from the same place as the binary). Pin actions by SHA and
  build from `uv.lock` (`uv sync --frozen`).
- Glider: `build/legendary.json` points `url`/`sha256`/`source` at the fork's release asset. Version becomes
  `0.21.1-glider.N`; the `LegendaryVersion` metadata, `LegendaryCli.PinnedSha256` and the pin test change together.
  Third-party notices: say it's a modified Legendary and link the fork. `LICENSE` stays as is.

### 5.5 Shared memory

`--max-shared-memory 1024` by default on every machine (today 1–4 GiB by RAM: 3 GiB here). Measured peak use when the
network is the limit: 40–237 MiB. When the disk is the limit, any size fills, and a bigger one only adds a longer drain
and commit charge (3 GiB here, while the user may be gaming). Keep the existing MemoryError retry
(`LegendaryCli.InstallAsync`) for analyses that need more (patching with heavy chunk reuse), ceiling as today.

### 5.6 Full / Balanced / Background

| | CPU (job object) | Writer process | Other Legendary processes | Bandwidth |
|---|---|---|---|---|
| **Full** | normal | I/O normal, memory 5 | normal | uncapped |
| **Balanced** (default) | normal until `Starting file writing worker...`, then BELOW_NORMAL | I/O **low (1)**, memory **low (2)** | memory low (2) | uncapped |
| **Background** | normal until that line, then IDLE | I/O **very low (0)**, memory **very low (1)** | memory very low (1) | with the patch: cap at 50% of the probe rate via `max_active` (§5.3); stock: uncapped |

- Lower CPU priority **after** start-up. The job object's priority class can be changed while it runs
  (`SetInformationJobObject` with `JOB_OBJECT_LIMIT_PRIORITY_CLASS` again, or clear the flag for normal). That removes
  the measured 12–70 s start-up penalty and keeps the download at 100–106% of Full under load.
- I/O and memory priority from outside: enumerate the job's processes (`QueryInformationJobObject`,
  `JobObjectBasicProcessIdList`), open each with `PROCESS_SET_INFORMATION`, and set `NtSetInformationProcess(h,
  ProcessIoPriority=33, &ULONG)` and `SetProcessInformation(h, ProcessMemoryPriority, &MEMORY_PRIORITY_INFORMATION)`.
  Lowering needs no privilege. The writer is the process created last, after the "Starting file writing worker"
  line. Apply 2 s after the line and re-check once. With the fork, the writer does it itself (§5.4).
- Expected network cost: Balanced 98–106% of Full in the download phase (§3.6, §3.4); target ≥ 85–90% met. The
  foreground benefit (disk latency, standby cache) comes from the writer's I/O/memory priority, which v0.1 never set.

### 5.7 Disk-bound jobs (the case that matters on this PC)

- **Detect** from progress (`ProgressParser`: add `CacheUsedMiB`, `ActiveTasks` to `LegendaryProgress`): disk-bound
  when cache ≥ 85% full for ≥ 10 of the last 15 s, or (with the patch) `cache_used_mib / cache_total_mib` likewise.
  Disk rate = the 30 s average of ` + Disk … (write)`.
- **UI**: in the job card, "Limited by drive D: (≈8 MB/s). Your connection can do ≈40 MB/s." with a link to a short
  explanation: the drive is nearly full / slow at sustained writes; free space or install on a faster drive. Show the
  speed and ETA from **write** progress while disk-bound (today's raw download rate swings 0–30 MiB/s and the ETA is
  wrong; for 6.5 minutes at the end it read 0 MiB/s while still working).
- **Drive choice**: in the install-location picker, show drive type (`MSFT_PhysicalDisk` `MediaType`/`BusType`) and
  % used. Warn on HDDs and on SSDs > 85% full. The user's NVMe here is ~150× faster at sustained writes than D:
  (1,222 vs 8 MB/s), but only has 22 GB free, so the message must point at freeing space, not just "use C:".
- **Updates**: Legendary rewrites every changed file in full (old chunks are copied from the old file into
  `<file>.tmp`, then renamed; `manager.py:393-401`), so disk writes for an update are ~"Install size" while the
  download is "Download size" (both printed in the summary, §4.2 of `legendary.md`). Log `write amplification =
  install / download` per update. Delta manifests and patching are already on; keep them (`--disable-patching`
  would only raise the download). In-place patching (write only changed ranges) would be a large fork change. Decide
  after seeing real amplification numbers in v0.2 logs. A public 34.00/34.10 manifest pair to estimate it offline
  wasn't usable (the archive's files are corrupted).

### 5.8 Smaller fixes

- Stall watchdog: keep 90 s, but restart on the next CDN (§5.2), and log cache usage at the moment of the kill
  (disk-bound stalls are not network stalls; don't restart those at all, just keep waiting while writes progress).
- `--dl-timeout`: keep 10 s. Stalls under 10 s cost little (§3.5).
- Keep-alive: already per worker; nothing to do. Don't pass `--no-https` (no gain; the CDN cost isn't TLS).

## 6. Implementation checklist (engineer)

1. `AutoTune.Compute`: workers = 16 (12 below 4 GB RAM); shared memory = 1024. Update the XML doc and tests. `Settings`
   overrides unchanged.
2. `ProgressParser`: parse ` - Cache usage: X MiB, active tasks: N` into `LegendaryProgress.CacheUsedMiB` /
   `ActiveTasks` (and, for the fork, the ` + Glider\t- {json}` line). Unit tests with the lines in §4.3 of
   `legendary.md` and a captured fork line from `docs/research/fixtures/download-perf/`.
3. `Glider.Core/Legendary/ManifestReader.cs`: minimal reader for the binary manifest (header magic `0x44BEC00C`,
   zlib body if `stored_as & 1`, meta, chunk data list: count, GUIDs, hashes, SHA1s, group numbers, window sizes, file
   sizes). Chunk path `ChunksV4/{group:D2}/{hash:X16}_{guid:X32}.chunk` (feature level ≥ 15). Golden test: compare 20
   paths with `tools/bench/dump_chunks.py` output for a committed small test manifest (don't commit the 10 MB one).
4. `CdnProbe` service (§5.1) + log line; plug into `JobQueue.Args` (`JobQueue.cs:757`) so `InstallArgs.PreferredCdn`
   / a new `BaseUrl` come from the probe unless the user set `PreferredCdn`. `LegendaryCli.InstallArguments`
   (`LegendaryCli.cs:567`) emits `--base-url` when set.
5. Download-mode setting `Full | Balanced | Background` (default Balanced) → `LegendaryCommand` priority profile.
   `WindowsJobObject`: start at normal and switch the job's priority class on the "Starting file writing worker" line.
   New `ProcessPriorities` helper for I/O + memory priority on job processes (P/Invoke in `NativeMethods.cs`).
6. Disk-bound detector + UI state/message + ETA from write progress (§5.7). Contract: add `limitedBy: "disk" |
   "network" | null` and `diskWriteBytesPerSecond` to `JobProgress`.
7. CDN-slow restart for the stock exe (§5.2), at most once per job; stall watchdog restarts on the next CDN.
8. Log per job: probe results, chosen CDN, workers/shm, network-phase rate, disk rate, seconds disk-bound, write
   amplification (updates).
9. Investigate the adopt flow: after `import` of an existing install, verify reported 190 corrupted + 247 missing files
   and a 35 GB repair (§3.3). Check whether the install tags Glider chose match what's on disk (SDL) and whether EGL's
   install was mid-update. Re-downloading 35 GB of a 150 GB install is the single largest time cost the user saw.
10. Fork (after 1–9): create `glider-legendary`, apply `tools/bench/legendary-0.21.1-glider.patch`, CI + release as
    in §5.4, repoint `build/legendary.json`, pass `--control-file %LocalAppData%\Glider\legendary\tmp\Fortnite.control`
    (written atomically: temp + rename), slow-CDN switching and the Background cap through it.

## 7. Re-running the benchmark after implementation

Prerequisites: Python 3.12 venv with `requests pycryptodomex filelock`, the 0.21.1 source (or the fork) checked out
outside the repo, a manifest copied from `%LocalAppData%\Glider\legendary\manifests\` to a scratch folder,
`py -I tools/bench/dump_chunks.py <src> <manifest> chunks.json`. Write only to a scratch folder; keep total traffic
< 20 GB.

1. Ceiling: `dotnet run tools/bench/CdnBench.cs -- --chunks chunks.json --conns 1,4,8,16,32 --seconds 6 --base
   <host1> --base <host2> …` (one call per N with all hosts, as in §3.1). Ceiling = best plateau.
2. Pipeline on a fast disk, real CDN: `legendary_harness.py --base <chosen CDN> --dir <scratch on NVMe>
   --prefix …pakchunk0- --prefix …pakchunk10- --prefix …pakchunk100- --workers 16 --shm-mb 1024`, three runs.
   **Pass: median net MB/s ≥ 90% of the ceiling.** Then once on the real install drive to check that the disk-bound
   detector triggers.
3. End-to-end in Glider: a real update or repair on the fast drive, with the job log's network-phase rate ≥ 90% of
   the ceiling for jobs > 5 GB.
4. Priorities: repeat §3.6 with `cpu_load.py` (Full vs Balanced ≥ 90%, start-up within 2 s of Full). Also measure
   what the priorities are for: while a disk-bound download runs, time a foreground file copy or game load on the same
   drive under Full vs Balanced vs Background.
5. Fork only: failover test from §3.7 (`ChunkServer --reset-pct 100` primary, healthy fallback; must switch within
   15 s and finish) and a control-file switch.
6. Summarise with `py -I tools/bench/summarize_runs.py runs.jsonl` and add the table to this file.

---

## 8. v0.2 implementation and re-run (2026-11-14, same machine)

What shipped (Core, `Glider.Core/Legendary` + `Jobs`): modes applied per process by PID (`RunTuner`, `ModeSettings`, `ProcessRoles`), 16 workers /
1 GiB (`AutoTune`), cache-usage + `+ Glider` line parsing, manifest chunk reader + CDN probe + one failover restart (`ManifestReader`,
`CdnProbe`, `DownloadMonitor`), disk-bound / slow-CDN detection and the limiter sentence (`DiskBoundDetector`, `CdnWatch`,
`LimiterDecision`), speed limit by worker throttling (`SpeedGovernor`), drive hints for the install picker (`DriveHints`), and the fork recipe
(`tools/legendary-fork/`, `.github/workflows/legendary-fork.yml`; not adopted, see DECISIONS).

### 8.1 Method

`DownloadBenchmarkTests` (opt-in, `GLIDER_BENCH_*`) runs Legendary's own DLManager from the 0.21.1 source through **Glider's real process runner**
(`tools/bench/legendary_run.py` is a stand-in for `legendary install` that logs in the real CLI format), so the job object, the tuner, the parser and
the modes are the shipped code. File set: `pakchunk90-` + `pakchunk80-` (818 MiB), real Epic CDN `egdownload.fastly-edge.com`, 16 workers, 1 GiB cache
(256 MiB on D: so the cache can fill). Before every run a 6 s, 16-connection ceiling sample on the same host (`CdnBench`). "Steady" = mean raw rate
of the progress blocks without the first 3 and last 2. Total traffic ≈ 11.5 GB; install folders and scratch files deleted afterwards.

**Caveat: the machine was at 73-100 % CPU the whole time** (other agents were building and testing in parallel) and the CDN drifted from 34 to 24 MB/s
inside one session. Single runs differ by ±20 %, so compare the averages and the ceiling sampled right before each run, not single numbers.

### 8.2 Install on C: (NVMe), network side

| Run | Ceiling right before (MB/s) | Steady raw (MB/s) | % of ceiling | Start → first progress | Process priorities seen mid-run |
|---|---:|---:|---:|---:|---|
| v0.1 settings (BELOW_NORMAL tree), round 1 | 34.7 | 27.8 | 80 % | **13.5 s** | 20 × BelowNormal / I/O 2 / mem 5 |
| **Full**, round 1 | 26.5 | 24.7 | 93 % | 4.2 s | 20 × Normal / 2 / 5 |
| **Balanced**, round 1 | 24.4 | 23.7 | 97 % | 4.6 s | 19 × Normal / 2 / mem 2, **1 × BelowNormal / I/O 1 / mem 2 (the writer)** |
| **Background**, round 1 (workers not capped in this harness) | 24.3 | 22.2 | 91 % | 5.3 s | 20 × Idle / I/O 0 / mem 1 |
| **Balanced**, round 2 | 34.2 | 27.5 | 80 % | 4.4 s | same as above |
| **Full**, round 2 | 28.9 | 25.5 | 88 % | 4.2 s | |
| v0.1 settings, round 2 | 27.4 | 28.8 | 105 % | 4.9 s | |

| Averages of the two rounds | Steady raw | Mean ceiling | % of ceiling |
|---|---:|---:|---:|
| v0.1 | 28.3 | 31.1 | 91 % |
| **Full** | 25.1 | 27.7 | **91 %** (target ≥ 90 %: met on average; single runs 93 % and 88 %) |
| **Balanced** | 25.6 | 29.3 | 87 % |
| Balanced ÷ Full (same metric) | | | **102 %** (target ≥ 85-90 %: met) |

Reading: the network side of Full, Balanced and v0.1 is the same within noise (as §3.6 predicted: workers wait on sockets). What changed is
*what the priorities protect*: Balanced puts exactly one process (the writer) at below-normal CPU / low I/O and every process at low memory priority;
v0.1 left I/O and memory at normal for all 20 processes. Start-up under load went from 13.5 s (BELOW_NORMAL tree) to 4-5 s. Background runs fully idle
but, with the same 16 workers, still reached 91 % of the ceiling; the production Background spawn uses 4 workers (`ModeResourcePolicy.BackgroundWorkers`),
which this harness did not apply, so its cap is not measured here.

### 8.3 Install on D: (DRAM-less SATA SSD, 82 % full), disk-bound check

Cache 256 MiB. Full: steady raw 6.5 MB/s, write 6.7 MB/s, **cache 256/256 MiB full for the whole run**, 139 s for 818 MiB while the ceiling sampled before
it was 34 MB/s. Balanced: 6.2 MB/s, write 6.5 MB/s, cache full, 165 s (the 15.7 s start includes the disk being busy). The full cache is exactly what
`DiskBoundDetector` keys on, so the job card reads "Limited by drive D: (≈6.5 MB/s). Your connection can do ≈34 MB/s." and `DriveSpeeds` remembers
≈6.5 MB/s for D:, which the install picker then shows as "Slow in practice".

### 8.4 CDN probe on the real hosts (3 rounds, the shipped C# probe)

Rounds took **1.3-1.4 s** (a slow host is cut at 1.2 s of warm-up; the first version took 3 s because Akamai's slow edge ate the budget). Verdicts: round 1
fastly 18.7 / akamai 30.5 / cloudfront 29.0 MB/s all ok -> pick fastly (Epic's order); round 2 akamai 12.9 MB/s "slow" vs cloudfront 37.7 -> pick fastly;
round 3 fastly 15.0 "slow" vs cloudfront 49.8 -> pick akamai (the second good host). As §3.1 warned, single 4 MB samples are ±50 %, so the probe flips between equally
good hosts; it excludes the really bad ones (earlier session: Akamai at 3.7 MB/s, TTFB 160 ms).

### 8.5 Speed limit

- **Job Object net-rate control** (`JobObjectNetRateControlInformation`, 32): accepted unelevated, but a job capped at **5 MB/s downloaded at 31.5 MB/s** and one
  capped at 12 MB/s at 27.9 MB/s (unlimited: 26.9). It only shapes outgoing traffic. Rejected.
- **Worker throttling** (kept): duty-cycle suspension of the download-worker processes with feedback on the raw rate. Test (4 real worker processes against a local
  server paced at 10 MB/s per connection, ≈40 MB/s unlimited): limit 10 MB/s -> measured within 5-16 MB/s after ~10 s, and back to full speed within seconds when the
  limit is removed. The feedback law is unit-tested (converges within ±30 % under ±10 % noise). Floor: the duty cycle never goes below 3 %, and a limit far below
  what 16 workers move in one 250 ms slice (< ~2 MB/s) is only approximate; the fork's `max_active` AIMD has no such floor.

### 8.6 Not measured / open

- Foreground benefit (game frame time, disk latency of a competing copy) under Full vs Balanced vs Background: the priorities are applied and verified, the effect
  needs a game and a quiet machine.
- Fork: patch applies cleanly to 0.21.1, the PyInstaller exe builds locally (14.7 MB), reports `0.21.1-glider.1`, shows `--control-file` and honours
  `LEGENDARY_CONFIG_PATH`; the GitHub Actions run itself has not been executed (nothing is pushed), and the failover test of §3.7 was not re-run against the exe.
- A real Epic install/update through the whole app (needs a sign-in).
