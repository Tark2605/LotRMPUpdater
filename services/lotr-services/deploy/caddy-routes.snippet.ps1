# REVIEW-AFTER(deploy-wiring): ready-to-merge fragment for site\deploy\package\lib\SiteDeploy.Services.ps1.
# Not a standalone script. See DEPLOY.md ("Caddy routes") for where each part goes.

# --- (1) Next to the other module constants ($script:KumaPort ...):
$script:ServicesService = 'LotRMPServices'
$script:ServicesPort    = 3010

# --- (2) In New-CaddyConfigText, after the monitoring block and before "$template = ...":
    # --- Updater services (lotr-services, Install-Services.ps1): the Core download queue and the support log upload.
    # /internal/* and /healthz are never routed: the service also refuses them when they come through a proxy.
    $sv = $null
    if ($modules.Contains('services')) { $sv = $modules['services'] }
    $filesAuth = New-Object System.Collections.Generic.List[string]
    if ($sv -and $sv['installed'] -and -not $LocalTest) {
        $sp = $script:ServicesPort
        $routes.Add("$T# Updater services (lotr-services on 127.0.0.1:$sp, Install-Services.ps1).")
        $routes.Add("${T}handle /api/queue/* {")
        $routes.Add("$T${T}request_body {")
        $routes.Add("$T$T${T}max_size 16KB")
        $routes.Add("$T$T}")
        $routes.Add("$T${T}reverse_proxy 127.0.0.1:$sp")
        $routes.Add("$T}")
        $routes.Add("${T}handle /api/support {")
        $routes.Add("$T$T# The service refuses more than 2 MB with a JSON 413; Caddy cuts anything far bigger before it.")
        $routes.Add("$T${T}request_body {")
        $routes.Add("$T$T${T}max_size 3MB")
        $routes.Add("$T$T}")
        $routes.Add("$T${T}reverse_proxy 127.0.0.1:$sp")
        $routes.Add("$T}")
        if ($sv['queueAuth'] -and $sv['queueAuth'] -ne 'off') {
            # Inside handle_path /files/* (prefix already stripped): every release object asks the service first.
            # The service answers 200 for non-Core objects, and for Core objects 200 (advisory) or 403 without a slot (enforce).
            $filesAuth.Add("$T$T# Core download queue (lotr-services, mode $($sv['queueAuth'])): objects need ?slot=<token> from /api/queue/slot.")
            # path_regexp, not "path /releases/*/objects/*": a wildcard in the middle of a path matcher never matched (Caddy 2.11, tested).
            $filesAuth.Add("$T$T@releaseObjects path_regexp releaseobj ^/releases/[^/]+/objects/")
            $filesAuth.Add("$T${T}forward_auth @releaseObjects 127.0.0.1:$sp {")
            $filesAuth.Add("$T$T${T}uri /internal/queue/check")
            $filesAuth.Add("$T$T}")
        }
    }

# --- (3) In the .Replace(...) chain of New-CaddyConfigText, add:
#         .Replace('@@FILES_AUTH@@', ($filesAuth.ToArray() -join "`r`n"))

# --- (4) In templates\Caddyfile.template, first line inside "handle_path /files/* {":
#         @@FILES_AUTH@@

# --- (5) In Install-ServiceHost, add $script:ServicesService to the list of services stopped/started around a
#         wrapper swap:  foreach ($s in @($script:KumaService, $script:UmamiService, $script:ServicesService)) { ... }
