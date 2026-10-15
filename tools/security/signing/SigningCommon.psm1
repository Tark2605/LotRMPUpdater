# Shared helpers for the updater code-signing pipeline (PowerShell 5.1 compatible).
Set-StrictMode -Version 2

# RFC 3161 timestamp servers. Timestamping keeps a signature valid after the certificate expires.
$script:TimestampUrls = @{
    AzureTrustedSigning = 'http://timestamp.acs.microsoft.com'   # required by Azure Trusted Signing
    Default             = 'http://timestamp.digicert.com'        # free public TSA, fine for the test certificate
}

$script:TestSubjectMarker = 'TEST SIGNING - NOT FOR RELEASE'

function Get-LotrTimestampUrl([string]$Provider) {
    if ($script:TimestampUrls.ContainsKey($Provider)) { return $script:TimestampUrls[$Provider] }
    return $script:TimestampUrls['Default']
}

function Get-LotrTestSubjectMarker { $script:TestSubjectMarker }

<#
 Finds signtool.exe: -SignToolPath, %SIGNTOOL%, PATH, newest Windows 10/11 SDK, or the NuGet package
 Microsoft.Windows.SDK.BuildTools in the user's NuGet cache. Returns $null when absent.
#>
function Find-LotrSignTool([string]$SignToolPath) {
    if ($SignToolPath) { if (Test-Path -LiteralPath $SignToolPath) { return (Resolve-Path -LiteralPath $SignToolPath).Path } else { throw "signtool not found at $SignToolPath" } }
    if ($env:SIGNTOOL -and (Test-Path -LiteralPath $env:SIGNTOOL)) { return $env:SIGNTOOL }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $roots = @()
    foreach ($pf in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) { if ($pf) { $roots += (Join-Path $pf 'Windows Kits\10\bin') } }
    $nuget = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    if (Test-Path -LiteralPath $nuget) { $roots += $nuget }
    $found = foreach ($r in $roots) {
        if (Test-Path -LiteralPath $r) {
            Get-ChildItem -LiteralPath $r -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\' }
        }
    }
    $best = $found | Sort-Object { $_.FullName } -Descending | Select-Object -First 1
    if ($best) { return $best.FullName }
    return $null
}

function Invoke-LotrNative([string]$Exe, [string[]]$Arguments) {
    Write-Host ("> " + (Split-Path -Leaf $Exe) + " " + ($Arguments -join ' '))
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $Exe) failed with exit code $LASTEXITCODE" }
}

<#
 Verifies an Authenticode signature. Returns an object; never throws for an unsigned/invalid file
 (the caller decides). Uses Get-AuthenticodeSignature always and "signtool verify /pa" when signtool exists.
#>
function Get-LotrSignatureReport([string]$File, [string]$SignToolPath) {
    $sig = Get-AuthenticodeSignature -LiteralPath $File
    $signer = $sig.SignerCertificate
    $ts = $sig.TimeStamperCertificate
    $report = [ordered]@{
        File              = (Resolve-Path -LiteralPath $File).Path
        Sha256            = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash.ToLowerInvariant()
        Status            = [string]$sig.Status
        StatusMessage     = $sig.StatusMessage
        Signer            = $(if ($signer) { $signer.Subject } else { $null })
        Issuer            = $(if ($signer) { $signer.Issuer } else { $null })
        Thumbprint        = $(if ($signer) { $signer.Thumbprint } else { $null })
        NotAfter          = $(if ($signer) { $signer.NotAfter.ToString('yyyy-MM-dd') } else { $null })
        Timestamped       = [bool]$ts
        TimestampSigner   = $(if ($ts) { $ts.Subject } else { $null })
        IsTestCertificate = [bool]($signer -and $signer.Subject -like "*$script:TestSubjectMarker*")
        SignToolVerify    = 'not run (signtool not found)'
    }
    $tool = $null
    try { $tool = Find-LotrSignTool $SignToolPath } catch { }
    if ($tool -and $sig.Status -ne 'NotSigned') {
        $out = & $tool verify /pa /v $File 2>&1 | Out-String
        $report.SignToolVerify = $(if ($LASTEXITCODE -eq 0) { 'ok' } else { "failed (exit $LASTEXITCODE)" })
        $report.SignToolOutput = $out.Trim()
    }
    return [pscustomobject]$report
}

Export-ModuleMember -Function Get-LotrTimestampUrl, Get-LotrTestSubjectMarker, Find-LotrSignTool, Invoke-LotrNative, Get-LotrSignatureReport
