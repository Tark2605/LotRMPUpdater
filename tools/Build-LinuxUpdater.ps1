<#
.SYNOPSIS
    Builds lotr-updater, the Linux / Steam Deck updater, and packs it for download (docs/LINUX.md, "Publishing").

.DESCRIPTION
    Runs on the team's Windows PC (no Linux needed: .NET cross-compiles).
    1. Uses the release PUBLIC key already in src\Lotr.Updater.Core\Security\Keys\ (the one the Windows exe was built
       with), or copies -PublicKey there first, exactly like Build-Updater.ps1.
    2. dotnet publish src\Lotr.Updater.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
       -p:LotrBaseUrl=https://<Domain>   (one ELF file, no .NET to install on the player's PC)
    3. Packs lotr-updater-<version>-linux-x64.tar.gz in -OutputDir: a folder with the program (mode 755, so it runs
       straight after extracting) and a short README. Writes a .sha256 next to it. (The menu entry is made by the
       program itself, "lotr-updater shortcut": only it knows where the player extracted it.)
    Publishing is a separate, signed step: lotr-publish publish-updater --platform linux --archive <the .tar.gz>
    (docs/LINUX.md, "Publish it"); it checks this exact layout (folder lotr-updater-<version>/, executable ELF x86-64).

.EXAMPLE
    .\tools\Build-LinuxUpdater.ps1 -Domain middleearthevent.com
.EXAMPLE
    .\tools\Build-LinuxUpdater.ps1 -Domain middleearthevent.com -PublicKey D:\from-server\release-signing.pub.pem -OutputDir ..\dist\updater
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Domain,
    [string]$PublicKey,
    [string]$OutputDir
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path   # $PSScriptRoot is empty in param defaults on PowerShell 5.1
$repo = Split-Path $here -Parent
if (-not $OutputDir) { $OutputDir = Join-Path $repo '..\dist\updater' }

$Domain = ($Domain.Trim().ToLowerInvariant() -replace '^https?://', '' -replace '/.*$', '')
if ($Domain -notmatch '^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$') { throw "Not a domain name: $Domain" }

# A POSIX (ustar) tar written by hand, gzip-compressed: Windows cannot store the Unix "executable" bit, a tar can.
function Set-TarField([byte[]]$Header, [int]$At, [int]$Length, [string]$Text) {
    $b = [Text.Encoding]::ASCII.GetBytes($Text)
    if ($b.Length -gt $Length) { throw "tar field too long: $Text" }
    [Array]::Copy($b, 0, $Header, $At, $b.Length)
}
function Set-TarOctal([byte[]]$Header, [int]$At, [int]$Length, [long]$Value) {
    Set-TarField $Header $At $Length ([Convert]::ToString($Value, 8).PadLeft($Length - 1, '0'))
}
function Write-Tarball([string]$Path, [object[]]$Entries) {
    $mtime = [long]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    $fs = [IO.File]::Create($Path)
    try {
        $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionLevel]::Optimal)
        try {
            foreach ($e in $Entries) {
                $data = if ($e.Dir) { New-Object byte[] 0 } else { [byte[]]$e.Bytes }
                $h = New-Object byte[] 512
                Set-TarField $h 0 100 $e.Name
                Set-TarOctal $h 100 8 $e.Mode
                Set-TarOctal $h 108 8 0                 # uid
                Set-TarOctal $h 116 8 0                 # gid
                Set-TarOctal $h 124 12 $data.Length
                Set-TarOctal $h 136 12 $mtime
                Set-TarField $h 148 8 '        '        # checksum: 8 spaces while summing
                Set-TarField $h 156 1 $(if ($e.Dir) { '5' } else { '0' })
                Set-TarField $h 257 6 "ustar`0"
                Set-TarField $h 263 2 '00'
                Set-TarField $h 265 32 'root'
                Set-TarField $h 297 32 'root'
                $sum = 0; foreach ($x in $h) { $sum += $x }
                Set-TarField $h 148 8 ([Convert]::ToString($sum, 8).PadLeft(6, '0') + "`0 ")
                $gz.Write($h, 0, 512)
                if ($data.Length -gt 0) {
                    $gz.Write($data, 0, $data.Length)
                    $pad = (512 - ($data.Length % 512)) % 512
                    if ($pad -gt 0) { $gz.Write((New-Object byte[] $pad), 0, $pad) }
                }
            }
            $gz.Write((New-Object byte[] 1024), 0, 1024)
        } finally { $gz.Dispose() }
    } finally { $fs.Dispose() }
}
$keysDir = Join-Path $repo 'src\Lotr.Updater.Core\Security\Keys'
if ($PublicKey) {
    if (-not (Test-Path -LiteralPath $PublicKey)) { throw "Public key not found: $PublicKey" }
    $pem = "$(Get-Content -LiteralPath $PublicKey -Raw)"
    if ($pem -match 'PRIVATE KEY') { throw 'This is a PRIVATE key. Copy only release-signing.pub.pem from the server.' }
    if (-not $pem.Contains('-----BEGIN PUBLIC KEY-----')) { throw 'Not a PEM public key (expected -----BEGIN PUBLIC KEY-----).' }
    Copy-Item -LiteralPath $PublicKey -Destination (Join-Path $keysDir 'release-signing.pub.pem') -Force
    Write-Host "[ok] Public key copied to $keysDir" -ForegroundColor Green
}
$keys = @(Get-ChildItem -Path $keysDir -Filter '*.pub.pem' -ErrorAction SilentlyContinue)
if ($keys.Count -eq 0) { throw "No release public key in $keysDir. Pass -PublicKey <release-signing.pub.pem> (copied from the server)." }

