<#
.SYNOPSIS
    Authenticode-signs the updater exe with one of three providers, timestamps it and verifies the result.

.DESCRIPTION
    Order in the release pipeline: build (reproducible) -> Sign-Updater.ps1 -> lotr-security vt-scan / ha-scan on the
    SIGNED exe (players download the signed bytes, so that is the hash the scans and latest.json must show).

    Providers
      TestCert             DEVELOPMENT ONLY. Self-signed PFX from New-TestSigningCertificate.ps1, signed with
                           Set-AuthenticodeSignature (no SDK needed). Refused with -Release.
      AzureTrustedSigning  signtool + Azure.CodeSigning.Dlib.dll + metadata.json (templates\trusted-signing-metadata.json).
                           Authentication: DefaultAzureCredential (az login, or AZURE_TENANT_ID / AZURE_CLIENT_ID /
                           AZURE_CLIENT_SECRET of an app registration with the "Trusted Signing Certificate Profile
                           Signer" role). Timestamp: http://timestamp.acs.microsoft.com (mandatory: certs live 3 days).
      SignPath             Local submission through the SignPath PowerShell module, for the TEST-SIGNING policy only.
                           Release signing by the SignPath Foundation is accepted only from the GitHub Actions workflow
                           (templates\signpath-github-workflow.yml), so -Release with SignPath is refused here.

    Exit: throws (non-zero) on any failure, including a signature that does not verify as expected.

.EXAMPLE
    .\New-TestSigningCertificate.ps1
    .\Sign-Updater.ps1 -File ..\..\..\out\LotRMPUpdater.exe -Provider TestCert
.EXAMPLE
    .\Sign-Updater.ps1 -File LotRMPUpdater.exe -Provider AzureTrustedSigning -Release `
        -DlibPath C:\tools\TrustedSigning\bin\x64\Azure.CodeSigning.Dlib.dll -MetadataPath C:\lotrmp\data\secrets\trusted-signing-metadata.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$File,
    [Parameter(Mandatory = $true)][ValidateSet('TestCert', 'AzureTrustedSigning', 'SignPath')][string]$Provider,
    [switch]$Release,
    [switch]$NoTimestamp,
    [string]$TimestampUrl,
    [string]$SignToolPath,
    [switch]$WriteSha256,
    # TestCert
    [string]$PfxPath = (Join-Path $env:LOCALAPPDATA 'LotRMP\test-signing\lotr-test-signing.pfx'),
    # AzureTrustedSigning
    [string]$DlibPath = $env:TRUSTED_SIGNING_DLIB,
    [string]$MetadataPath = $env:TRUSTED_SIGNING_METADATA,
    # SignPath (test-signing policy, local)
    [string]$SignPathOrganizationId = $env:SIGNPATH_ORGANIZATION_ID,
    [string]$SignPathProjectSlug = 'LotRMPUpdater',
    [string]$SignPathPolicySlug = 'test-signing',
    [string]$SignPathArtifactConfigurationSlug
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SigningCommon.psm1') -Force

$File = (Resolve-Path -LiteralPath $File).Path
if ($Release -and $NoTimestamp) { throw '-Release requires a timestamp (remove -NoTimestamp).' }
if (-not $TimestampUrl) { $TimestampUrl = Get-LotrTimestampUrl $Provider }

switch ($Provider) {
    'TestCert' {
        if ($Release) { throw 'Refusing: the test certificate is for development only and must never sign a release.' }
        if (-not (Test-Path -LiteralPath $PfxPath)) { throw "Test PFX not found: $PfxPath. Run New-TestSigningCertificate.ps1 first." }
        $password = [System.IO.File]::ReadAllText("$PfxPath.password")
        # Key loaded for this process only (no PersistKeySet, no store import).
        $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $password, $flags)
        try {
            Write-Warning "Signing with the DEVELOPMENT test certificate ($($cert.Subject)). Not for release."
            $tool = Find-LotrSignTool $SignToolPath
            if ($tool) {
                $a = @('sign', '/fd', 'SHA256', '/f', $PfxPath, '/p', $password)
                if (-not $NoTimestamp) { $a += @('/tr', $TimestampUrl, '/td', 'SHA256') }
                $a += $File
                # Do not echo the password.
                & $tool @a
                if ($LASTEXITCODE -ne 0) { throw "signtool sign failed with exit code $LASTEXITCODE" }
            } else {
                $p = @{ LiteralPath = $File; Certificate = $cert; HashAlgorithm = 'SHA256' }
                if (-not $NoTimestamp) { $p.TimestampServer = $TimestampUrl }
                $r = Set-AuthenticodeSignature @p
                if ($r.Status -eq 'HashMismatch' -or -not $r.SignerCertificate) { throw "Set-AuthenticodeSignature failed: $($r.StatusMessage)" }
            }
        } finally { $cert.Dispose() }
    }
    'AzureTrustedSigning' {
        $tool = Find-LotrSignTool $SignToolPath
        if (-not $tool) { throw 'signtool.exe not found. Install the Windows SDK "Signing Tools" or the NuGet package Microsoft.Windows.SDK.BuildTools, or pass -SignToolPath.' }
        if (-not $DlibPath -or -not (Test-Path -LiteralPath $DlibPath)) {
            throw 'Azure.CodeSigning.Dlib.dll not found. Get it from the NuGet package Microsoft.Trusted.Signing.Client (bin\x64), then pass -DlibPath or set TRUSTED_SIGNING_DLIB. See docs/SECURITY-AUTOMATION.md section 5.2.'
        }
        if (-not $MetadataPath -or -not (Test-Path -LiteralPath $MetadataPath)) {
            throw 'metadata.json not found. Copy tools\security\templates\trusted-signing-metadata.json outside the repo, fill Endpoint / CodeSigningAccountName / CertificateProfileName, pass -MetadataPath or set TRUSTED_SIGNING_METADATA.'
        }
        $meta = Get-Content -LiteralPath $MetadataPath -Raw | ConvertFrom-Json
        foreach ($k in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
            if (-not $meta.$k -or "$($meta.$k)" -like '<*') { throw "metadata.json: '$k' is not filled in ($MetadataPath)." }
        }
        $a = @('sign', '/v', '/debug', '/fd', 'SHA256')
        if (-not $NoTimestamp) { $a += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        $a += @('/dlib', $DlibPath, '/dmdf', $MetadataPath, $File)
        Invoke-LotrNative $tool $a
    }
    'SignPath' {
        if ($Release) {
            throw 'Refusing: SignPath Foundation release signing only accepts requests from the GitHub Actions workflow (origin verification). Use tools\security\templates\signpath-github-workflow.yml. Locally only the test-signing policy is possible.'
        }
        if (-not $env:SIGNPATH_API_TOKEN) { throw 'SIGNPATH_API_TOKEN is not set (a CI user API token of your SignPath organization).' }
        if (-not $SignPathOrganizationId) { throw 'Pass -SignPathOrganizationId or set SIGNPATH_ORGANIZATION_ID.' }
        if (-not (Get-Command Submit-SigningRequest -ErrorAction SilentlyContinue)) {
            throw 'SignPath PowerShell module missing: Install-Module -Name SignPath -Scope CurrentUser  (https://www.powershellgallery.com/packages/SignPath)'
        }
        $signed = "$File.signpath-signed"
        $p = @{
            InputArtifactPath  = $File
            ApiToken           = $env:SIGNPATH_API_TOKEN
            OrganizationId     = $SignPathOrganizationId
            ProjectSlug        = $SignPathProjectSlug
            SigningPolicySlug  = $SignPathPolicySlug
            OutputArtifactPath = $signed
            WaitForCompletion  = $true
        }
        if ($SignPathArtifactConfigurationSlug) { $p.ArtifactConfigurationSlug = $SignPathArtifactConfigurationSlug }
        Submit-SigningRequest @p
        Move-Item -LiteralPath $signed -Destination $File -Force
    }
}

$rep = Get-LotrSignatureReport $File $SignToolPath
$rep | Format-List File, Sha256, Status, Signer, Issuer, Thumbprint, NotAfter, Timestamped, TimestampSigner, IsTestCertificate, SignToolVerify

if ($Provider -eq 'TestCert') {
    # Untrusted root is the expected outcome; a hash mismatch or a missing signature is not.
    if ($rep.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) { throw "Test signature unusable: $($rep.Status) - $($rep.StatusMessage)" }
    if (-not $rep.IsTestCertificate) { throw 'Signed file does not carry the test certificate.' }
    Write-Host "OK (development signature; Windows reports '$($rep.Status)' because the test root is not trusted - expected)."
} else {
    if ($rep.Status -ne 'Valid') { throw "Signature does not verify: $($rep.Status) - $($rep.StatusMessage)" }
    if ($rep.IsTestCertificate) { throw 'A test certificate was used where a real one was expected.' }
    if ($Release -and -not $rep.Timestamped) { throw 'Release signature is not timestamped.' }
    Write-Host 'OK (trusted signature).'
}

if ($WriteSha256) {
    # REVIEW-AFTER(publish-cli): CONTRACTS §1 lists "LotRMPUpdater-<version>.exe (+ .sha256)"; format here is "<hex>  <name>".
    $line = "$($rep.Sha256)  $(Split-Path -Leaf $File)`n"
    [System.IO.File]::WriteAllText("$File.sha256", $line, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Wrote $File.sha256"
}
