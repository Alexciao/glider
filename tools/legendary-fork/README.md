# Glider fork of Legendary

Legendary 0.21.1 plus one small, tested patch (`../bench/legendary-0.21.1-glider.patch`, +79/-9, four files, every change marked
`# glider:`). Why and what it does: `docs/download-perf.md` section 3.7 (failure handling in 0.21.1) and section 5.4.

| Change | Why |
|---|---|
| `workers.py`: the retry loop counts its tries; no double report on give-up | 0.21.1 retried a failing chunk forever, back to back |
| `manager.py`: a failed chunk is requeued as a **fresh** task | requeueing the result object raised a `TypeError` that silently killed the worker |
| `manager.py`: CDN failover (8 failed chunks in 30 s -> next CDN of Epic's list), `--control-file` polling (`{"max_active": n, "base_url": url}`), one `+ Glider` JSON line per progress block | a dead CDN no longer stalls the job; Glider can change CDN or concurrency live |
| `core.py` / `cli.py`: `--control-file`, Epic's full CDN list as failover order | plumbing |

## Build

`.github/workflows/legendary-fork.yml` (windows-latest): fetch upstream at the commit in `PIN.json`, check the patch hash, `git apply`,
pin `__version__` to the fork version, `uv sync --frozen` from upstream's `uv.lock`, PyInstaller one-file build exactly like upstream's
`build-base.yml`, smoke test (`--version`, `install --help` shows `--control-file`), then upload `legendary.exe`, its SHA-256, the patch,
`SOURCE-OFFER.txt` and the corresponding source tarball. A tag `legendary-fork-<version>` publishes them as one GitHub release
(GPLv3 section 6: the source is offered from the same place as the binary).

## Adopting it in Glider

1. Push the tag, wait for the release. Copy the exe's SHA-256 into `build/legendary-fork.json` (`sha256`).
2. Copy the fork pin's fields over `build/legendary.json` and set `LegendaryCli.PinnedSha256` (a test keeps them equal). The version
   becomes `0.21.1-glider.1`, which switches on the fork features in Core (`LegendaryPin.IsFork`: control file, CDN failover inside
   Legendary, the `+ Glider` line).
3. `dotnet run build/FetchLegendary.cs -- --fork` also copies `SOURCE-OFFER.txt` and `glider-legendary.patch` next to the exe
   (`src/Glider.App/legendary/`), which ships them in the installer. Settings -> About then says "modified by Glider" (`Licenses`).

Until step 1 is done Glider ships the stock exe and everything above is inert.
