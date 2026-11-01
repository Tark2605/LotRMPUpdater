# LotR MP Event updater

Installer / updater for the LotR MP Event Bannerlord modules. Plan and principles: `docs/PLAN.md`.
Shared formats between workstreams: `docs/CONTRACTS.md`. Local Git only, never pushed.

## Game detection (step 1)

| Project | Target | What |
|---|---|---|
| `src/LotR.GameDetection` | netstandard2.0, net48 | finds Bannerlord (Steam, Epic, GOG, Xbox PC) and its `Modules` folder — `GameLocator.FindAll()` / `GameLocator.Validate(path)` |
| `src/LotR.Detect.Cli` | net48 | `lotr-detect.exe`: prints the detection report as JSON (for support) |
| `tests/LotR.GameDetection.Tests` | net8.0, net48 | xUnit test bench: simulated PCs (in-memory disks, registry, processes, shortcuts) + a skippable read-only smoke test on the real PC |

Details, sources and research links: `docs/DETECTION.md`.

**Read-only guarantee.** The detection library and `lotr-detect` never write to the registry and never create, modify
or delete anything on disk, with one exception: the Modules write check, which by default creates a hidden 0-byte
`.lotr-write-probe-<guid>.tmp` with `FILE_FLAG_DELETE_ON_CLOSE` and closes it at once, so Windows removes it
immediately (even if the process crashes). Use `--write-check accesscheck` (ask Windows about the permissions, write
nothing) or `--write-check none` to avoid even that. Nothing ever asks for administrator rights.

```
dotnet build src/LotR.Detect.Cli -c Release
src/LotR.Detect.Cli/bin/Release/net48/lotr-detect.exe [--deep] [--write-check tempfile|accesscheck|none] [--no-process] [--mask-user] [--pick <path>]
dotnet test tests/LotR.GameDetection.Tests          # both frameworks; set LOTR_SKIP_SMOKE=1 to skip the real-PC tests
```
Exit code: 0 found, 1 not found, 2 error.

## Build, test, rehearse (everything)

Requirements: .NET SDK 8 (builds the net48 targets too, no Visual Studio needed), Node 24 for `lotr-services`,
Caddy 2 (winget `CaddyServer.Caddy`) for the rehearsal.

```
dotnet build LotR.Installer.sln                 # every project, Debug (net8.0 + net48 + netstandard2.0)
dotnet test  LotR.Installer.sln                 # every test project on net8.0 and net48 (LOTR_SKIP_SMOKE=1 skips the real-PC detection tests)
cd services\lotr-services && npm test           # lotr-services (node --test)
powershell -ExecutionPolicy Bypass -File tools\rehearsal\Run-Rehearsal.ps1 [-Keep] [-SkipBuild]
```

| Project | Target | What |
|---|---|---|
| `src/LotR.Releases` | netstandard2.0, net48 | manifest model, canonical JSON, ECDSA signatures, zstd/FastCDC, publish + verification, Authenticode check |
| `src/Lotr.Updater.Core` | netstandard2.0, net48 | install engine, queue client, self-update, headless runner (no UI types) |
| `src/Lotr.Updater` | net48 WPF | `LotRMPUpdater.exe` (single file, embedded DLLs and fonts) |
| `tools/LotR.Publish` | net48 | `lotr-publish` (keys, plan, publish, verify, channels, scans, site JSON, `publish-updater`) — `docs/MANIFEST.md` |
| `tools/security/Lotr.Security` | net48 | `lotr-security` (VirusTotal / Hybrid Analysis) — `docs/SECURITY-AUTOMATION.md` |
| `services/lotr-services` | Node 24 | Core download queue + support codes (loopback, behind Caddy) |
| `tests/*`, `tools/security/Lotr.Security.Tests` | net8.0 + net48 (security: net48) | xUnit; `tests/Lotr.Updater.DevServer` = dev helper (fake game, fake Core, local server) |

**Release build of the updater**: `dotnet build src/Lotr.Updater -c Release` fails (`LOTR001`) until the production
release key is pasted in `src/Lotr.Updater.Core/Security/Keys/` (`docs/MANIFEST.md`, "Production key");
`-p:AllowNoProductionKey=true` builds a test exe that refuses every release.

**Rehearsal** (`tools/rehearsal/Run-Rehearsal.ps1`): in a temp folder, on 127.0.0.1 only — throw-away key, publish a
COPY of the real event module (read-only source: `LOTR/colleague/github-LotRMPEvent/LotRMPEvent`) + a fake Core,
lotr-services (enforce mode) + Caddy (files, forward_auth, /api), the real `LotRMPUpdater.exe` in `--dev --headless`
mode into a fake game folder, a second release + a newer updater (delta update, self-update staged, swapped at the
next start), a support upload fetched back on the bot route, a tampered manifest refused; prints a PASS/FAIL table,
stops every process it started by PID and deletes the folder (`-Keep` keeps it).

**Dev runs of the app** (never the real game, never a real server): `LotRMPUpdater.exe --dev --server http://localhost:<port>/
--home <temp> --game <fake game> --trust <test .pub.pem>` (`--tour <dir>` saves screenshots, `--headless --report <file>
[--self-update] [--check-only] [--send-support]` runs without a window). Fonts: `tools/fonts/make_updater_fonts.py`
rebuilds `src/Lotr.Updater/Resources/Fonts/` from the site's OFL packages.