$proj = Join-Path $repo 'src\Lotr.Updater.Cli\Lotr.Updater.Cli.csproj'
$version = ([xml](Get-Content -LiteralPath $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $proj" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("lotr-linux-build-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    $bin = Join-Path $stage 'publish'
    & dotnet publish $proj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true "-p:LotrBaseUrl=https://$Domain" -o $bin -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
    $elf = Join-Path $bin 'lotr-updater'
    if (-not (Test-Path -LiteralPath $elf)) { throw 'lotr-updater not found after the publish.' }
    $head = [IO.File]::ReadAllBytes($elf)[0..3]
    if ($head[0] -ne 0x7F -or $head[1] -ne 0x45 -or $head[2] -ne 0x4C -or $head[3] -ne 0x46) { throw 'lotr-updater is not a Linux (ELF) program.' }

    $top = "lotr-updater-$version"
    $readme = @"
lotr-updater $version - installs and updates the LotR MP Event mod for Bannerlord on Linux and Steam Deck.

Start it from a terminal in this folder (Steam Deck desktop mode: right-click in this folder > Open Terminal Here):
    ./lotr-updater
then choose 2 to install or update the mod, 4 to play. All commands: ./lotr-updater --help
To get "LotR MP Event updater" in your application menu: ./lotr-updater shortcut

Full guide and troubleshooting: https://$Domain/install/  (Linux / Steam Deck)
Check the download: sha256sum -c lotr-updater-$version-linux-x64.tar.gz.sha256
"@
    $entries = @(
        @{ Name = "$top/"; Mode = 493; Dir = $true },
        @{ Name = "$top/lotr-updater"; Mode = 493; Bytes = [IO.File]::ReadAllBytes($elf) },
        @{ Name = "$top/README.txt"; Mode = 420; Bytes = [Text.Encoding]::UTF8.GetBytes(($readme -replace "`r`n", "`n") + "`n") }
    )

    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $tgz = Join-Path (Resolve-Path $OutputDir) "$top-linux-x64.tar.gz"
    Write-Tarball -Path $tgz -Entries $entries
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $tgz -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$tgz.sha256" -Value "$hash  $(Split-Path $tgz -Leaf)`n" -Encoding ASCII -NoNewline
Write-Host "[ok] $tgz ($([math]::Round((Get-Item $tgz).Length / 1MB, 2)) MB)" -ForegroundColor Green
Write-Host "     sha256 $hash"
Write-Host "     server https://$Domain"
Write-Host ''
Write-Host "Next: sign it with lotr-publish publish-updater --platform linux --archive `"$tgz`" --updater-dir <signed>\updater --base-url https://$Domain (docs/LINUX.md, 'Publish it')." -ForegroundColor Cyan
