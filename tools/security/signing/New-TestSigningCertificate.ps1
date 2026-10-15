<#
.SYNOPSIS
    DEVELOPMENT ONLY. Creates a self-signed code-signing certificate (PFX) to exercise the signing pipeline.

.DESCRIPTION
    *** NOT FOR RELEASE. A self-signed signature gives players NO trust at all (Windows shows "unknown publisher",
    SmartScreen still warns). Never ship a build signed with it; Test-Signature.ps1 flags it, and Sign-Updater.ps1
    refuses -Provider TestCert when -Release is passed. ***

    - Built in memory with .NET CertificateRequest (RSA 3072, EKU Code Signing, 1 year).
    - NOT added to any certificate store, and never to "Trusted Root": nothing system-wide changes.
    - Writes <OutDir>\lotr-test-signing.pfx and a random password in <OutDir>\lotr-test-signing.pfx.password
      (default OutDir: %LOCALAPPDATA%\LotRMP\test-signing, outside the repository). Re-running reuses them.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'LotRMP\test-signing'),
    [switch]$Force
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SigningCommon.psm1') -Force

$pfx = Join-Path $OutDir 'lotr-test-signing.pfx'
$pwdFile = "$pfx.password"
if ((Test-Path -LiteralPath $pfx) -and (Test-Path -LiteralPath $pwdFile) -and -not $Force) {
    Write-Host "Test certificate already exists: $pfx (use -Force to recreate)"
    return $pfx
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$marker = Get-LotrTestSubjectMarker
$subject = "CN=LotR MP Event $marker, O=LotR MP Event (development only)"
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
try {
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        $subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $eku = New-Object System.Security.Cryptography.OidCollection
    [void]$eku.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3'))   # Code Signing
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($eku, $true)))
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
    $req.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
    $now = [DateTimeOffset]::UtcNow
    $cert = $req.CreateSelfSigned($now.AddMinutes(-5), $now.AddYears(1))

    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes)
    [System.IO.File]::WriteAllBytes($pfx, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
    [System.IO.File]::WriteAllText($pwdFile, $password)
    $cert.Dispose()
} finally {
    $rsa.Dispose()
}
Write-Host "Created DEVELOPMENT-ONLY test certificate: $pfx"
Write-Host "Subject: $subject"
Write-Host "It is not installed in any store and is NOT trusted by Windows: signatures made with it verify as 'UnknownError/NotTrusted'. That is expected."
return $pfx
