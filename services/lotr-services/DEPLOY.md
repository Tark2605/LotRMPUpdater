# Deploying lotr-services

> **REVIEW-AFTER(deploy-wiring)** — this page and the files in `deploy\` are written by the services workstream to
> match the site deploy package (`LOTR\site\deploy\package`). The site deploy owner merges them; nothing here has
> run on the server yet. The Caddy part was validated (`caddy validate`) and exercised end to end on a workstation
> with Caddy v2.11.7 (enforce mode, slots, rate limits, internal routes refused through the proxy).

## What runs where

| Piece | Where | How |
|---|---|---|
| `lotr-services` | web server, `127.0.0.1:3010` | Windows service `LotRMPServices` = `LotRMP.ServiceHost.exe` (compiled from `lib\ServiceHost.cs`) running the pinned portable Node 24.21.0 from `<root>\runtime`, account `NT SERVICE\LotRMPServices` |
| code | `<root>\apps\lotr-services-<version>\` | copied from the package, no `npm install` (no dependency) |
| config | `<root>\services\lotr-services\config.json` + `service.conf` | config kept across updates; `service.conf` regenerated |
| data | `<root>\data\support\` (logs, 72 h), `<root>\data\lotr-services\queue-state.json` | service account: Modify |
| secret | `<root>\data\secrets\support-service.secret` | SYSTEM + Administrators full, service account and bot account Read |
| logs | `<root>\logs\lotr-services\` | JSON lines, daily + size rotation, 14 days; `stdout.log` rotated by ServiceHost |
| public routes | Caddy site block | `/api/queue/*`, `/api/support`; optional `forward_auth` on `/files/releases/*/objects/*` |

Nothing listens outside loopback: no firewall rule. `/internal/*` and `/healthz` are never routed by Caddy, and the
service refuses `/internal/support/*` and `/healthz` when the request carries proxy headers anyway.

## Files to merge into `site\deploy\package`

1. **Service source**: copy `src\`, `package.json`, `package-lock.json`, `config.example.json`, `README.md` to
   `package\services\lotr-services\` (the package builder can copy them from `LOTR\installer\services\lotr-services`).
2. **Installer**: `deploy\Install-Services.ps1` → `package\Install-Services.ps1` (draft following
   `Install-Monitoring.ps1`: `-WhatIf`, `Invoke-Action`, `Assert-ModulePrerequisites`, `Install-NodeRuntime`,
   `Install-ServiceHost`, `Write-ServiceHostConfig`, `Install-ModuleService`, `Set-RestrictedAcl`, deploy-state
   `modules.services`, then `New-CaddyConfigText` + `Publish-CaddyConfig -Reload`). Pure ASCII, parses under
   Windows PowerShell 5.1. Optionally call it from `Install-All.ps1` / `Deploy-Site.ps1 -WithServices`.
3. **Caddy routes**: `deploy\caddy-routes.snippet.ps1`, five numbered parts:
   1. constants `$script:ServicesService = 'LotRMPServices'`, `$script:ServicesPort = 3010`;
   2. the route block for `New-CaddyConfigText` (after the monitoring block), which fills `$routes` and
      `$filesAuth` from `deploy-state.json` `modules.services` (`installed`, `queueAuth`);
   3. `.Replace('@@FILES_AUTH@@', ...)` in the replace chain;
   4. the placeholder `@@FILES_AUTH@@` as first line inside `handle_path /files/* {` of
      `templates\Caddyfile.template`;
   5. `$script:ServicesService` in the list of services `Install-ServiceHost` stops and restarts around a wrapper swap.

Generated result (see `deploy\Caddyfile.example` for the exact validated text):

```caddy
	handle_path /files/* {
		@releaseObjects path_regexp releaseobj ^/releases/[^/]+/objects/
		forward_auth @releaseObjects 127.0.0.1:3010 {
			uri /internal/queue/check
		}
		...existing root / headers / file_server browse...
	}
	handle /api/queue/* {
		request_body {
			max_size 16KB
		}
		reverse_proxy 127.0.0.1:3010
	}
	handle /api/support {
		request_body {
			max_size 3MB
		}
		reverse_proxy 127.0.0.1:3010
	}
```

Notes from the test run:

- Use `path_regexp` for the objects matcher: `path /releases/*/objects/*` (wildcard in the middle) never matched,
  so `forward_auth` silently did not run.
- `forward_auth` forwards the query string (`/internal/queue/check?slot=...`) and the original URI in
  `X-Forwarded-Uri`; the service reads either. Range requests (download resume) go through the same check.
- With `queueAuth = off` no `forward_auth` is generated at all. `advisory` (default) costs one loopback request
  per object download and never refuses one; `enforce` needs the updater to send `?slot=<token>` on Core objects.
- Caddy cuts `/api/support` bodies over 3 MB; the service itself answers a JSON `413` above 2 MB.
- The `/api/` paths are not cached by the site's `@html` header rule (they do not end with `/` or `.html`).

## Install (once the merge is done)

From an elevated PowerShell on the server, in the package folder:

```powershell
.\Install-Services.ps1 -WhatIf                                   # dry run
.\Install-Services.ps1 -QueueAuth advisory -BotAccount 'NT AUTHORITY\SYSTEM'
```

Then check `http://127.0.0.1:3010/healthz` (RDP) and, from outside, that `POST https://<domain>/api/queue/slot`
answers JSON. Updating = running it again with the new package (config, secret and data are kept; the previous
app folder is kept for rollback).

## Discord bot

The bot reads the log on loopback, so it must run **on the web server** (see REVIEW-AFTER(bot-host) if it stays on
the game server). In the bot's `.env`:

```
SUPPORT_SERVICE_URL=http://127.0.0.1:3010
SUPPORT_SERVICE_SECRET_FILE=C:\www\lotrmp\data\secrets\support-service.secret
```

(or `SUPPORT_SERVICE_SECRET=<value of that file>` when the bot's account cannot be given read access). Run
`deploy-commands.cmd` once to register `/support`, restart the bot, run `/tickets panel` again to get the
**Updater help** button. Update the bot file by file (`index.js`, `deploy-commands.js`, `test\`, docs), never by
extracting an archive over a live install: its `.json` data files and `.env` must stay.

## Operations

- Health: `GET http://127.0.0.1:3010/healthz` → queue counts per kind, enforcement mode, forward_auth counters
  (`checks`, `missing`, `invalid`: how many Core downloads would be refused in enforce mode), stored support logs.
  Add it as an Uptime Kuma HTTP monitor (keyword `"ok":true`).
- Change the concurrency: `queue.maxConcurrent` in `config.json`, restart the service. Switch enforcement:
  `Install-Services.ps1 -QueueAuth enforce` (Caddy) **and** `queue.enforcement` `"enforce"` in `config.json`.
- Rotate the support secret: stop the service, replace the file content, start it, update the bot if it holds the
  value in `SUPPORT_SERVICE_SECRET`.
- A support log is personal data even with the user name masked: it is deleted 72 h after upload, or replaced by an
  empty tombstone as soon as the bot fetched it.
