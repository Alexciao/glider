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
pin `__version__` to the fork version, `uv sync --frozen` from upstream's `uv.lock`, then **rebuild PyInstaller from its locked sdist so
the bootloader is compiled from source with MSVC** (the check fails if it is byte-identical to the wheel's prebuilt one), and build with
`--onedir --noupx` plus a Windows version resource (`version-info.template.txt`, filled from `forkVersion`). Smoke tests (`--version`,
`install --help` shows `--control-file`, offline `status`, the version resource) run against `dist/legendary/legendary.exe`; the folder is
zipped (`legendary.exe` and `_internal/` at the zip root) and the same checks run on the extracted zip. Uploaded: `legendary-<v>-win64.zip`
and `.zip.sha256`, `legendary.exe.sha256` (the exe inside), the patch, `SOURCE-OFFER.txt` and the corresponding source tarball. A tag
`legendary-fork-<version>` publishes them as one GitHub release (GPLv3 section 6: the source is offered from the same place as the binary).

Why not the one-file exe of glider.1/.2: Windows Defender's ML heuristics flagged it (`Trojan:Win32/Bearfoos.A!ml`), as they do for many
PyInstaller self-extractors, and blocked starts and killed a running repair. A one-file exe unpacks a Python runtime to `%TEMP%` on every
start, ships the same prebuilt bootloader as every other PyInstaller program and carries no version information. The onedir build avoids
the self-extraction, its bootloader is unique to this build, and its metadata says who it is. (No code signing: there is no certificate.)

## Adopting it in Glider

1. Push the tag, wait for the release. Build output differs per run (timestamps), so pin the release's own files, not an earlier
   dispatch run's: put the zip's SHA-256 into `build/legendary-fork.json` (`sha256`, with `url` = the `.zip` asset) and the hash of the
   `legendary.exe` inside it into `exeSha256`.
2. Copy the fork pin's fields over `build/legendary.json` and set `LegendaryCli.PinnedSha256` to `exeSha256` (a test keeps them equal;
   Core hashes `legendary.exe` only, the bootloader. `_internal/` is data that exe loads, covered by the zip hash at build time). The version
   becomes `0.21.1-glider.1`, which switches on the fork features in Core (`LegendaryPin.IsFork`: control file, CDN failover inside
   Legendary, the `+ Glider` line).
3. `dotnet run build/FetchLegendary.cs -- --fork` downloads and verifies the zip, extracts `legendary.exe` + `_internal/` into
   `src/Glider.App/legendary/` and also copies `SOURCE-OFFER.txt` and `glider-legendary.patch` next to the exe, which ships the whole
   folder in the installer. Settings -> About then says "modified by Glider" (`Licenses`).

Until step 1 is done Glider ships the stock exe and everything above is inert.
