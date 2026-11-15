# Build, package, release

Requirements: Windows, .NET 10 SDK (see `global.json`), PowerShell 7, Node 24, pnpm (version pinned by
`frontend/package.json` `packageManager`). No Python.

## Build locally

```powershell
./build/build.ps1                       # contract check, frontend, Legendary, publish -> artifacts/publish
./build/build.ps1 -Pack -Version 0.2.0  # ... and Velopack packages -> artifacts/releases
```

Switches: `-Configuration` (default `Release`), `-Version` (SemVer, default `0.1.0`), `-Pack`.
The script fails fast on the first error and prints artifact sizes at the end.

## What each file does

| File | Purpose |
|---|---|
| `build/build.ps1` | Orchestrates: `ContractGen --check` → `pnpm install --frozen-lockfile` + `pnpm build` → copy `frontend/dist` to `src/Glider.App/wwwroot` (cleaned first) → `FetchLegendary` → `dotnet publish` (win-x64, self-contained, ReadyToRun) → with `-Pack`, `vpk pack --framework webview2`. |
| `build/FetchLegendary.cs` | `dotnet run build/FetchLegendary.cs`. Downloads the Legendary onedir zip and its GPLv3 text pinned in `build/legendary.json`, verifies the zip's SHA-256 (hard failure on mismatch), extracts it to `src/Glider.App/legendary/` (`legendary.exe` + `_internal/`), verifies the extracted exe against `exeSha256`, and writes `LICENSE.txt`. Skips the download when the exe already matches. A pin whose `url` is a lone `.exe` (a one-file build) still works. |
| `build/legendary.json` | The pin: version, zip URL, `sha256` of the zip, `exeSha256` of the `legendary.exe` inside it (what Core checks; a test keeps `LegendaryCli.PinnedSha256` equal), license URL and SHA-256. To bump Legendary, change all of these together. |
| `build/legendary-fork.json` | Candidate pin for the Glider fork of Legendary (`tools/legendary-fork/`, built by `.github/workflows/legendary-fork.yml`). `sha256` is `null` until the workflow has published a release; `FetchLegendary --fork` / `build.ps1 -Fork` / `smoke-legendary.ps1 -Fork` use it and also ship `glider-legendary.patch` + `SOURCE-OFFER.txt` (GPLv3). |
| `build/verify-legendary-isolation.ps1` | Proves the bundle needs no Python and an installed Python can't interfere: relocated copy in a non-ASCII folder, `PATH` = System32 only, `PYTHONHOME`/`PYTHONPATH`/`PYTHONSTARTUP` and decoy `ssl.py`/`requests.py`/`site.py`... in the cwd, then `--version` and an online `status` (verified HTTPS) must succeed. Runs in both workflows (the runners have a Python). |
| `build/smoke-legendary.ps1` | Runs the real `legendary.exe` with an isolated `LEGENDARY_CONFIG_PATH` (`--version`, `status --offline --json`) and asserts the output. Needs no login. |
| `.config/dotnet-tools.json` | Pins `vpk` 1.2.161 (keep equal to the `Velopack` package version). Run as `dotnet vpk`. |
| `.github/workflows/ci.yml` | Every push/PR: build with `-warnaserror`, tests, contract check, frontend lint/typecheck/test, Legendary smoke test, then `build.ps1 -Pack -Version 0.1.0-ci.<run>`; uploads `artifacts/releases/*`. |
| `.github/workflows/release.yml` | On tag `v*`: tests, build with `-Version <tag without v>`, `gh release create` with all Velopack files attached. |

## What the host csproj must provide

- `src/Glider.App/wwwroot/**` and `src/Glider.App/legendary/**` (both produced by the scripts, gitignored) as content copied to the publish output.
- `Program.Main` calling `VelopackApp.Build().Run()` first (`vpk pack` refuses to pack otherwise).
- Icon at `assets/brand/glider.ico` (preferred) or `src/Glider.App/Assets/app.ico`.

## Cut a release

1. Make sure `main` is green.
2. `git tag v0.2.0 && git push origin v0.2.0` (a `-` in the tag, e.g. `v0.2.0-beta.1`, publishes a prerelease).
3. `release.yml` builds, packs and creates the GitHub Release with `Glider-win-Setup.exe`, `Glider-<v>-full.nupkg`,
   `Glider-win-Portable.zip`, `releases.win.json`, `assets.win.json`, `RELEASES`.

Installers are unsigned (no signing parameters are passed), so SmartScreen will warn until signing is added.
