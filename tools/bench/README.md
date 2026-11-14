# Download benchmarks

Tools behind `docs/download-perf.md`. None of them signs in or reads tokens: Fortnite's chunk CDN is public, and
Legendary's downloader is driven straight from its source with a manifest file and a base URL. Write only to a
scratch folder, delete it afterwards, and keep total traffic small (the study used ~16 GB).

| File | What it does |
|---|---|
| `CdnBench.cs` | Network ceiling: N keep-alive HTTP/1.1 connections fetching real chunks, bytes discarded; `--probe` = the job-start CDN probe of §5.1. Arguments are documented at the top of the file. |
| `ChunkServer.cs` | Local stand-in CDN (one real chunk for every chunk URL) with latency, per-connection cap, link cap, resets and stalls, to simulate Wi‑Fi or a broken CDN. |
| `dump_chunks.py` | Manifest → `chunks.json` (input for `CdnBench.cs`) + summary. |
| `disk_write.py` | Sustained write rate of a folder using Legendary's writer pattern (`--fsync-mb` for the device rate). |
| `legendary_harness.py` | Runs Legendary's own `DLManager` from a 0.21.1 checkout into a scratch folder and reports per-stage timings, cache stalls, connections, retries and the real process priorities; `--plain` runs stock (or patched) classes without timers. |
| `lgd_instr.py` | The instrumented copies of `DLWorker.run` / `FileWorker.run` / `DLManager.download_job_manager` (0.21.1 code + timers) and the Windows priority helpers. |
| `lgd_shim.py` | Makes a source checkout importable in every spawned process (version lookup shim). |
| `legendary_run.py` | A stand-in for `legendary install` (stock DLManager, real CLI log format, no login) that Glider's own process runner can drive; used by `tests/.../Benchmarks/DownloadBenchmarkTests.cs` (opt-in via `GLIDER_BENCH_*`, see the class comment). |
| `make_test_manifest.py` | Writes the small golden manifest for `ManifestReaderTests` with Legendary's own serializer. |
| `cpu_load.py` | Foreground CPU load for the priority tests. |
| `summarize_runs.py` | Table of `legendary_harness.py` results. |
| `patch_legendary.py` / `legendary-0.21.1-glider.patch` | The proposed fork patch (§5.4) and its generated diff. |

## Setup

```
py -m venv <scratch>\venv
<scratch>\venv\Scripts\python -m pip install requests pycryptodomex filelock
git clone --depth 1 --branch 0.21.1 https://github.com/legendary-gl/legendary <scratch>\src\legendary
copy %LocalAppData%\Glider\legendary\manifests\Fortnite_*.manifest <scratch>\fn.manifest
<scratch>\venv\Scripts\python -I tools\bench\dump_chunks.py <scratch>\src\legendary <scratch>\fn.manifest <scratch>\chunks.json
```

## Typical runs

```
# ceiling, all CDNs interleaved per connection count
dotnet run tools/bench/CdnBench.cs -- --chunks chunks.json --conns 8 --seconds 6 --base https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir --base https://egs-cloudfront-chunks.epicgamescdn.com/Builds/Fortnite/CloudDir

# Legendary on the real CDN with v0.1 settings
python -I tools/bench/legendary_harness.py --src <scratch>/src/legendary --manifest fn.manifest --base https://egdownload.fastly-edge.com/Builds/Fortnite/CloudDir --dir <scratch>/inst --prefix FortniteGame/Content/Paks/pakchunk10- --workers 16 --shm-mb 3072 --tree-prio below --json runs.jsonl

# simulated lossy Wi-Fi
dotnet run tools/bench/ChunkServer.cs -- --chunk one.chunk --port 8100 --latency-ms 50 --jitter-ms 20 --connect-ms 150 --conn-mbps 4 --link-mbps 45 --reset-pct 0.2 --stall-pct 0.5
python -I tools/bench/legendary_harness.py ... --base http://127.0.0.1:8100 --workers 16

python -I tools/bench/summarize_runs.py runs.jsonl
```

`one.chunk` is any full 1 MiB chunk (`file_size` 1048642 in `chunks.json`) fetched once from a CDN.
