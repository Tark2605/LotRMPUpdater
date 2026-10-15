<#
.SYNOPSIS
    Collects, read-only, everything Microsoft (or another AV vendor) asks for when reporting a false positive.

.DESCRIPTION
    Prints a text block to paste into https://www.microsoft.com/en-us/wdsi/filesubmission (see
    docs/SECURITY-AUTOMATION.md section 4): file name, size, SHA-256, Authenticode signer, product/version info,
    the local Defender engine/definition versions and, if Defender already detected the file on THIS machine, the
    detection name. It changes nothing (no definition update, no exclusion, no quarantine action).

.EXAMPLE
    .\Get-FalsePositiveEvidence.ps1 -File .\LotRMPUpdater-0.1.0.exe -DownloadUrl https://<domain>/files/updater/LotRMPUpdater-0.1.0.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [string]$DownloadUrl,
    [string]$DetectionName
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$fi = Get-Item -LiteralPath $File
$sha = (Get-FileHash -LiteralPath $fi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$sig = Get-AuthenticodeSignature -LiteralPath $fi.FullName
$vi = $fi.VersionInfo

$defender = $null; $detections = @()
try {
    $defender = Get-MpComputerStatus -ErrorAction Stop
    $threats = @(Get-MpThreatDetection -ErrorAction Stop | Where-Object { $_.Resources -match [regex]::Escape($fi.Name) })
    foreach ($t in $threats) {
        $name = (Get-MpThreat -ThreatID $t.ThreatID -ErrorAction SilentlyContinue).ThreatName
        $detections += "$name (detected $($t.InitialDetectionTime))"
    }
} catch {
    Write-Warning "Defender status not readable here ($($_.Exception.Message)); fill the versions by hand."
}
if ($DetectionName) { $detections += $DetectionName }

$lines = @(
    'Submission type : Software developer -> "Incorrectly detected as malware/malicious"',
    "File name       : $($fi.Name)",
    "Size            : $($fi.Length) bytes",
    "SHA-256         : $sha",
    "Product         : $($vi.ProductName) $($vi.ProductVersion)  (file version $($vi.FileVersion))",
    "Company         : $($vi.CompanyName)",
    "Signature       : $($sig.Status) $(if ($sig.SignerCertificate) { '- ' + $sig.SignerCertificate.Subject } else { '(unsigned)' })",
    "Download URL    : $(if ($DownloadUrl) { $DownloadUrl } else { '<https://<domain>/files/updater/...>' })",
    "Detection name  : $(if ($detections) { $detections -join '; ' } else { '<copy it from Windows Security > Protection history>' })",
    "Defender engine : $(if ($defender) { $defender.AMEngineVersion } else { '?' })",
    "Definitions     : $(if ($defender) { "$($defender.AntivirusSignatureVersion) ($($defender.AntivirusSignatureLastUpdated))" } else { '?' })",
    "VirusTotal      : https://www.virustotal.com/gui/file/$sha",
    "Hybrid Analysis : https://www.hybrid-analysis.com/sample/$sha",
    '',
    'Suggested comment:',
    "  $($fi.Name) is the updater of the LotR MP Event mod for Mount & Blade II: Bannerlord. It downloads",
    '  the mod files from our own server, verifies their SHA-256 against a signed manifest and copies them into the',
    '  game''s Modules folder. It runs without administrator rights, installs no service and sends no telemetry.',
    '  Source and reproducible build instructions: <source link>. We believe this detection is a false positive.'
)
$lines -join [Environment]::NewLine
