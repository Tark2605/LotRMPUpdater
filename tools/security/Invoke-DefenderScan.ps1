<#
.SYNOPSIS
    Scans a published release (and optionally the updater exe) with the local Microsoft Defender Antivirus and writes
    <ReleaseDir>\defender.json. No API key, no quota, nothing leaves the server.

.DESCRIPTION
    1. Reads <ReleaseDir>\manifest.json and checks that every file of every module exists under
       <SourceModules>\<folder>\ with the SIZE and SHA-256 the manifest lists (so what Defender scans is exactly what
       players download; the release objects themselves are compressed and cannot be scanned meaningfully).
    2. Runs "MpCmdRun.exe -Scan -ScanType 3 -File <folder> -DisableRemediation" on each module folder, then on the
       updater exe if -UpdaterExe. -DisableRemediation: Defender reports but never quarantines or deletes anything.
    3. Writes <ReleaseDir>\defender.json (unsigned sidecar next to security.json, CONTRACTS section 8): engine and
       signature versions, per-module result, files verified, threats found.
    Exit codes: 0 clean (or skipped: Defender not available), 2 threats found, 1 error.
    Run elevated (Invoke-ReleaseScans.ps1 / "lotr-publish scan" do it from an elevated prompt).

.EXAMPLE
    .\Invoke-DefenderScan.ps1 -ReleaseDir C:\www\lotrmp\files\releases\20261018-150000-a1b2c3d4 `
        -SourceModules C:\www\lotrmp\team-repo -UpdaterExe C:\www\lotrmp\files\updater\LotRMPUpdater-0.1.0.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [Parameter(Mandatory = $true)][string]$SourceModules,
    [string]$UpdaterExe,
    [switch]$SkipHashCheck
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $ReleaseDir 'manifest.json'
$outPath = Join-Path $ReleaseDir 'defender.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "manifest.json not found in $ReleaseDir" }
$manifest = [System.IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json

function Write-Result($obj) {
    $json = ($obj | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
    $tmp = "$outPath.tmp"
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
    if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }
    Move-Item -LiteralPath $tmp -Destination $outPath
    Write-Host "defender: wrote $outPath"
}

$result = [ordered]@{
    schema = 1; releaseId = [string]$manifest.releaseId; scannedAt = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    status = 'done'; result = 'clean'; engine = $null; modules = @(); updater = $null; note = $null
}

# Defender available and enabled?
$mp = $null
try { $mp = Get-MpComputerStatus -ErrorAction Stop } catch { $mp = $null }
$cmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not $mp -or -not $mp.AntivirusEnabled -or -not (Test-Path -LiteralPath $cmd)) {
    $result.status = 'skipped'; $result.result = $null
    $result.note = 'Microsoft Defender Antivirus is not available or not enabled on this server.'
    Write-Warning "defender: skipped - $($result.note)"
    Write-Result $result
    exit 0
}
$result.engine = [ordered]@{
    product = 'Microsoft Defender Antivirus'; engineVersion = [string]$mp.AMEngineVersion
    signatureVersion = [string]$mp.AntivirusSignatureVersion
    signatureUpdated = ([datetime]$mp.AntivirusSignatureLastUpdated).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}
Write-Host "defender: engine $($result.engine.engineVersion), signatures $($result.engine.signatureVersion) ($($result.engine.signatureUpdated))"

function Invoke-MpScan([string]$Target) {
    $out = @(& $cmd -Scan -ScanType 3 -File $Target -DisableRemediation 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    # MpCmdRun: 0 = no threat, 2 = threats found (listed as "Threat  : <name>" lines), anything else = scan error.
    $threats = @($out | Where-Object { $_ -match '^\s*Threat\s*:' } | ForEach-Object { ($_ -replace '^\s*Threat\s*:\s*', '').Trim() } | Select-Object -Unique)
    return [pscustomobject]@{ Code = $code; Threats = $threats; Output = $out }
}

$sha = [System.Security.Cryptography.SHA256]::Create()
$worst = 0
foreach ($m in @($manifest.modules)) {
    $folder = Join-Path $SourceModules ([string]$m.folder)
    $entry = [ordered]@{ folder = [string]$m.folder; id = [string]$m.id; version = [string]$m.version; files = 0; bytes = [long]0
        verified = 0; mismatched = 0; missing = 0; result = $null; threats = @(); exitCode = $null }
    foreach ($f in @($m.files)) {
        $entry.files++; $entry.bytes += [long]$f.size
        $p = Join-Path $folder (([string]$f.path).Replace('/', '\'))
        if (-not [System.IO.File]::Exists($p)) { $entry.missing++; continue }
        if ((New-Object System.IO.FileInfo $p).Length -ne [long]$f.size) { $entry.mismatched++; continue }
        if ($SkipHashCheck) { $entry.verified++; continue }
        $s = [System.IO.File]::OpenRead($p)
        try { $h = -join ($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') }) } finally { $s.Dispose() }
        if ($h -eq ([string]$f.sha256).ToLowerInvariant()) { $entry.verified++ } else { $entry.mismatched++ }
    }
    Write-Host "defender: $($entry.folder): $($entry.verified)/$($entry.files) files match the release ($($entry.mismatched) differ, $($entry.missing) missing)"
    if ($entry.verified -ne $entry.files) {
        # The folder is not exactly the published content: scanning it would prove nothing about the release.
        $entry.result = 'not-scanned'
        $result.status = 'error'
        $result.note = "Source folder $folder does not match the release: copy the exact published module there and scan again."
        $worst = [Math]::Max($worst, 1)
        $result.modules += $entry
        continue
    }
    Write-Host "defender: scanning $folder ..."
    $scan = Invoke-MpScan $folder
    $entry.exitCode = $scan.Code
    $entry.threats = @($scan.Threats)
    if ($scan.Code -eq 0) { $entry.result = 'clean' }
    elseif ($scan.Code -eq 2) { $entry.result = 'threats'; $result.result = 'threats'; $worst = 2 }
    else { $entry.result = 'error'; $result.status = 'error'; $worst = [Math]::Max($worst, 1); $scan.Output | Select-Object -Last 5 | ForEach-Object { Write-Warning $_ } }
    Write-Host "defender: $($entry.folder): $($entry.result)$(if ($entry.threats.Count) { ' - ' + ($entry.threats -join ', ') })"
    $result.modules += $entry
}

if ($UpdaterExe) {
    $s = [System.IO.File]::OpenRead($UpdaterExe)
    try { $h = -join ($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') }) } finally { $s.Dispose() }
    $scan = Invoke-MpScan ([System.IO.Path]::GetFullPath($UpdaterExe))
    $u = [ordered]@{ file = [System.IO.Path]::GetFileName($UpdaterExe); sha256 = $h; exitCode = $scan.Code; threats = @($scan.Threats); result = $null }
    if ($scan.Code -eq 0) { $u.result = 'clean' }
    elseif ($scan.Code -eq 2) { $u.result = 'threats'; $result.result = 'threats'; $worst = 2 }
    else { $u.result = 'error'; $result.status = 'error'; $worst = [Math]::Max($worst, 1) }
    Write-Host "defender: $($u.file): $($u.result)"
    $result.updater = $u
}
$sha.Dispose()
if ($result.status -eq 'error' -and $result.result -eq 'clean') { $result.result = $null }
Write-Result $result
exit $worst
