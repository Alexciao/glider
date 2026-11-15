#requires -Version 7.0
<#
.SYNOPSIS
  Proves the bundled Legendary is self-contained: it needs no Python install and an installed Python (or its environment variables) cannot reach it.
.DESCRIPTION
  End users never need Python, and a Python they do have must never be used. The bundle (PyInstaller --onedir: legendary.exe + _internal/)
  carries its own interpreter DLL, VC++ runtime, OpenSSL, _ctypes and the certifi CA bundle. This script copies the bundle into a folder whose
  name has non-ASCII characters (also proving it is relocatable) and runs it with a scrubbed environment: PATH is only %SystemRoot%\System32,
  no registry/PATH/Store-python dependence, and PYTHONHOME / PYTHONPATH / PYTHONSTARTUP pointing at a decoy folder whose ssl.py, requests.py,
  encodings, site and sitecustomize modules would print HIJACKED if Python ever imported them (they are also in the working directory).
  Then it asserts `--version` shows the pinned fork version and an ONLINE `status` (HTTPS to api.legendary.gl, certificate verification on)
  ends with "Update available:". Run it where a Python is installed (a CI runner, a developer PC): that is the interference case.
.PARAMETER Exe      The bundled legendary.exe (default: src/Glider.App/legendary/legendary.exe).
.PARAMETER Version  The version `--version` must show (default: the one in build/legendary.json).
#>
[CmdletBinding()]
param([string] $Exe, [string] $Version)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $Exe) { $Exe = Join-Path $root 'src/Glider.App/legendary/legendary.exe' }
$Exe = (Resolve-Path $Exe).Path
$srcDir = Split-Path $Exe
if (-not $Version) { $Version = (Get-Content (Join-Path $PSScriptRoot 'legendary.json') -Raw | ConvertFrom-Json).version }

# 1. The runtime is in the bundle, not borrowed from the machine.
$internal = Join-Path $srcDir '_internal'
if (-not (Test-Path $internal)) { throw "no _internal folder next to $Exe" }
foreach ($pattern in 'python3*.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', '_ssl.pyd', '_hashlib.pyd', '_ctypes.pyd', '_multiprocessing.pyd', 'libcrypto-*.dll', 'libssl-*.dll', 'base_library.zip') {
    if (-not (Get-ChildItem $internal -Filter $pattern -File | Select-Object -First 1)) { throw "bundle is missing _internal\$pattern" }
}
if (-not (Test-Path (Join-Path $internal 'certifi/cacert.pem'))) { throw "bundle is missing the certifi CA bundle" }
Write-Host 'bundle contents: ok (interpreter DLL, VC++ runtime, OpenSSL, _ssl/_hashlib/_ctypes/_multiprocessing, certifi)'

# 2. Run a relocated copy in a non-ASCII folder with a hostile environment.
$tag = [string]::Concat([char]0x65E5, [char]0x672C, [char]0x8A9E, '-', [char]0x00E9, [char]0x00E8)
$work = Join-Path ([IO.Path]::GetTempPath()) "glider-isolation-$tag-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
New-Item $work -ItemType Directory | Out-Null
try {
    $app = Join-Path $work 'app'
    Copy-Item $srcDir $app -Recurse
    $decoy = Join-Path $work 'decoy'; $cwd = Join-Path $work 'cwd'
    $cfg = Join-Path $work ("config-" + [char]0x00FC + [char]0x65E5)
    New-Item $decoy, $cwd, $cfg -ItemType Directory | Out-Null
    foreach ($m in 'ssl', 'requests', 'encodings', 'site', 'sitecustomize', 'usercustomize', 'legendary') {
        foreach ($d in $decoy, $cwd) { Set-Content (Join-Path $d "$m.py") 'import sys; print("HIJACKED"); sys.exit(99)' }
    }

    function Invoke-Scrubbed([string[]] $Arguments) {
        $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'legendary.exe'))
        foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
        $psi.WorkingDirectory = $cwd
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $psi.Environment.Clear()
        $sys32 = Join-Path $env:SystemRoot 'System32'
        $vars = @{
            SystemRoot = $env:SystemRoot; PATH = $sys32; TEMP = [IO.Path]::GetTempPath(); TMP = [IO.Path]::GetTempPath()
            USERPROFILE = $env:USERPROFILE; LOCALAPPDATA = $env:LOCALAPPDATA; APPDATA = $env:APPDATA
            PYTHONHOME = 'C:\no-such-python'; PYTHONPATH = $decoy; PYTHONSTARTUP = (Join-Path $decoy 'sitecustomize.py'); PYTHONUTF8 = '0'
            LEGENDARY_CONFIG_PATH = $cfg
        }
        foreach ($k in $vars.Keys) { $psi.Environment[$k] = $vars[$k] }
        $p = [Diagnostics.Process]::Start($psi)
        $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
        if (-not $p.WaitForExit(120000)) { $p.Kill($true); throw "legendary $($Arguments -join ' ') did not finish in 120 s" }
        [pscustomobject]@{ ExitCode = $p.ExitCode; Text = ($out.Result + $err.Result) }
    }

    $v = Invoke-Scrubbed @('--version')
    Write-Host "--version: $($v.Text.Trim())"
    if ($v.ExitCode -ne 0 -or $v.Text -match 'HIJACKED' -or $v.Text -notmatch [regex]::Escape("version `"$Version`"")) { throw "scrubbed --version failed (exit $($v.ExitCode)): $($v.Text)" }

    $s = Invoke-Scrubbed @('status')
    $lines = $s.Text.Trim() -split "`r?`n" | Where-Object { $_.Trim() }
    Write-Host "status (online): $($lines[-2]) | $($lines[-1])"
    if ($s.ExitCode -ne 0 -or $s.Text -match 'HIJACKED') { throw "scrubbed status failed (exit $($s.ExitCode)): $($s.Text)" }
    if ($lines[-1] -notmatch '^Update available:') { throw "online status did not reach api.legendary.gl over verified HTTPS (last line: '$($lines[-1])')" }
    Write-Host 'Legendary isolation check passed (no Python install needed, none used).' -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
