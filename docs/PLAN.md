# LotR MP Event updater — plan

Goal: a small Windows utility that installs and updates the LotR MP Event modules (LotRMPEvent, LotRMPCore,
LotRMPAdminClient for admins) on every PC store version of Mount & Blade II: Bannerlord, without the Steam Workshop
delay, and that is transparent enough that a cautious player trusts it.

Decisions (14/10/2026): stays installed and checks for updates; "Launch Bannerlord" opens the official launcher and
never touches its configuration; C# WPF on .NET Framework 4.8 with a netstandard2.0 core; local Git repository only
(`LOTR/installer`), nothing pushed; Core: Steam Workshop first (when it is at the right version), our server as the
fallback; security scans with VirusTotal + Hybrid Analysis + Microsoft submission; support through a one-time code
pasted into a Discord ticket. Binding cross-team formats live in `docs/CONTRACTS.md`.

## Steps

| # | Step | Output | State |
|---|------|--------|-------|
| 1 | **Game detection** | `LotR.GameDetection` (netstandard2.0 + net48), `lotr-detect.exe` (JSON report), test bench of simulated PCs, smoke test on a real PC — see `docs/DETECTION.md` | done (15/10) |
| 2 | **Release format and publishing** | signed manifest (ECDSA P-256), content-addressed objects, publish CLI, verification library, local server bench (`docs/CONTRACTS.md` §1–3) | in progress |
| 3 | **WPF utility skeleton** | screens: plan of actions, live log, "Verify my install", About (endpoints), settings; no real download yet | next |
| 4 | **Download and install engine** | file-level delta, cache, resume, atomic swap of a module folder, rollback to the previous release, Workshop Core reuse | |
| 5 | **Server services** | static files behind Caddy, Core download queue, support log upload + one-time code, Discord bot `/support` hand-off (`docs/CONTRACTS.md` §4) | |
| 6 | **Security pipeline** | VirusTotal + Hybrid Analysis per release, Microsoft false-positive submission, code signing, reproducible build | |
| 7 | **Site integration and rollout** | `/install/` and `/security/` pages fed by `releases.json`, admin "Modules" screen, beta channel with a few players, then stable | |

## Transparency principles

The utility must earn trust by showing everything it does. These are requirements, not nice-to-haves:

1. **Plan before action.** Before touching anything, the exact plan is shown: which files will be added, replaced or
   removed, in which folder, and how much will be downloaded.
2. **Nothing without a click.** No action starts on its own; every install, update, repair or uninstall waits for the
   player's click (background *pre-download* into the cache is the only automatic activity, see Bandwidth, and can be
   turned off).
3. **Readable live log, saved to a text file.** Every step is written in plain language in a log window and in
   `%LOCALAPPDATA%\LotRMPUpdater\logs\YYYY-MM-DD.log`.
4. **Narrow footprint.** It touches only `<game>\Modules\<our module folders>`, its own folder
   (`%LOCALAPPDATA%\LotRMPUpdater`) and its cache. Never the game files, the launcher configuration, the registry
   (beyond its own uninstall entry, per user), other mods or saves.
5. **No admin, no background service, no telemetry.** Runs as the current user (`asInvoker` manifest); if Modules is not
   writable it explains how to fix it instead of elevating. No Windows service, no scheduled task, no usage statistics.
6. **About screen lists the exact network endpoints** it may contact (our domain's `/files/` and `/api/` paths, nothing
   else), with the reason for each.
7. **"Verify my install"** button: re-hashes every installed file against the signed manifest and lists differences,
   with an optional repair.
8. **Per-release proof**: each release has a signed manifest, SHA-256 of every file, VirusTotal and Hybrid Analysis
   report links and a link to the source; all of it shown in the app and on the site's Security page.
9. **Support log shown in full before sending.** The player sees the whole text (Windows user name masked) and decides;
   the upload returns a one-time code valid 72 h (e.g. `LOTR-7F3K`), rate-limited, which the player pastes in a Discord
   ticket.
10. **Clean uninstall**: removes the utility, its cache and logs, and asks whether to keep or remove the installed mod
    files.
11. **Signed, reproducible build** (later): Authenticode signature and a documented build that anyone can reproduce
    from the source to get the same hashes.

Step 1 already follows these rules: the detection library and `lotr-detect` are read-only; the only disk write is
an optional delete-on-close 0-byte probe in `Modules` (see `docs/DETECTION.md`).

## Bandwidth

The Core module is about 9.5 GB, so distribution has to be frugal for the server and for players:

- **File-level delta updates**: objects are addressed by SHA-256; a new release only downloads files whose hash
  changed (`docs/CONTRACTS.md` §1).
- **Background pre-download**: when a release is published, running utilities fetch it into the cache after a random
  0–6 h jitter, so the event-day peak is spread out; installing is then instant (still behind a click).
- **2–4 parallel connections per client**, never more.
- **Resume and exponential backoff**: interrupted downloads resume (HTTP Range); 503 / 429 honour `Retry-After`, then
  back off exponentially with jitter.
- **Core download slots**: a small server queue hands out Core download tokens (e.g. 40 concurrent); the utility shows
  the player's position in the queue.
- **Steam players reuse the Workshop Core** when the subscribed Workshop item is at the version the release requires;
  only Epic/GOG/Xbox players (or out-of-date Workshop copies) download the Core from us.
- Later options: a Cloudflare R2 mirror (no egress fees), and a torrent with a web seed for the Core.
