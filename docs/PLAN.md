# LotR MP Event updater — plan

Goal: a small Windows utility that installs and updates the LotR MP Event modules (LotRMPEvent, LotRMPCore,
LotRMPAdminClient for admins) on every PC store version of Mount & Blade II: Bannerlord, without the Steam Workshop
delay, and that is transparent enough that a cautious player trusts it.

Decisions (14/10/2026): stays installed and checks for updates; "Launch Bannerlord" opens the official launcher and
never touches its configuration; C# WPF on .NET Framework 4.8 with a netstandard2.0 core; local Git repository only
(`LOTR/installer`), nothing pushed; Core: shipped by the updater to everybody (decision 18/10/2026, see below), a
Steam Workshop copy being reused as a local source when present; security scans with VirusTotal + Hybrid Analysis + Microsoft submission; support through a one-time code
pasted into a Discord ticket. Binding cross-team formats live in `docs/CONTRACTS.md`.

## Steps

| # | Step | Output | State |
|---|------|--------|-------|
| 1 | **Game detection** | `LotR.GameDetection` (netstandard2.0 + net48), `lotr-detect.exe` (JSON report, user name and 8.3 profile name masked), test bench of simulated PCs, smoke test on a real PC — see `docs/DETECTION.md` | done (15/10) |
| 2 | **Release format and publishing** | signed manifest (ECDSA P-256), content-addressed objects (zstd / FastCDC chunks), `lotr-publish` (incl. `publish-updater`), verification library, local server bench (`docs/CONTRACTS.md` §1–3, `docs/MANIFEST.md`) | done (15/10) |
| 3 | **WPF utility** | `LotRMPUpdater.exe` (net48, single file, 1.56 MB Release, embedded OFL fonts): plan of actions, live log, "Verify my install", Support, Settings, About (endpoints, folders, licences), self-update, optional Start with Windows + tray | done (15/10) |
| 4 | **Download and install engine** | file-level delta, chunk-level delta for big files, cache, resume, atomic swap, rollback/recovery, Core download queue with heartbeat; Steam Workshop copy reused as a read-only local source (0.1.1) | done (18/10) |
| 5 | **Server services** | static files behind Caddy, Core download queue (advisory or enforce), support log upload + one-time code, Discord bot `/support` hand-off (`docs/CONTRACTS.md` §4); rehearsed end to end on loopback (`tools/rehearsal/Run-Rehearsal.ps1`) | done, pending the real server |
| 6 | **Security pipeline** | VirusTotal + Hybrid Analysis per release (`lotr-security`, `publish --scan`), Microsoft false-positive submission, code signing scripts, reproducible build check | automation ready, pending accounts / keys |
| 7 | **Site integration and rollout** | `/install/` and `/security/` pages fed by `releases.json`, admin "Modules" screen (`<root>\tools\lotr-publish`), beta channel with a few players, then stable | beta pending |

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
   Optional, off by default: "Start with Windows (minimised to the tray)" = one shortcut in the user's own Startup
   folder (no registry Run key), explained in Settings and About; the tray icon says when an update is ready.
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
  0–6 h jitter, so the event-day peak is spread out; installing is then instant (still behind a click). With "Start with
  Windows" on, this also happens while the window is closed (tray icon).
- **6 parallel connections per client by default, 8 at most** (0.1.1, 18/10: the Core is ~38,000 small objects, so
  latency limits a single connection; the .NET Framework per-host limit of 2 is raised explicitly).
- **Resume and exponential backoff**: interrupted downloads resume (HTTP Range); 503 / 429 honour `Retry-After`, then
  back off exponentially with jitter.
- **Core download slots**: a small server queue hands out Core download tokens (e.g. 40 concurrent); the utility shows
  the player's position in the queue.
- **Steam players reuse their Workshop Core** (decision 18/10/2026): the beta ships LotRMPCore through the updater to
  everybody, into `Modules\LotRMPCore`; when a Steam Workshop copy is present (item `workshopItem` of the manifest, else
  any item whose `SubModule.xml` has the module Id, at the item root or one level deeper), every file with the release's
  SHA-256 is copied from it instead of downloaded, and a changed big file only downloads its missing chunks. The
  Workshop folder is only read, never changed; the player may keep or unsubscribe the Workshop item.
- Later options: a Cloudflare R2 mirror (no egress fees), and a torrent with a web seed for the Core.

## Decisions after step 3 (15/10/2026)

- Game version outside the release's `game.minVersion/maxVersion`: **warning only**, never blocks (owner decision).
- The updater updates itself from `files/updater/latest.json` (signed with the release key, `latest.json.sig`): new
  exe verified (SHA-256, release-key signature or same Authenticode signer), swapped at the next start, previous exe
  kept for rollback.
- Open items after the final review pass: `docs/review-after/RESOLVED.md`.
