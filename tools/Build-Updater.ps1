<#
.SYNOPSIS
    Builds the production LotRMPUpdater.exe for a given domain and release public key.

.DESCRIPTION
    1. Copies the release PUBLIC key (release-signing.pub.pem, never the .key) into
       src\Lotr.Updater.Core\Security\Keys\ so the exe trusts releases signed on the server.
    2. Builds src\Lotr.Updater in Release with -p:LotrBaseUrl=https://<Domain> (the only server the exe talks to).
    3. Copies the exe to -OutputDir as LotRMPUpdater-<version>.exe with a .sha256 file, and prints the key
       fingerprint to compare with the server (lotr-publish show-key) and the site's Security page.

.EXAMPLE
    .\tools\Build-Updater.ps1 -Domain lotrmpevent.com -PublicKey D:\from-server\release-signing.pub.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [Parameter(Mandatory = $true)][string]$PublicKey,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\..\dist\updater')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$Domain = ($Domain.Trim().ToLowerInvariant() -replace '^https?://', '' -replace '/.*$', '')
if ($Domain -notmatch '^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$') { throw "Not a domain name: $Domain" }
if (-not (Test-Path -LiteralPath $PublicKey)) { throw "Public key not found: $PublicKey" }
$pem = Get-Content -LiteralPath $PublicKey -Raw
if ($pem -match 'PRIVATE KEY') { throw 'This is a PRIVATE key. Copy only release-signing.pub.pem from the server.' }
if ($pem -notmatch 'BEGIN PUBLIC KEY') { throw 'Not a PEM public key (expected -----BEGIN PUBLIC KEY-----).' }

$keysDir = Join-Path $repo 'src\Lotr.Updater.Core\Security\Keys'
Copy-Item -LiteralPath $PublicKey -Destination (Join-Path $keysDir 'release-signing.pub.pem') -Force
Write-Host "[ok] Public key copied to $keysDir" -ForegroundColor Green

$proj = Join-Path $repo 'src\Lotr.Updater\Lotr.Updater.csproj'
& dotnet build $proj -c Release "-p:LotrBaseUrl=https://$Domain" -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

$exe = Get-ChildItem -Path (Join-Path $repo 'src\Lotr.Updater\bin\Release') -Recurse -Filter 'LotRMPUpdater.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'LotRMPUpdater.exe not found after the build.' }
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe.FullName).ProductVersion -replace '\+.*$', ''
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$out = Join-Path (Resolve-Path $OutputDir) "LotRMPUpdater-$version.exe"
Copy-Item -LiteralPath $exe.FullName -Destination $out -Force
$hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$out.sha256" -Value "$hash  $(Split-Path $out -Leaf)" -Encoding ASCII -NoNewline

Write-Host "[ok] $out ($([math]::Round((Get-Item $out).Length / 1MB, 2)) MB)" -ForegroundColor Green
Write-Host "     sha256 $hash"
Write-Host "     server https://$Domain"
$publish = Get-ChildItem -Path (Join-Path $repo 'tools\LotR.Publish\bin') -Recurse -Filter 'lotr-publish.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($publish) {
    Write-Host '     key fingerprint (must match the server and the Security page):'
    & $publish.FullName show-key --pub (Join-Path $keysDir 'release-signing.pub.pem')
}
Write-Host ''
Write-Host 'Next, on the server:' -ForegroundColor Cyan
Write-Host "  C:\www\lotrmp\tools\lotr-publish\lotr-publish.exe publish-updater --exe <copied exe> --root C:\www\lotrmp"
