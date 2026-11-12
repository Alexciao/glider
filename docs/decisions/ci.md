# CI and packaging decisions

- **`pnpm/action-setup` reads the pnpm version from `frontend/package.json` `packageManager`**: one source of truth, no drift between local and CI.
- **Legendary is fetched by a .NET file-based app (`build/FetchLegendary.cs`), not Python/PowerShell**: the same SDK we already need, SHA-256 pinned in `build/legendary.json` (exe and GPL license text), idempotent, fails hard on mismatch.
- **CI runs the real `legendary.exe` (`--version`, `status --offline --json`)**: our parsers depend on exact output shapes, and this runs with no credentials.
- **`vpk` is a local dotnet tool pinned in `.config/dotnet-tools.json` (1.2.161)**: matches the Velopack package version, reproducible on any machine; invoked as `dotnet vpk`.
- **Installer built with `--framework webview2` only**: self-contained app, so no .NET desktop runtime prerequisite.
- **Release uses `gh release create` with every file in `artifacts/releases`**: Velopack's GithubSource needs `releases.win.json` and all nupkgs; `vpk upload github` was not chosen because we build on one runner and have no previous releases to download for deltas yet.
- **CI package version is `0.1.0-ci.<run_number>`; release version is the tag minus `v`**: tags with a `-` are published as prereleases.
- **Actions pinned to current majors** (checkout v7, setup-dotnet v6, setup-node v7, pnpm/action-setup v6, upload-artifact v7), checked 2026-11-12.
