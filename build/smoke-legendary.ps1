#requires -Version 7.0
<# Runs the pinned legendary.exe for real (no auth needed) and asserts the outputs Glider depends on. #>
[CmdletBinding()]
param([switch]$Fork)  # test the Glider fork: pin from legendary-fork.json, and its extra option must exist
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$pin  = Get-Content (Join-Path $PSScriptRoot $(if ($Fork) { 'legendary-fork.json' } else { 'legendary.json' })) -Raw | ConvertFrom-Json
$exe  = Join-Path $root 'src/Glider.App/legendary/legendary.exe'
if (-not (Test-Path $exe)) { throw "legendary.exe missing; run: dotnet run build/FetchLegendary.cs" }

$cfg = Join-Path ([IO.Path]::GetTempPath()) "glider-legendary-smoke-$([guid]::NewGuid().ToString('N'))"
New-Item $cfg -ItemType Directory | Out-Null
$env:LEGENDARY_CONFIG_PATH = $cfg
try {
    $ver = (& $exe --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "legendary --version exited $LASTEXITCODE" }
    Write-Host "--version: $ver"
    if ($ver -notmatch [regex]::Escape("version `"$($pin.version)`"")) { throw "Unexpected version output (expected $($pin.version)): $ver" }

    if ($Fork) {
        $help = (& $exe install --help 2>&1 | Out-String)
        if ($help -notmatch '--control-file') { throw "the fork's --control-file option is missing from install --help" }
        foreach ($f in 'glider-legendary.patch', 'SOURCE-OFFER.txt') {
            if (-not (Test-Path (Join-Path (Split-Path $exe) $f))) { throw "$f must ship next to a modified legendary.exe (GPLv3)" }
        }
    }

    $raw = (& $exe status --offline --json 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "legendary status exited $LASTEXITCODE" }
    Write-Host "status: $raw"
    $s = $raw | ConvertFrom-Json
    if ($s.account -ne '<not logged in>') { throw "expected account '<not logged in>', got '$($s.account)'" }
    if ($s.games_installed -ne 0) { throw "expected games_installed 0, got $($s.games_installed)" }
    if ([IO.Path]::GetFullPath($s.config_directory) -ne [IO.Path]::GetFullPath($cfg)) { throw "LEGENDARY_CONFIG_PATH not honoured: $($s.config_directory)" }
    Write-Host 'Legendary smoke test passed.' -ForegroundColor Green
}
finally {
    Remove-Item Env:LEGENDARY_CONFIG_PATH -ErrorAction SilentlyContinue
    Remove-Item $cfg -Recurse -Force -ErrorAction SilentlyContinue
}
