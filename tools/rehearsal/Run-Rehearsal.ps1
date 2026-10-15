<#
.SYNOPSIS
    Full end-to-end rehearsal of the LotR MP Event updater chain on this PC, on loopback only.

.DESCRIPTION
    Everything happens in a new temporary folder (deleted at the end unless -Keep):
      1. builds lotr-publish, the dev helper and two updater exes (0.1.0-style current version and a "next" version)
      2. init-key (throw-away rehearsal key), publishes a COPY of the real event module (read-only source) + a fake Core
      3. starts lotr-services (node, enforce mode, rehearsal secret) and Caddy (static files + forward_auth + /api) on 127.0.0.1
      4. runs LotRMPUpdater.exe headless (--dev --headless) into a FAKE game folder: install, verify
      5. publishes a second release (XML edited, file added, file removed, Core pack region changed) and the next updater
      6. runs the updater again: self-update staged + delta update; then a third start swaps to the new updater
      7. support upload through Caddy -> lotr-services, fetched back on the loopback /internal route, burnt
      8. tampered manifest refused
    and prints a PASS/FAIL table. Never touches the real game, the real HKCU, a real key or a live service; every
    process it starts is stopped by PID.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\rehearsal\Run-Rehearsal.ps1
    powershell -ExecutionPolicy Bypass -File tools\rehearsal\Run-Rehearsal.ps1 -Keep -SkipBuild
