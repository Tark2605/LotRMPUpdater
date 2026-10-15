# Shared contracts (v0 — draft, binding for parallel work)

Everything that the updater, the publish tool, the server services and the website must agree on.
Change it only through the coordinator. Anything still open is marked **OPEN** and must be handled with a
`REVIEW-AFTER(<topic>)` comment in code plus a line in `docs/review-after/<workstream>.md`.

## 1. Server layout (Caddy, under the deploy root, e.g. `C:\www\lotrmp`)

```
files\                         player downloads, never touched by site deploys (already served at /files/*)
  releases\
    channels.json              { "stable": "<releaseId>", "beta": "<releaseId>" }   (unsigned index, convenience only)
    <releaseId>\               releaseId = "<yyyyMMdd-HHmmss>-<moduleSet short hash>"
      manifest.json            the release manifest (section 2)
      manifest.json.sig        detached signature of manifest.json bytes (section 3)
      security.json            scan results sidecar (VirusTotal / Hybrid Analysis), NOT signed, updated async (DECIDED 15/10)
      objects\<sha256[0:2]>\<sha256>.<ext>   content-addressed payload, ext = transport (OPEN: compression)
  updater\
    LotRMPUpdater-<version>.exe   the Windows utility (+ .sha256)
    latest.json                { "version", "url", "sha256", "size", "releasedAt", "signed": bool }
```
Objects are content-addressed by the SHA-256 of the UNCOMPRESSED file, so unchanged files between releases are
never stored or downloaded twice (file-level delta). Transport encoding of objects: **DECIDED 15/10
(docs/COMPRESSION.md)**:
- `.zst`: zstd level 19, windowLog <= 27 (`--long=27`), single frame with content size + checksum. Decoder:
  ZstdSharp.Port (MIT, managed) `DecompressionStream` with `ZSTD_d_windowLogMax = 27`; SHA-256 computed on the
  decoded bytes while copying.
- `.raw`: stored as is when zstd saves < 3 %.
- `.zpatch`: reserved (phase 2: zstd --patch-from binary patches for 64 KB-64 MB edited files).
- Files >= 16 MiB (Core .tpac packs, big scenes): `"chunked": true` + `"chunkIndex": "<sha256>.zst"`; content-defined
  chunks FastCDC avg 256 KiB / min 64 KiB / max 1 MiB; each chunk is an object (`.zst`/`.raw`) addressed by the
  SHA-256 of its uncompressed bytes; the chunk index lists ordered {sha256, size, object}. Only chunks missing
  locally are downloaded. FastCDC ported in-house (~100 lines) with pinned test vectors.
- Objects are plain static files: no Caddy `encode`/`precompressed` on /files/releases (Range/ETag resume).

## 2. manifest.json (UTF-8, LF, canonical: keys sorted, no insignificant whitespace when signed)

```json
{
  "schema": 1,
  "releaseId": "20261015-140000-a1b2c3d4",
  "channel": "stable",
  "publishedAt": "2026-10-15T14:00:00Z",
  "publishedBy": "admin-panel:<user>",
  "notes": { "en": "short player-facing summary" },
  "game": { "minVersion": "v1.2.12", "maxVersion": null },
  "modules": [
    {
      "id": "OrderMiddleEarthEvent",
      "name": "LotRMPEvent",
      "version": "v1.0.2.2",
      "folder": "LotRMPEvent",
      "role": "event",               "role": "event | core | admin-client",
      "dependsOn": [ { "id": "LotRMPCore", "version": "v1.4.7.9" } ],
      "workshopItem": "3774022783",
      "files": [
        { "path": "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", "size": 123456,
          "sha256": "<hex>", "object": "<sha256>.raw", "objectSize": 123456, "exec": true }
      ],
      "scenes": [ { "name": "ForestBattle", "uniqueToken": "{...}", "revision": "1791917392611" } ]
    }
  ],
  "security": {
    "virustotal": [ { "sha256": "<hex>", "url": "https://www.virustotal.com/gui/file/<hex>", "detections": 0, "engines": 70, "scannedAt": "..." } ],
    "hybridAnalysis": [ { "sha256": "<hex>", "url": "...", "verdict": "no specific threat" } ]
  },
  "endpoints": { "base": "https://<domain>", "files": "/files/releases/", "queue": "/api/queue", "support": "/api/support" }
}
```
- `path` uses `/`, relative to the module folder; the updater never writes outside `<Modules>\<folder>`.
- `exec: true` for .dll/.exe (scanned by VirusTotal, Mark-of-the-Web removed after install like every file).
- `id`/`version`/`dependsOn` are read from the module's `SubModule.xml` by the publish tool (never typed by hand).
- `scenes` read from each `SceneObj/<name>/scene.xscene` (`unique_token`, `revision`).
- `security` may be filled after publication (scans are async); its absence never blocks install.
- The `<domain>` is the site's real domain at publish time (same placeholder rule as the site deploy).

