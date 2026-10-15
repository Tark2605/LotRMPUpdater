<#
.SYNOPSIS
    Verifies the Authenticode signature of the updater exe (Get-AuthenticodeSignature + signtool verify /pa when present).

.DESCRIPTION
    Default (release gate): the signature must be Valid, timestamped, and NOT the development test certificate.
    -AllowTestCert: accept the development test certificate (status UnknownError/NotTrusted is then expected).
    -ExpectedSubject: substring the signer subject must contain (e.g. 'SignPath Foundation' or your Trusted Signing name).
    -Json: print the report as JSON (for the publish tool / site).
    Exit code 0 = acceptable, 1 = not acceptable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [switch]$AllowTestCert,
    [string]$ExpectedSubject,
    [string]$SignToolPath,
    [switch]$Json
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SigningCommon.psm1') -Force

$rep = Get-LotrSignatureReport $File $SignToolPath
$problems = New-Object System.Collections.Generic.List[string]
if ($rep.Status -eq 'NotSigned') { $problems.Add('file is not signed') }
elseif ($rep.Status -eq 'HashMismatch') { $problems.Add('file was modified after signing (HashMismatch)') }
elseif ($rep.IsTestCertificate) {
    if (-not $AllowTestCert) { $problems.Add('signed with the DEVELOPMENT test certificate - never publish this build') }
} elseif ($rep.Status -ne 'Valid') { $problems.Add("signature status $($rep.Status): $($rep.StatusMessage)") }
if (-not $rep.IsTestCertificate -and $rep.Status -ne 'NotSigned' -and -not $rep.Timestamped) { $problems.Add('signature is not timestamped (it would stop verifying when the certificate expires)') }
if ($ExpectedSubject -and ($null -eq $rep.Signer -or $rep.Signer -notlike "*$ExpectedSubject*")) { $problems.Add("signer '$($rep.Signer)' does not contain '$ExpectedSubject'") }

$rep | Add-Member -NotePropertyName Acceptable -NotePropertyValue ($problems.Count -eq 0)
$rep | Add-Member -NotePropertyName Problems -NotePropertyValue @($problems)
if ($Json) { $rep | Select-Object * -ExcludeProperty SignToolOutput | ConvertTo-Json -Depth 3 }
else {
    $rep | Format-List File, Sha256, Status, Signer, Issuer, Thumbprint, NotAfter, Timestamped, TimestampSigner, IsTestCertificate, SignToolVerify, Acceptable
    foreach ($p in $problems) { Write-Warning $p }
}
if ($problems.Count -gt 0) { exit 1 } else { exit 0 }
