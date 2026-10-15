<#
.SYNOPSIS
    Runs every automated scan for one release (and optionally the updater exe) and prints the manifest "security" block.

.DESCRIPTION
    1. lotr-security vt-scan  on every exec:true file of <ReleaseDir>\manifest.json  -> <ReleaseDir>\security.json
    2. lotr-security vt-scan  on the updater exe (if -UpdaterExe)                     -> same security.json
    3. lotr-security ha-scan  on the updater exe (if -UpdaterExe)                     -> same security.json
    4. lotr-security manifest-block                                                   -> printed for the publish tool
    Services without an API key are skipped with a message (exit 0). Exit code 3 = run again later (quota or an
    analysis still running); progress is saved, re-running the same command resumes.
    REVIEW-AFTER(publish-cli): the publish tool / admin "Modules" screen should call this after publication and merge
    the printed block into manifest.security (then re-sign the manifest, see docs/SECURITY-AUTOMATION.md §3.4).

.EXAMPLE
    .\Invoke-ReleaseScans.ps1 -ReleaseDir C:\www\lotrmp\files\releases\20261015-140000-a1b2c3d4 `
        -UpdaterExe C:\www\lotrmp\files\updater\LotRMPUpdater-0.1.0.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [string]$UpdaterExe,
    [string]$SourceModules,
    [string]$Exe,
    [switch]$Sandbox,
    [switch]$RequireKeys
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Exe) {
    $Exe = @(
        (Join-Path $here 'Lotr.Security\bin\Release\net48\lotr-security.exe'),
        (Join-Path $here 'Lotr.Security\bin\Debug\net48\lotr-security.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $Exe) { throw 'lotr-security.exe not built: dotnet build tools\security\Lotr.Security -c Release' }
}

$manifest = Join-Path $ReleaseDir 'manifest.json'
$out = Join-Path $ReleaseDir 'security.json'
$common = @()
if ($RequireKeys) { $common += '--require-keys' }
$codes = @()

$a = @('vt-scan', '--manifest', $manifest, '--out', $out) + $common
if ($SourceModules) { $a += @('--source-modules', $SourceModules) }
& $Exe @a; $codes += $LASTEXITCODE

if ($UpdaterExe) {
    & $Exe @(@('vt-scan', '--file', $UpdaterExe, '--out', $out) + $common); $codes += $LASTEXITCODE
    $h = @('ha-scan', '--file', $UpdaterExe, '--out', $out) + $common
    if ($Sandbox) { $h += '--sandbox' }
    & $Exe @h; $codes += $LASTEXITCODE
}

if (Test-Path -LiteralPath $out) {
    Write-Host "`nManifest security block (docs/CONTRACTS.md §2):"
    & $Exe manifest-block --in $out
}

# Worst code wins, with "auth/config" > "failed" > "incomplete" > ok.
$final = 0
foreach ($c in 4, 2, 1, 3) { if ($codes -contains $c) { $final = $c; break } }
exit $final