## 3. Signature
- Algorithm: **ECDSA P-256 / SHA-256** (built into .NET Framework 4.8 CNG and modern .NET; no third-party crypto).
- Private key: on the server only, `<root>\data\secrets\release-signing.key` (Administrators/SYSTEM ACL), created
  by the publish tool on first run (`init-key`); public key exported as `release-signing.pub.pem`.
- The updater embeds the public key (and accepts a list, for key rotation). A manifest whose signature fails is
  rejected with a clear message.
- The site's Security page shows the public key fingerprint.

## 4. HTTP endpoints (server, behind Caddy; small Node service "lotr-services" on 127.0.0.1, port 3010)
- `GET  /files/releases/channels.json`, `/files/releases/<id>/manifest.json[.sig]`, objects — static (Caddy).
- `POST /api/queue/slot`   { "kind": "core", "client": "<random install id>" } → `200 { "token", "expiresAt" }` or
  `429 { "position", "retryAfter" }`; `POST /api/queue/release { "token" }`. Max concurrent Core downloads: config (default 40).
  Static object downloads for core objects require `?slot=<token>` (Caddy forward_auth to the service) — **OPEN**,
  may stay advisory in v1.
- `POST /api/support` (JSON, max 2 MB): { "installId", "appVersion", "log": "<text, user name already masked>" }
  → `200 { "code": "LOTR-7F3K", "expiresAt" }` (one-time, 72 h). Rate limit: 3/hour per IP and per installId.
- Bot side (local dcbot copy): `/support code:LOTR-7F3K` inside a ticket thread → bot fetches
  `GET http://127.0.0.1:3010/internal/support/<code>` (loopback only, shared secret header) and posts the log as an
  attachment, then the code is burnt.

## 5. Website integration
- Build-time: the site reads the latest published manifest + updater/latest.json from a JSON file the admin
  "Modules" publish step writes into the site source: `src/data/releases.json`
  ({ "current": <manifest subset: releaseId, publishedAt, modules[id,name,version,size,files count], security summary,
  public key fingerprint>, "updater": <latest.json>, "history": [last 10 releaseIds+dates+versions] }).
  Until the first publish, the file holds `"current": null` and the Install page keeps its "coming soon" shell.
- Pages: `/install/` (download the updater, per-store notes, what it does), `/security/` (per release: hashes,
  scans, signature fingerprint, source link, "what the updater does"), linked from footer and install page.
- The admin panel gets a "Modules" screen: pick module folders (default: the team repo clone path configured on
  the server, read-only), dry-run diff vs current release, publish (manifest + objects + signature), then trigger the
  existing site publish so `/install/` and `/security/` update.

## 6. Updater local layout (per user, no admin)
`%LOCALAPPDATA%\LotRMPUpdater\` : `app\` (exe), `cache\objects\`, `logs\YYYY-MM-DD.log`, `state.json`
(chosen game path, installed releaseId, previous releaseId for rollback, installId GUID), `backup\<releaseId>\`.

## 7. Repos and rules
- `LOTR/installer` (updater, publish tool, services, docs) and `LOTR/site` (pages, admin screen): local git only,
  never pushed; agents do NOT commit — the coordinator does.
- Never touch the team repo clone except read-only. Game installs are read-only except in tests' temp folders.

## 8. Decisions log
- 15/10: scan results live ONLY in the unsigned sidecar `fileseleases\<id>\security.json` (format = output of
  `lotr-security manifest-block`, keys `virustotal`, `hybridAnalysis`); the signed manifest.json carries no `security`
  block (no re-signing after async scans). The site and the updater read the sidecar; absence never blocks install.
- 15/10: single secrets folder on the server: `<root>\data\secrets\` (signing key, VirusTotal/Hybrid Analysis keys,
  services shared secret), ACL SYSTEM + Administrators.
- 15/10: support queue adds `POST /api/queue/heartbeat { "token" }` (extends slot TTL, 180 s without heartbeat = released).
- 15/10: agents must NEVER run bot scripts or anything reading a real .env / real API keys (incident: Discord slash
  commands re-registered on the live guild). Tests use mocks only.
- 15/10 (from workstream C, accepted): `manifest.json.sig` = `{"schema":1,"signatures":[{"alg":"ES256","keyId":"<fingerprint>","sig":"<base64 DER or P1363 as documented in docs/MANIFEST.md>"}]}`; several signatures allowed (key rotation); the updater accepts the release if ANY signature verifies with a trusted key.
- 15/10 (accepted): chunk index rows are compact arrays `[sha256, size, objectSize, ext]` in file order; a chunked file entry has `chunked: true`, `chunkIndex`, no `object`, and `objectSize` = bytes of its distinct chunks. Objects stay inside each release folder (NTFS hard links between releases); no shared pool.
- 15/10: anti-rollback: the updater checks the signed `channel` and refuses a manifest with an older `publishedAt` than the installed one unless the player explicitly chose "roll back".