#>
[CmdletBinding()]
param(
    [string]$EventModule = '',
    [string]$Caddy = (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\CaddyServer.Caddy_Microsoft.Winget.Source_8wekyb3d8bbwe\caddy.exe'),
    [string]$Node = 'node',
    [string]$NextVersion = '0.1.1',
    [string]$WorkRoot = $env:TEMP,
    [switch]$SkipBuild,
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $EventModule) { $EventModule = Join-Path $repo '..\colleague\github-LotRMPEvent\LotRMPEvent' }
$EventModule = (Resolve-Path $EventModule).Path
$T = Join-Path $WorkRoot ('lotr-rehearsal-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $T | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$procs = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Add-Result([string]$Step, [bool]$Ok, [string]$Detail) {
    $results.Add([pscustomobject]@{ Step = $Step; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
    $c = if ($Ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1} - {2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Step, $Detail) -ForegroundColor $c
}

function Get-FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

function Invoke-Tool([string]$Exe, [string[]]$ArgList, [string]$LogName) {
    $out = Join-Path $T ($LogName + '.out.txt'); $err = Join-Path $T ($LogName + '.err.txt')
    $p = Start-Process -FilePath $Exe -ArgumentList $ArgList -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out -RedirectStandardError $err
    return [pscustomobject]@{ Code = $p.ExitCode; Out = (Get-Content $out -Raw -ErrorAction SilentlyContinue); Err = (Get-Content $err -Raw -ErrorAction SilentlyContinue) }
}

function Quote([string]$s) { if ($s -match '[\s"]') { return '"' + ($s -replace '"', '\"') + '"' } return $s }

function Read-Report([string]$File) {
    $h = @{}
    if (Test-Path $File) {
        foreach ($line in Get-Content $File) {
            $i = $line.IndexOf('=')
            if ($i -gt 0) { $k = $line.Substring(0, $i); $v = $line.Substring($i + 1); if ($h.ContainsKey($k)) { $h[$k] += "; " + $v } else { $h[$k] = $v } }
        }
    }
    return $h
}

function Get-Sha256([string]$f) { (Get-FileHash -Algorithm SHA256 -Path $f).Hash.ToLowerInvariant() }

function Wait-Http([string]$Url, [int]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { $r = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2; if ($r.StatusCode -eq 200) { return $true } } catch { Start-Sleep -Milliseconds 250 }
    }
    return $false
}

try {
    Write-Host "Rehearsal folder: $T"
    # ------------------------------------------------------------------ 1. build
    $bin = Join-Path $T 'bin'
    $publishExe = Join-Path $repo 'tools\LotR.Publish\bin\Debug\net48\lotr-publish.exe'
    $devExe = Join-Path $repo 'tests\Lotr.Updater.DevServer\bin\Debug\net8.0\lotr-updater-devserver.exe'
    $upd1 = Join-Path $bin 'upd1\LotRMPUpdater.exe'
    $upd2 = Join-Path $bin 'upd2\LotRMPUpdater.exe'
    if (-not $SkipBuild) {
        foreach ($b in @(
                @{ P = 'tools\LotR.Publish'; A = @() },
                @{ P = 'tests\Lotr.Updater.DevServer'; A = @() })) {
            $r = Invoke-Tool 'dotnet' (@('build', (Join-Path $repo $b.P), '-c', 'Debug', '-nologo', '-v', 'q') + $b.A) ('build-' + (Split-Path $b.P -Leaf))
            if ($r.Code -ne 0) { throw "build of $($b.P) failed:`n$($r.Out)" }
        }
    }
    # Two updater exes: the current version and the "next" one (same code, higher version number).
    $proj = Join-Path $repo 'src\Lotr.Updater\Lotr.Updater.csproj'
    $r1 = Invoke-Tool 'dotnet' @('build', $proj, '-c', 'Debug', '-nologo', '-v', 'q', '-o', (Join-Path $bin 'upd1')) 'build-updater-1'
    $r2 = Invoke-Tool 'dotnet' @('build', $proj, '-c', 'Debug', '-nologo', '-v', 'q', "-p:Version=$NextVersion", '-o', (Join-Path $bin 'upd2')) 'build-updater-2'
    # Leave the normal Debug output as the default build again (the -p:Version build reused obj\).
    $r3 = Invoke-Tool 'dotnet' @('build', $proj, '-c', 'Debug', '-nologo', '-v', 'q') 'build-updater-restore'
    $v1 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($upd1).FileVersion
    $v2 = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($upd2).FileVersion
    $buildOk = $r1.Code -eq 0 -and $r2.Code -eq 0 -and $r3.Code -eq 0 -and (Test-Path $publishExe) -and (Test-Path $devExe)
    Add-Result 'Build' $buildOk ("lotr-publish, dev helper, updater {0} ({1:N0} bytes) and {2}" -f $v1, (Get-Item $upd1).Length, $v2)
    if (-not $buildOk) { throw 'build failed' }

    # ------------------------------------------------------------------ 2. key + release 1
    $R = Join-Path $T 'root'
    $k = Invoke-Tool $publishExe @('init-key', '--root', $R, '--protection', 'plain', '--no-acl') 'init-key'
    $pub = Join-Path $R 'data\secrets\release-signing.pub.pem'
    Add-Result 'init-key (throw-away key)' ($k.Code -eq 0 -and (Test-Path $pub)) (($k.Out -split "`n" | Where-Object { $_ -match 'fingerprint' } | Select-Object -First 1) -replace '\s+', ' ')

    $src1 = Join-Path $T 'src1'; $src2 = Join-Path $T 'src2'
    $null = & robocopy $EventModule (Join-Path $src1 'LotRMPEvent') /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
    if ($LASTEXITCODE -ge 8) { throw "robocopy of the event module failed ($LASTEXITCODE)" }
    $null = & $devExe fakecore (Join-Path $src1 'LotRMPCore') 1
    $p1 = Invoke-Tool $publishExe @('publish', '--root', $R, '--event', (Join-Path $src1 'LotRMPEvent'), '--core', (Join-Path $src1 'LotRMPCore'), '--channel', 'stable', '--notes', (Quote 'Rehearsal release 1'), '--by', 'rehearsal', '--json') 'publish-1'
    $rel1 = $null; try { $rel1 = ($p1.Out | ConvertFrom-Json).releaseId } catch { }
    Add-Result 'Publish release 1 (real event module copy + fake Core)' ($p1.Code -eq 0 -and $rel1) ("{0}, {1} files in the event module" -f $rel1, (Get-ChildItem -Recurse -File (Join-Path $src1 'LotRMPEvent')).Count)
    if (-not $rel1) { throw "publish 1 failed:`n$($p1.Err)" }

    # ------------------------------------------------------------------ 3. services + caddy
    $svcPort = Get-FreePort; $webPort = Get-FreePort
    $secret = -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
    $svcConfig = Join-Path $T 'lotr-services.json'
    @{ host = '127.0.0.1'; port = $svcPort; root = $R; log = @{ console = $false; level = 'info' }; queue = @{ enforcement = 'enforce' } } | ConvertTo-Json -Depth 4 | Set-Content -Path $svcConfig -Encoding ASCII
    $env:LOTR_SUPPORT_SECRET = $secret
    $svc = Start-Process -FilePath $Node -ArgumentList @((Quote (Join-Path $repo 'services\lotr-services\src\server.js')), '--config', (Quote $svcConfig)) -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $T 'lotr-services.out.txt') -RedirectStandardError (Join-Path $T 'lotr-services.err.txt')
    $procs.Add($svc)
    Remove-Item Env:\LOTR_SUPPORT_SECRET
    $svcUp = Wait-Http "http://127.0.0.1:$svcPort/healthz"
    Add-Result 'lotr-services started (enforce mode)' $svcUp ("pid {0}, 127.0.0.1:{1}" -f $svc.Id, $svcPort)

    $files = ($R + '\files') -replace '\\', '/'
    $caddyfile = @"
{
	auto_https off
	admin off
}
http://127.0.0.1:$webPort {
	handle_path /files/* {
		@releaseObjects path_regexp releaseobj ^/releases/[^/]+/objects/
		forward_auth @releaseObjects 127.0.0.1:$svcPort {
			uri /internal/queue/check
		}
		root * "$files"
		file_server
	}
	handle /api/queue/* {
		request_body {
			max_size 16KB
		}
		reverse_proxy 127.0.0.1:$svcPort
	}
	handle /api/support {
		request_body {
			max_size 3MB
		}
		reverse_proxy 127.0.0.1:$svcPort
	}
}
"@
    $caddyConf = Join-Path $T 'Caddyfile'
    Set-Content -Path $caddyConf -Value $caddyfile -Encoding ASCII
    $cad = Start-Process -FilePath $Caddy -ArgumentList @('run', '--config', (Quote $caddyConf), '--adapter', 'caddyfile') -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $T 'caddy.out.txt') -RedirectStandardError (Join-Path $T 'caddy.err.txt')
    $procs.Add($cad)
    $base = "http://127.0.0.1:$webPort/"
    $webUp = Wait-Http ($base + 'files/releases/channels.json')
    Add-Result 'Caddy started (files + forward_auth + /api)' $webUp ("pid {0}, {1}" -f $cad.Id, $base)
    if (-not ($svcUp -and $webUp)) { throw 'servers did not start' }

    # ------------------------------------------------------------------ 4. first install, headless
    $game = (& $devExe fakegame (Join-Path $T 'game\steamapps\common\Mount & Blade II Bannerlord') | Select-Object -Last 1).Trim()
    $uhome = Join-Path $T 'home'
    $appDir = Join-Path $uhome 'app'
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Copy-Item $upd1 (Join-Path $appDir 'LotRMPUpdater.exe')
    $installed = Join-Path $appDir 'LotRMPUpdater.exe'
    function Run-Updater([string]$Name, [string[]]$Extra) {
        $report = Join-Path $T ($Name + '.report.txt')
        $a = @('--dev', '--server', $base, '--home', (Quote $uhome), '--game', (Quote $game), '--trust', (Quote $pub), '--headless', '--report', (Quote $report)) + $Extra
        $p = Start-Process -FilePath $installed -ArgumentList $a -Wait -PassThru
        $h = Read-Report $report
        $h['_exit'] = $p.ExitCode
        return $h
    }

    $h1 = Run-Updater 'run1-install' @('--self-update')
    Add-Result 'Updater run 1: fresh install' ($h1['_exit'] -eq 0 -and $h1['install'] -eq 'ok' -and $h1['installed'] -eq $rel1) `
        ("v{0}, release {1}, {2:N0} bytes in {3} objects, Core slot {4}" -f $h1['appVersion'], $h1['installed'], [long]$h1['planDownloadBytes'], $h1['planDownloadObjects'], $h1['planCoreSlot'])
    Add-Result 'Updater run 1: verify (every file re-hashed)' ($h1['verifyHealthy'] -eq 'True') ("{0} files checked, {1} extra" -f $h1['verifyChecked'], $h1['verifyExtra'])
    Add-Result 'Updater run 1: no updater release yet' ($h1['selfUpdate'] -eq 'none') $h1['selfUpdate']
    $modules = Join-Path $game 'Modules'
    $vi1 = Invoke-Tool $publishExe @('verify-install', (Quote (Join-Path $modules 'LotRMPEvent')), '--release', (Quote (Join-Path $R "files\releases\$rel1")), '--pub', (Quote $pub)) 'verify-install-1'
    Add-Result 'lotr-publish verify-install (independent check)' ($vi1.Code -eq 0 -and $vi1.Out -match 'RESULT: OK') (($vi1.Out -split "`n" | Where-Object { $_ -match '^checked' }) -join '' -replace '\s+', ' ')
    $hz = Invoke-RestMethod "http://127.0.0.1:$svcPort/healthz"
    $fa = $hz.queue.forwardAuth
    Add-Result 'Core objects went through the queue (enforce)' ($fa.checks -gt 0 -and $fa.missing -eq 0 -and $fa.invalid -eq 0 -and $hz.queue.core.active -eq 0) `
        ("forward_auth checks {0}, missing {1}, invalid {2}; active slots after: {3}" -f $fa.checks, $fa.missing, $fa.invalid, $hz.queue.core.active)

    # ------------------------------------------------------------------ 5. release 2 + next updater
    $null = & robocopy $src1 $src2 /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1
    $ev2 = Join-Path $src2 'LotRMPEvent'
    $xml = Get-ChildItem (Join-Path $ev2 'ModuleData') -Filter '*.xml' | Where-Object { $_.Name -ne 'SubModule.xml' } | Select-Object -First 1
    Add-Content -Path $xml.FullName -Value '<!-- rehearsal edit -->'
    Set-Content -Path (Join-Path $ev2 'ModuleData\rehearsal_added.xml') -Value '<Rehearsal added="true"/>' -Encoding UTF8
    $removed = Get-ChildItem -Recurse -File (Join-Path $ev2 'GUI') -Filter '*.png' | Select-Object -First 1
    Remove-Item $removed.FullName
    $null = & $devExe fakecore (Join-Path $src2 'LotRMPCore') 2
    $p2 = Invoke-Tool $publishExe @('publish', '--root', $R, '--event', $ev2, '--core', (Join-Path $src2 'LotRMPCore'), '--channel', 'stable', '--notes', (Quote 'Rehearsal release 2'), '--by', 'rehearsal', '--json') 'publish-2'
    $rel2 = $null; try { $rel2 = ($p2.Out | ConvertFrom-Json).releaseId } catch { }
    Add-Result 'Publish release 2 (xml edited, file added, file removed, Core region changed)' ($p2.Code -eq 0 -and $rel2 -and $rel2 -ne $rel1) $rel2
    $pu = Invoke-Tool $publishExe @('publish-updater', '--exe', (Quote $upd2), '--root', $R, '--channel', 'stable', '--json') 'publish-updater'
    $latest = $null; try { $latest = ($pu.Out | ConvertFrom-Json).latest } catch { }
    $updDir = Join-Path $R 'files\updater'
    Add-Result 'publish-updater (exe + .sha256 + latest.json + .sig)' ($pu.Code -eq 0 -and $latest -and (Test-Path (Join-Path $updDir 'latest.json.sig')) -and (Test-Path (Join-Path $updDir ("LotRMPUpdater-{0}.exe.sha256" -f $latest.version)))) `
        ("{0}, {1:N0} bytes, sha256 {2}..." -f $latest.version, $latest.size, $latest.sha256.Substring(0, 12))

    # ------------------------------------------------------------------ 6. update + self-update
    $h2 = Run-Updater 'run2-update' @('--self-update')
    Add-Result 'Updater run 2: delta update to release 2' ($h2['_exit'] -eq 0 -and $h2['install'] -eq 'ok' -and $h2['installed'] -eq $rel2 -and $h2['verifyHealthy'] -eq 'True') `
        ("add {0}, replace {1}, remove {2}; download {3:N0} bytes in {4} objects, {5:N0} bytes reused from local files" -f $h2['planAdd'], $h2['planReplace'], $h2['planRemove'], [long]$h2['planDownloadBytes'], $h2['planDownloadObjects'], [long]$h2['planReusedLocalBytes'])
    $small = [long]$h2['planDownloadBytes'] -lt 16MB -and [long]$h2['planReusedLocalBytes'] -gt 0
    Add-Result 'Delta is small (only changed files/chunks)' $small ("{0:N1} MB downloaded" -f ([long]$h2['planDownloadBytes'] / 1MB))
    Add-Result 'Updater run 2: new updater staged (SHA-256 + release-key signature)' ($h2['selfUpdate'] -eq ('staged ' + $latest.version) -and $h2['selfUpdateReleaseKeySigned'] -eq 'True') $h2['selfUpdate']
    $stillOld = (Get-Sha256 $installed) -eq (Get-Sha256 $upd1)
    Add-Result 'Running exe untouched until the next start' $stillOld 'app\LotRMPUpdater.exe = build 1'

    $h3 = Run-Updater 'run3-swap' @('--self-update')
    $swapOk = $h3['_exit'] -eq 0 -and $h3['appVersion'] -eq $latest.version -and (Get-Sha256 $installed) -eq $latest.sha256 -and (Get-Sha256 (Join-Path $appDir 'LotRMPUpdater.old.exe')) -eq (Get-Sha256 $upd1)
    Add-Result 'Updater run 3: swapped at start, restarted as the new version, old exe kept' $swapOk ("now v{0}; LotRMPUpdater.old.exe kept; pending {1}" -f $h3['appVersion'], $h3['pendingSelfUpdate'])
    Add-Result 'Updater run 3: up to date and healthy' ($h3['install'] -eq 'nothing to do' -and $h3['verifyHealthy'] -eq 'True' -and $h3['selfUpdate'] -eq 'none') ("install: {0}; selfUpdate: {1}" -f $h3['install'], $h3['selfUpdate'])

    # ------------------------------------------------------------------ 7. support upload
    $h4 = Run-Updater 'run4-support' @('--check-only', '--send-support')
    $code = $h4['supportCode']
    $codeOk = $h4['_exit'] -eq 0 -and $code -match '^LOTR-[0-9A-Z]{4,6}$'
    Add-Result 'Support log upload through Caddy -> lotr-services' $codeOk ("code {0}, {1:N0} bytes" -f $code, [long]$h4['supportLogBytes'])
    $fetched = $null; $fetchErr = $null
    try { $fetched = Invoke-RestMethod -Uri ("http://127.0.0.1:$svcPort/internal/support/" + $code) -Headers @{ 'X-LotR-Secret' = $secret } } catch { $fetchErr = $_.Exception.Message }
    $userName = [Environment]::UserName
    $masked = $fetched -and $fetched.log -match ('App version: ' + [regex]::Escape($latest.version)) -and $fetched.log -notmatch [regex]::Escape($userName) -and $fetched.log -notmatch '(?i)\\Users\\[^\\<]{1,6}~\d'
    Add-Result 'Support log fetched by the bot route (loopback + secret), user name masked' $masked $(if ($fetched) { "summary: " + ($fetched.summary | ConvertTo-Json -Compress) } else { $fetchErr })
    $second = $null
    try { Invoke-RestMethod -Uri ("http://127.0.0.1:$svcPort/internal/support/" + $code) -Headers @{ 'X-LotR-Secret' = $secret } | Out-Null; $second = 200 } catch { $second = [int]$_.Exception.Response.StatusCode }
    Add-Result 'Support code is one-time (second fetch refused)' ($second -eq 410) ("second fetch: HTTP {0}" -f $second)

    # ------------------------------------------------------------------ 8. tampering
    $mf = Join-Path $R "files\releases\$rel2\manifest.json"
    $bytes = [System.IO.File]::ReadAllBytes($mf)
    $i = [Array]::IndexOf($bytes, [byte][char]'1')
    $bytes[$i] = [byte][char]'2'
    [System.IO.File]::WriteAllBytes($mf, $bytes)
    $h5 = Run-Updater 'run5-tampered' @('--check-only')
    Add-Result 'Tampered manifest refused (nothing changed)' ($h5['_exit'] -ne 0 -and $h5['check'] -match 'signature') $h5['check']
}
catch {
    Add-Result 'Rehearsal script' $false $_.Exception.Message
}
finally {
    foreach ($p in $procs) {
        try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(5000) | Out-Null } } catch { }
    }
    $left = @($procs | Where-Object { -not $_.HasExited })
    Add-Result 'Cleanup: started processes stopped by PID' ($left.Count -eq 0) (($procs | ForEach-Object { $_.Id }) -join ', ')
    if (-not $Keep) {
        Start-Sleep -Milliseconds 500
        try { Remove-Item -Recurse -Force $T -ErrorAction Stop; Add-Result 'Cleanup: temp folder removed' $true $T } catch { Add-Result 'Cleanup: temp folder removed' $false $_.Exception.Message }
    }
    else { Write-Host "Kept: $T" }
}

Write-Host ''
Write-Host ("LotR MP Event updater rehearsal - {0:N0} s" -f $sw.Elapsed.TotalSeconds)
$results | Format-Table -AutoSize -Wrap Step, Result, Detail | Out-String -Width 220 | Write-Host
$failed = @($results | Where-Object { $_.Result -eq 'FAIL' }).Count
if ($failed -eq 0) { Write-Host "ALL $($results.Count) CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$failed of $($results.Count) CHECKS FAILED" -ForegroundColor Red
exit 1
