<#
.SYNOPSIS
    Builds a project twice from two copies of the source in two DIFFERENT folders and compares the output hashes.

.DESCRIPTION
    Proves that the build is deterministic: same source + same SDK + same packages => byte-identical exe/dll,
    whatever the folder it was built in. Two different folders on purpose: that catches absolute paths leaking into
    the binaries (fixed by PathMap). The comparison is done on UNSIGNED outputs (an Authenticode signature embeds a
    timestamp, so signed files always differ; a verifier strips or ignores the signature, see
    docs/SECURITY-AUTOMATION.md section 6).

    Copied into each build folder: the project's repository subtree (src\, tools\, Directory.Build.props,
    Directory.Packages.props, global.json, NuGet.config when present), without bin\ and obj\.

    REVIEW-AFTER(updater): the default -Project is the detection CLI as a stand-in; switch it to the WPF updater
    project once it exists (src\LotR.Updater\... per docs/PLAN.md step 3).

.EXAMPLE
    .\Test-ReproducibleBuild.ps1
    .\Test-ReproducibleBuild.ps1 -Project src\LotR.Updater\LotR.Updater.csproj -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string]$Project = 'src\LotR.Detect.Cli\LotR.Detect.Cli.csproj',
    [string]$Configuration = 'Release',
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) ('lotr-repro-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))),
    [switch]$Keep
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
# $PSScriptRoot is not reliable inside param() defaults on Windows PowerShell 5.1.
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..\..')).Path }

if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot $Project))) { throw "Project not found: $(Join-Path $RepoRoot $Project)" }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
Write-Host "SDK: $(& $dotnet --version)  (pin it with global.json - see templates\global.json.example)"

function Copy-Source([string]$dest) {
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($f in 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.config', 'nuget.config') {
        $p = Join-Path $RepoRoot $f
        if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $dest }
    }
    foreach ($d in 'src', 'tools') {
        $p = Join-Path $RepoRoot $d
        if (Test-Path -LiteralPath $p) {
            # robocopy: exit codes < 8 are success.
            & robocopy $p (Join-Path $dest $d) /E /XD bin obj .vs node_modules /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
        }
    }
}

function Build([string]$root, [string]$label) {
    $proj = Join-Path $root $Project
    $out = Join-Path $WorkDir "out-$label"
    $log = Join-Path $WorkDir "build-$label.log"
    # PathMap rewrites the source root to a fixed "/_/" so no machine/folder path ends up in the binaries or PDBs.
    $args = @('build', $proj, '-c', $Configuration, '-o', $out, '--nologo',
        '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true',
        "-p:PathMap=$root\=/_/", '-p:SourceRevisionId=repro', '-p:IncludeSourceRevisionInInformationalVersion=false')
    Write-Host "Building copy '$label' in $root"
    & $dotnet @args *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content $log -Tail 30 | Write-Host; throw "build '$label' failed (log: $log)" }
    return $out
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
# Expand 8.3 short names (e.g. C:\Users\UTILSA~1 from %TEMP%) so PathMap and relative paths match what tools report.
$WorkDir = (Get-Item -LiteralPath $WorkDir).FullName
try {
    $a = Join-Path $WorkDir 'a'
    $b = Join-Path $WorkDir 'b\deeper\path'   # different length on purpose
    Copy-Source $a; Copy-Source $b
    $outA = Build $a 'a'
    $outB = Build $b 'b'

    $rows = foreach ($fa in Get-ChildItem -LiteralPath $outA -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll', '.pdb' }) {
        $rel = $fa.FullName.Substring($outA.Length + 1)
        $fb = Join-Path $outB $rel
        $ha = (Get-FileHash -LiteralPath $fa.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hb = if (Test-Path -LiteralPath $fb) { (Get-FileHash -LiteralPath $fb -Algorithm SHA256).Hash.ToLowerInvariant() } else { '(missing)' }
        [pscustomobject]@{ File = $rel; Same = ($ha -eq $hb); Sha256 = $ha; Other = $(if ($ha -eq $hb) { '' } else { $hb }) }
    }
    $rows | Sort-Object File | Format-Table File, Same, Sha256 -AutoSize | Out-String -Width 220 | Write-Host

    $bin = @($rows | Where-Object { $_.File -notlike '*.pdb' })
    $diffBin = @($bin | Where-Object { -not $_.Same })
    $diffPdb = @($rows | Where-Object { $_.File -like '*.pdb' -and -not $_.Same })
    if ($diffPdb.Count -gt 0) { Write-Warning "$($diffPdb.Count) PDB(s) differ (paths/GUIDs); PDBs are not shipped, informational only." }
    if ($diffBin.Count -gt 0) {
        Write-Host "NOT REPRODUCIBLE: $($diffBin.Count) of $($bin.Count) binaries differ." -ForegroundColor Red
        exit 1
    }
    Write-Host "REPRODUCIBLE: $($bin.Count) binaries byte-identical across two build folders." -ForegroundColor Green
    exit 0
} finally {
    if (-not $Keep) { Remove-Item -LiteralPath $WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Kept $WorkDir" }
}
