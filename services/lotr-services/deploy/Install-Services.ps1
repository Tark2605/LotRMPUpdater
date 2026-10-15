<#
.SYNOPSIS
    Installs or updates lotr-services (updater Core download queue + support log codes) as the Windows service
    LotRMPServices, listening on 127.0.0.1:3010 only, and adds its routes to the public Caddy site.

.DESCRIPTION
    REVIEW-AFTER(deploy-wiring): DRAFT written by the services workstream to match the site deploy package
    conventions (site\deploy\package). It is meant to be copied to site\deploy\package\Install-Services.ps1 together
    with the service source in site\deploy\package\services\lotr-services\ (src\, package.json, package-lock.json,
    config.example.json) and the Caddy route block of DEPLOY.md merged into lib\SiteDeploy.Services.ps1.
    Not yet run on the server.

    Run on the server, elevated, after Deploy-Site.ps1 (it reads <Root>\deploy-state.json).

    What it does
      1. Portable Node.js (pinned in modules\versions.json, shared with the other modules) in <Root>\runtime.
      2. Copies the service (no npm dependency at all) to <Root>\apps\lotr-services-<version>.
      3. <Root>\data\secrets\support-service.secret: random shared secret for the Discord bot, created once.
      4. <Root>\services\lotr-services\config.json (kept if it exists) and service.conf for LotRMP.ServiceHost.exe;
         service LotRMPServices under NT SERVICE\LotRMPServices.
      5. Data in <Root>\data\support (logs, 72 h) and <Root>\data\lotr-services (queue state), logs in
         <Root>\logs\lotr-services.
      6. deploy-state.json modules.services, then the Caddyfile is regenerated (routes /api/queue/*, /api/support,
         and with -QueueAuth the forward_auth on Core objects).

    No firewall rule: nothing listens outside loopback.

.PARAMETER Root
    Install folder of the site. Default: the folder above this script when it runs from <Root>\scripts, else
    C:\www\lotrmp.
.PARAMETER QueueAuth
    advisory (default) or enforce: adds forward_auth on /files/releases/*/objects/* so the service sees (advisory)
    or requires (enforce) a queue slot for Core objects. Off = no forward_auth at all. Saved; later runs keep it.
.PARAMETER BotAccount
    Account that runs the Discord bot on this machine (for example "NT AUTHORITY\SYSTEM", the default of the bot's
    install-autostart.cmd). Gets read access to the secret file so the bot can use SUPPORT_SERVICE_SECRET_FILE.
.PARAMETER PackageDir
    Folder holding lib\, templates\, modules\ and services\ (default: this script's folder).

.EXAMPLE
    .\Install-Services.ps1 -WhatIf
.EXAMPLE
    .\Install-Services.ps1 -QueueAuth advisory -BotAccount 'NT AUTHORITY\SYSTEM'
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Root,
    [ValidateSet('off', 'advisory', 'enforce')][string]$QueueAuth,
    [string]$BotAccount,
    [string]$PackageDir
)

$ErrorActionPreference = 'Stop'
if (-not $PackageDir) { $PackageDir = $PSScriptRoot }
. (Join-Path $PackageDir 'lib\SiteDeploy.Common.ps1')
$dry = [bool]$WhatIfPreference
$WhatIfPreference = $false
function Invoke-Action([string]$Target, [string]$Action, [scriptblock]$Do) { if ($PSCmdlet.ShouldProcess($Target, $Action)) { & $Do } }

$ServiceName = 'LotRMPServices'
$Port = 3010
$Root = Resolve-InstallRoot $Root $PackageDir
$versions = Get-VersionsManifest $PackageDir
$src = Join-Path $PackageDir 'services\lotr-services'
if (-not (Test-Path -LiteralPath (Join-Path $src 'src\server.js'))) { throw "Service source not found in $src (src\server.js)." }
$ver = [string]((Get-Content -LiteralPath (Join-Path $src 'package.json') -Raw | ConvertFrom-Json).version)

Write-Step "Updater services: lotr-services $ver"
Write-Info "Root      $Root"
if ($dry) { Write-Host '    DRY RUN (-WhatIf): nothing is changed; "What if:" lines show the planned actions.' -ForegroundColor Magenta }
$state = Assert-ModulePrerequisites $Root $dry
$prev = Get-ModuleState $Root 'services'
$auth = 'advisory'
if ($prev -and $prev['queueAuth']) { $auth = [string]$prev['queueAuth'] }
if ($QueueAuth) { $auth = $QueueAuth }

$appName = "lotr-services-$ver"
$appDir = Join-Path $Root "apps\$appName"
$svcDir = Join-Path $Root 'services\lotr-services'
$conf = Join-Path $svcDir 'service.conf'
$cfg = Join-Path $svcDir 'config.json'
$logDir = Join-Path $Root 'logs\lotr-services'
$supportDir = Join-Path $Root 'data\support'
$stateDir = Join-Path $Root 'data\lotr-services'
$secretDir = Join-Path $Root 'data\secrets'
$secretFile = Join-Path $secretDir 'support-service.secret'
$account = "NT SERVICE\$ServiceName"

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Pre-flight'
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$listen = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
if ($listen.Count -gt 0 -and -not ($svc -and $svc.Status -eq 'Running')) {
    $o = $listen[0].OwningProcess
    $msg = "TCP $Port is already used by PID $o ($((Get-Process -Id $o -ErrorAction SilentlyContinue).ProcessName))."
    if ($dry) { Write-Warn2 "$msg The real run would stop here." } else { throw $msg }
} else { Write-Ok "TCP $Port is free (or held by $ServiceName)." }

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Runtime and application'
$nodeDir = Join-Path $Root "runtime\node-v$($versions.node.version)-win-x64"
Invoke-Action $nodeDir "Install portable Node.js $($versions.node.version) (verified zip)" {
    [void](Install-NodeRuntime -Root $Root -Versions $versions)
}
Invoke-Action $appDir "Copy lotr-services $ver (no dependencies to install)" {
    if (Test-Path -LiteralPath $appDir) { Remove-DirectoryTree $appDir }
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    foreach ($item in @('src', 'package.json', 'package-lock.json', 'config.example.json', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path $src $item) -Destination $appDir -Recurse -Force
    }
    Write-Ok $appDir
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Service'
Invoke-Action $ServiceName 'Stop the service (update)' { Stop-ModuleService $ServiceName }
$hostExe = Join-Path $Root 'runtime\servicehost\LotRMP.ServiceHost.exe'
Invoke-Action $hostExe 'Compile the service wrapper from lib\ServiceHost.cs (csc.exe of .NET Framework 4.x)' {
    $script:hostExe = (Install-ServiceHost (Join-Path $PackageDir 'lib') $Root).Exe
}
Invoke-Action $secretFile 'Create the support secret shared with the Discord bot (kept if it exists)' {
    foreach ($d in @($svcDir, $logDir, $supportDir, $stateDir, $secretDir)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $secretFile)) { Write-TextFile $secretFile ((New-RandomSecret 32) + "`r`n"); Write-Ok "$secretFile created." }
    else { Write-Ok "$secretFile kept." }
}
Invoke-Action $cfg 'Write config.json (kept if it exists) and service.conf' {
    $mode = 'advisory'
    if ($auth -eq 'enforce') { $mode = 'enforce' }
    if (-not (Test-Path -LiteralPath $cfg)) {
        $c = [ordered]@{ host = '127.0.0.1'; port = $Port; root = $Root; log = [ordered]@{ console = $false }; queue = [ordered]@{ enforcement = $mode } }
        Write-JsonFile $cfg $c
    } else {
        # Keep every hand-made setting; only the enforcement mode follows -QueueAuth (Caddy and service must agree).
        $c = ConvertTo-PlainHashtable (Get-Content -LiteralPath $cfg -Raw | ConvertFrom-Json)
        if (-not $c.Contains('queue') -or $null -eq $c['queue']) { $c['queue'] = [ordered]@{} }
        if ($c['queue']['enforcement'] -ne $mode) { $c['queue']['enforcement'] = $mode; Write-JsonFile $cfg $c; Write-Ok "config.json: queue.enforcement = $mode" }
    }
    $envs = [ordered]@{ NODE_ENV = 'production'; LOTR_SERVICES_CONFIG = $cfg }
    Write-ServiceHostConfig -Path $conf -Exe (Join-Path $nodeDir 'node.exe') -Arguments 'src\server.js' -WorkDir $appDir -Log (Join-Path $logDir 'stdout.log') -Environment $envs
    Write-Ok $conf
}
Invoke-Action $ServiceName "Create/update the service (account $account, delayed auto start, restart on failure)" {
    Install-ModuleService -Name $ServiceName -DisplayName 'LotR MP Event updater services (queue, support logs)' `
        -Description 'lotr-services on 127.0.0.1:3010. Managed by Install-Services.ps1.' `
        -BinaryPath "`"$hostExe`" `"$conf`""
}
Invoke-Action "$supportDir, $stateDir, $logDir, $svcDir, $secretFile" "Grant ${account}: modify on its data and logs, read on its config, the secret and files\releases" {
    Set-RestrictedAcl $supportDir -Grants @("$($account):(OI)(CI)M")
    Set-RestrictedAcl $stateDir -Grants @("$($account):(OI)(CI)M")
    Set-RestrictedAcl $logDir -UsersRead -Grants @("$($account):(OI)(CI)M")
    Set-RestrictedAcl $svcDir -Grants @("$($account):(OI)(CI)R")
    $grants = @('/inheritance:r', '/grant:r', '*S-1-5-18:F', '*S-1-5-32-544:F', "$($account):R")
    if ($BotAccount) { $grants += "$($BotAccount):R" }
    Invoke-NativeChecked icacls.exe (@($secretFile) + $grants) "icacls on $secretFile" | Out-Null
    $rel = Join-Path $Root 'files\releases'
    if (Test-Path -LiteralPath $rel) { Add-AclGrant $rel "$($account):(OI)(CI)R" }
    Write-Ok 'ACLs set.'
}
Invoke-Action $ServiceName 'Start the service' {
    Start-Service -Name $ServiceName
    $code = Wait-HttpUp "http://127.0.0.1:$Port/healthz" 60
    if ($code -eq 200) { Write-Ok "lotr-services answers on http://127.0.0.1:$Port/healthz." }
    else { throw "lotr-services does not answer (code $code). Read $logDir\lotr-services-*.log and $logDir\stdout.log." }
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Caddy routes'
$modState = [ordered]@{
    installed = $true; version = $ver; port = $Port; service = $ServiceName; app = $appDir; queueAuth = $auth
    node = $versions.node.version; installedAt = (Get-Date).ToString('s')
    summary = "lotr-services $ver on 127.0.0.1:$Port, /api/queue/* and /api/support public, forward_auth $auth"
}
$newState = Get-DeployStateTable $Root
$newState['modules']['services'] = $modState
$caddyExe = Join-Path $Root 'caddy\caddy.exe'
$text = New-CaddyConfigText -Root $Root -TemplatePath (Get-TemplatePath $PackageDir) -State $newState -PackageName ([string]$newState['package'])
[void](Publish-CaddyConfig -Cmdlet $PSCmdlet -Root $Root -Text $text -CaddyExe $caddyExe -Reload)
Invoke-Action (Join-Path $Root $script:StateFileName) 'Record the module in deploy-state.json' {
    Save-DeployState $Root ([ordered]@{ modules = [ordered]@{ services = $modState } })
}
Invoke-Action (Join-Path $Root 'apps') 'Remove lotr-services versions older than the previous one' {
    Remove-OldVersions (Join-Path $Root 'apps') 'lotr-services-' $appName 1
}

Write-Step 'Next steps (owner)'
Write-Info "1. Discord bot (same machine): in its .env set SUPPORT_SERVICE_URL=http://127.0.0.1:$Port and"
Write-Info "   SUPPORT_SERVICE_SECRET_FILE=$secretFile (or paste the value as SUPPORT_SERVICE_SECRET), run deploy-commands.cmd, restart it."
Write-Info "2. Check: https://$($state['domain'])/api/queue/slot answers 405/404 to a GET (route live), http://127.0.0.1:$Port/healthz is ok."
if ($dry) { Write-Host '    DRY RUN finished: nothing was changed.' -ForegroundColor Magenta }
