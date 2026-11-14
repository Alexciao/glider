#requires -Version 7.0
<#
.SYNOPSIS
  Builds Glider end to end: contract check, frontend, Legendary, publish, and (optionally) Velopack packages.
.PARAMETER Configuration  MSBuild configuration for publish (default Release).
.PARAMETER Version        Package/assembly version, SemVer2 (default 0.1.0).
.PARAMETER Pack           Also run `vpk pack` -> artifacts/releases.
.PARAMETER Fork           Bundle the Glider fork of Legendary (build/legendary-fork.json) with its patch and source offer instead of the stock exe.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Version = '0.1.0',
    [switch] $Pack,
    [switch] $Fork
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

function Step([string] $Title) { Write-Host "`n==> $Title" -ForegroundColor Cyan }

# Runs a native command and fails the script on a non-zero exit code.
function Exec([string] $Exe, [string[]] $Arguments, [string] $WorkDir = $root) {
    Push-Location $WorkDir
    try {
        & $Exe @Arguments
        if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

function Require([string] $Tool, [string] $Hint) {
    if (-not (Get-Command $Tool -ErrorAction SilentlyContinue)) { throw "'$Tool' not found on PATH. $Hint" }
}

if ($Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.\-+]+)?$') { throw "Invalid -Version '$Version' (expected SemVer, e.g. 1.2.3 or 0.1.0-ci.5)" }
Require dotnet 'Install the .NET 10 SDK (see global.json).'
Require pnpm   'Install pnpm (corepack enable, or https://pnpm.io/installation).'

$frontend = Join-Path $root 'frontend'
$wwwroot  = Join-Path $root 'src/Glider.App/wwwroot'
$publish  = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'
if (-not (Test-Path (Join-Path $frontend 'package.json'))) { throw "frontend/package.json not found." }

Step 'Contract check (contract.gen.ts is up to date)'
Exec dotnet @('run', '--project', 'tools/Glider.ContractGen', '-c', $Configuration, '--', '--check')

Step 'Frontend: pnpm install + build'
Exec pnpm @('install', '--frozen-lockfile') $frontend
Exec pnpm @('build') $frontend
$dist = Join-Path $frontend 'dist'
if (-not (Test-Path (Join-Path $dist 'index.html'))) { throw "frontend build produced no dist/index.html" }

Step 'Copy frontend/dist -> src/Glider.App/wwwroot'
if (Test-Path $wwwroot) { Remove-Item $wwwroot -Recurse -Force }
New-Item $wwwroot -ItemType Directory | Out-Null
Copy-Item (Join-Path $dist '*') $wwwroot -Recurse -Force

Step 'Fetch Legendary (hash-verified)'
$fetch = @('run', 'build/FetchLegendary.cs', '--') + $(if ($Fork) { @('--fork') } else { @() }) + @("$root")
Exec dotnet $fetch

Step "Publish (win-x64, self-contained, ReadyToRun, v$Version)"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
Exec dotnet @('publish', 'src/Glider.App', '-c', $Configuration, '-r', 'win-x64', '--self-contained',
    '-p:PublishReadyToRun=true', '-p:SatelliteResourceLanguages=en', '-p:DebugType=none',
    "-p:Version=$Version", '-o', $publish)

if (-not (Test-Path (Join-Path $publish 'Glider.exe'))) { throw "publish did not produce Glider.exe" }
if (-not (Test-Path (Join-Path $publish 'legendary/legendary.exe'))) { throw "publish output is missing legendary/legendary.exe (host csproj must include it as content)" }
if (-not (Test-Path (Join-Path $publish 'wwwroot/index.html'))) { throw "publish output is missing wwwroot/index.html (host csproj must include wwwroot/** as content)" }

if ($Pack) {
    Step "Velopack pack (v$Version)"
    $icon = @('assets/brand/glider.ico', 'src/Glider.App/Assets/app.ico') |
        ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $icon) { throw "No icon found (looked for assets/brand/glider.ico and src/Glider.App/Assets/app.ico)" }

    Exec dotnet @('tool', 'restore')
    if (Test-Path $releases) { Remove-Item $releases -Recurse -Force }
    Exec dotnet @('vpk', 'pack', '--packId', 'Glider', '--packTitle', 'Glider', '--packAuthors', 'Glider contributors',
        '--packVersion', $Version, '--packDir', $publish, '--mainExe', 'Glider.exe', '--icon', $icon,
        '--framework', 'webview2', '--outputDir', $releases)
}

Step 'Artifact sizes'
function Show-Size([string] $Path, [string] $Label) {
    if (Test-Path $Path) { '{0,-34} {1,8:N1} MB' -f $Label, ((Get-ChildItem $Path -Recurse -File | Measure-Object Length -Sum).Sum / 1MB) }
}
Show-Size $publish 'publish dir'
if ($Pack) {
    Get-ChildItem $releases -File | Sort-Object Length -Descending |
        ForEach-Object { '{0,-34} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
}
Write-Host "`nDone." -ForegroundColor Green
