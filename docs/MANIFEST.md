# Release manifest, signing and publishing (workstream C)

Status: implemented 15/10/2026 — `src/LotR.Releases` (library, netstandard2.0 + net48), `tools/LotR.Publish`
(`lotr-publish.exe`, net48), `tests/LotR.Releases.Tests` (net8.0 + net48). Binding rules: `docs/CONTRACTS.md` §1–3, §5,
§8. Open points: `docs/review-after/C-manifest.md`.

## 1. What a release is

```
files\releases\
  channels.json                      {"beta":"<id>","stable":"<id>"}   unsigned index, canonical JSON
  <releaseId>\                       releaseId = <yyyyMMdd-HHmmss UTC>-<first 8 hex of SHA-256(canonical modules array)>
    manifest.json                    canonical JSON (below), the signed bytes
    manifest.json.sig                detached signature(s) over the exact bytes of manifest.json
    security.json                    optional, unsigned, written later by tools/security (CONTRACTS §8)
    objects\ab\ab12…ef.zst|.raw      content-addressed objects (whole files, chunks, chunk indexes)
```

- **Canonical JSON**: UTF-8 without BOM, keys sorted by ordinal UTF-16 order, no whitespace, minimal escaping
  (`"`, `\`, control characters only; non-ASCII stays raw UTF-8), integers only, no trailing newline. The writer is
  in-house (`LotR.Releases.Json`), the parser is strict (no duplicate keys, no comments, depth ≤ 64). The canonical
  bytes of a reference manifest are pinned by a unit test: changing them requires a schema bump.
- **Objects** are named by the SHA-256 of their **uncompressed** bytes. Transport (CONTRACTS §1, decided 15/10):
  `.zst` = zstd level 19, windowLog ≤ 27 + long-distance matching, one frame with content size and checksum
  (ZstdSharp.Port 0.8.8, MIT, managed); `.raw` when zstd saves < 3 %. `.zpatch` is reserved and refused. Every zst
  object is checked structurally before decoding (one standard frame, no dictionary, content size present,
  checksum present, window ≤ 2^27, nothing after the frame) and the decoded bytes must hash to the object name.
- **Chunked files** (≥ 16 MiB): FastCDC 2020, normalised level 1, avg 256 KiB / min 64 KiB / max 1 MiB, seed 0.
  The gear table and output are pinned by vectors generated with `pyfastcdc` 0.3.0
  (`tests/LotR.Releases.Tests/Data/gen-fastcdc-vectors.py` → `fastcdc-vectors.txt`). Each chunk is an object.
  The **chunk index** is an object too (3 % rule applies), canonical JSON:
  `{"chunker":"fastcdc2020;avg=262144;min=65536;max=1048576;nc=1","chunks":[["<sha256>",size,objectSize,"zst"],…],"schema":1,"sha256":"<whole file>","size":N}`.
- **Same content, stored once**: an object that an older release already holds is NTFS **hard-linked** into the new
  release folder (zero bytes written; deleting an old release never breaks a newer one). Copy is the fallback on
  another volume. The publish result reports written / linked / copied objects.

### manifest.json (schema 1)

Fields as in CONTRACTS §2, with these precisions:

| Field | Rule |
|---|---|
| `modules[].id/name/version` | read from `SubModule.xml` (`Id`, `Name`, `Version` `@value`), version kept verbatim |
| `modules[].dependsOn[]` | every `DependedModule` (`Id`, `DependentVersion` → `version` when present, `Optional="true"` → `optional:true`) plus BLSE `DependedModuleMetadata order="LoadBeforeThis"` |
| `modules[].folder` | the module folder name (the only place the updater writes for that module) |
| `modules[].role` | `event`, `core` or `admin-client` (from the CLI option used) |
| `modules[].workshopItem` | `--workshop role=id`, else inherited from the channel's current release |
| `modules[].scenes[]` | each `SceneObj/<folder>/scene.xscene`: `name` = folder, `uniqueToken`, `revision` (root tag only, streaming) |
| `files[].path` | `/` separators, relative, safe: no `..`, no root, no `:`/`\`/`*?"<>|`, no device names, no trailing dot/space, ≤ 240 chars, unique case-insensitively |
| `files[].object` | `<sha256>.<ext>` of the whole file; **absent** for chunked files |
| `files[].chunked`, `chunkIndex` | `true` and `<sha256 of index>.<ext>` for files ≥ 16 MiB |
| `files[].objectSize` | stored bytes of the object; for a chunked file the sum of its **distinct** chunks (index not included) |
| `files[].exec` | `true` for `.dll`/`.exe` |
| `game.minVersion/maxVersion` | `--game-min/--game-max`, else inherited, else null |
| `endpoints` | `--base-url https://<domain>` (+ fixed `/files/releases/`, `/api/queue`, `/api/support`), else inherited |
| `security` | **never written** (CONTRACTS §8): scan results live in `security.json` |

Excluded when scanning a module folder: `.git/`, `.gitattributes`, `.gitignore`, `Thumbs.db`, `desktop.ini`,
`.DS_Store`, `*.tmp`, `~$*` (+ `--exclude <glob>`). Links/junctions inside a module folder are refused.

### manifest.json.sig

```json
{"schema":1,"signatures":[{"alg":"ES256","keyId":"<64 hex: SHA-256 of the SubjectPublicKeyInfo DER>","sig":"<base64 of r||s, 64 bytes>"}]}
```

ECDSA P-256 / SHA-256 over the exact bytes of `manifest.json` (no re-canonicalisation on the reader side). Several
signatures may be present (key rotation); a manifest is accepted when one of them verifies with a trusted key.

## 2. Publishing (server)

Build: `dotnet build tools\LotR.Publish -c Release` → `tools\LotR.Publish\bin\Release\net48\lotr-publish.exe`
(+ `LotR.Releases.dll`, `ZstdSharp.dll`, `System.Memory.dll`… copy the whole folder). Run from an elevated prompt,
or as an account granted on the key (`init-key --grant`). `--root C:\www\lotrmp` is a shortcut for
`--releases C:\www\lotrmp\files\releases --key C:\www\lotrmp\data\secrets\release-signing.key`.

```bat
:: once
lotr-publish init-key --root C:\www\lotrmp [--grant "NT SERVICE\LotRMPServices"]

:: every release
lotr-publish plan    --root C:\www\lotrmp --event D:\repo\LotRMPEvent [--core ...] [--admin-client ...] [--exact] [--json]
lotr-publish publish --root C:\www\lotrmp --event D:\repo\LotRMPEvent --workshop event=3774022783 ^
                     --base-url https://<domain> --notes "Short player-facing summary" --by admin-panel:<user> [--scan]
lotr-publish export-site-json --root C:\www\lotrmp --out C:\lotrmp\site\src\data\releases.json

:: any time
lotr-publish verify C:\www\lotrmp\files\releases\<id> --root C:\www\lotrmp
lotr-publish scan   C:\www\lotrmp\files\releases\<id> [--updater-exe ...] [--source-modules D:\repo]
lotr-publish set-channel stable <older id> --root C:\www\lotrmp        :: rollback
lotr-publish verify-install "<game>\Modules\LotRMPEvent" --release C:\www\lotrmp\files\releases\<id> --root C:\www\lotrmp
```

- `plan` hashes and chunks only (fast); new objects are counted at their raw size (upper bound). `--exact` also
  compresses them in memory to give the exact download size. Nothing is written.
- `publish` = plan + compress new objects (all cores) + write them into a hidden `.staging-*` folder + manifest +
  signature (self-checked) → atomic rename to `<releaseId>` → full `verify` (decode and hash every object; a
  failing release is moved to `.failed-<id>` and the channel is not touched) → atomic replace of `channels.json`.
  `--no-channel` publishes without switching players; `--no-verify` skips the final check (not recommended).
- `--json` prints machine-readable results for the admin panel "Modules" screen (diff lists, sizes, warnings).
- `scan` runs `tools\security\Invoke-ReleaseScans.ps1`, which writes `security.json`. That script finds its own API
  keys and skips a service without a key; `lotr-publish` never reads them. Exit 3 = "run again later".
  `publish --scan` runs it after a successful publish and never undoes the publication (with `--json` too: the
  scan output then goes to stderr, stdout stays the JSON document). On the server the site deploy package installs
  the tools as `<root>\tools\lotr-publish\` and `<root>\tools\lotr-security\` (script + exe), where `--scan` finds them.
- Exit codes: 0 ok, 1 failure (incl. verification), 2 usage error. `LOTR_PUBLISH_DEBUG=1` prints stack traces.

### Publishing the updater itself (`publish-updater`)

```
lotr-publish publish-updater --exe <signed LotRMPUpdater.exe> --root C:\www\lotrmp [--channel stable] [--version 1.2.3]
                             [--base-url https://<domain>] [--key <file>] [--no-sign] [--force] [--json]
```
- Writes into `<root>\files\updater\`: `LotRMPUpdater-<version>.exe`, `LotRMPUpdater-<version>.exe.sha256`
  (`<hex>  <name>` + LF, the format of `Sign-Updater.ps1 -WriteSha256`; this command is the one owner of the
  published copy), `latest.json` (`latest-<channel>.json` for another channel) and `latest.json.sig`.
- Every file is written to a temporary name and renamed; the signature, then `latest.json`, are written LAST, so an
  updater never sees a `latest.json` that points at a missing exe. Re-publishing the same bytes is a no-op; other
  bytes under an existing version are refused (bump the version, or `--force`).
- `version` comes from the exe's version resource (`0.1.0.0` → `0.1.0`) unless `--version` is given; `url` is
  site-relative (`/files/updater/...`) unless `--base-url https://...`; `signed` = the exe carries a VALID
  Authenticode signature (an invalid one is refused).
- `latest.json.sig` (same JSON format as `manifest.json.sig`) is signed with the release key (`--key`, default the
  `--root` key); `--no-sign` skips it and removes a stale one. Additive to CONTRACTS §1 (coordinator to fold in).
- The updater accepts a new exe only when its SHA-256 matches `latest.json` AND (`latest.json.sig` verifies with an
  embedded release key, OR both the running and the new exe are Authenticode-signed by the same subject). A signed
  updater never accepts an unsigned or differently signed one. The new exe is staged in
  `%LOCALAPPDATA%\LotRMPUpdater\app\update\`, swapped in at the next start, and the previous one is kept as
  `LotRMPUpdater.old.exe` (About › "Go back to the previous version" = `--rollback-updater`).

### Production key (embedding it in the updater)

The updater trusts every `src/Lotr.Updater.Core/Security/Keys/*.pub.pem` (embedded resources `trusted-keys/<file>`).
The folder holds only a README placeholder until the real key exists. After `lotr-publish init-key` ran on the
production server:

1. Copy **only** `<root>\data\secrets\release-signing.pub.pem` (never the `.key`) to
   `src/Lotr.Updater.Core/Security/Keys/release-signing.pub.pem` (rotation: add the second `.pub.pem` next to it).
2. Check the fingerprint: `lotr-publish show-key --pub src\Lotr.Updater.Core\Security\Keys\release-signing.pub.pem`
   must equal the server's (and, later, the site's Security page). The app's About page lists it too.
3. `dotnet build src/Lotr.Updater -c Release`. A **Release** build FAILS (error `LOTR001`) while the folder has no
   `*.pub.pem`; `-p:AllowNoProductionKey=true` builds a test exe that refuses every release. Debug builds and the
   tests never need the key (dev runs pass `--trust <pem>`).

## 3. Key management

- **Storage** (`init-key`): `<root>\data\secrets\release-signing.key` + `release-signing.pub.pem` (SPKI PEM).
  - Default `--protection dpapi`: PKCS#8 encrypted with DPAPI **machine** scope (`-----BEGIN LOTR DPAPI PRIVATE KEY-----`).
    A copy of the file (backup, disk image) is useless on another machine; on this machine any process could decrypt
    it, so the ACL is what protects it locally.
  - `--protection plain`: standard unencrypted PKCS#8 PEM (`openssl pkey -in` reads it). Use it only for the offline
    backup, on removable media kept off the server.
  - ACL on both kinds: inheritance removed; full control for SYSTEM and BUILTIN\Administrators; read for each
    `--grant` account (`current` = the user running the command). `--no-acl` keeps the inherited ACL (tests only).
  - The file is created empty, locked down, then written: the secret never sits under an inherited ACL.
- **Fingerprint** (Security page, `show-key`): SHA-256 of the SPKI DER in 16 groups of 4 hex characters, e.g.
  `b519 ccfd 0dce 3b16 …`. The same 64 hex characters are the `keyId` in signature files.
- **Backup**: a copy of the DPAPI file is NOT a backup (it only opens on this machine). Right after `init-key`:
  `lotr-publish export-key --root C:\www\lotrmp --out E:\release-signing.backup.key` (plain PKCS#8 to removable
  media, then unplug it and keep it offline). To restore on a new server:
  `lotr-publish export-key --key E:\release-signing.backup.key --out C:\www\lotrmp\data\secrets\release-signing.key --protection dpapi`.
- **Rotation** (planned, no downtime):
  1. Create the new key (`init-key --out ...\release-signing-2026b.key`).
  2. Ship an updater build that trusts **both** public keys.
  3. Publish with both keys (`--key old --key new`): both signatures are written; old and new updaters accept.
  4. When old updaters are gone (forced update via `latest.json`), publish with the new key only and remove the old
     public key from the next updater build.
- **Compromise**: publish an updater that trusts only a new key (signed with Authenticode if available), delete the
  old key, re-publish the current modules with the new key. Players on an old updater must download the new updater
  manually from the site (the site shows the new fingerprint).

## 4. What the updater reads (library API)

Order of checks, all in `LotR.Releases`:
1. `channels.json` → `ChannelIndex.Parse` → releaseId (unsigned: only a pointer).
2. `manifest.json` + `.sig` → `ManifestSignature.Verify(bytes, sig, trustedKeys)` **before** parsing anything else;
   then `ManifestCodec.Parse` and `ManifestValidator.Validate` (refuse on any error, including unknown transports).
   Check `manifest.channel` equals the requested channel (REVIEW-AFTER(anti-rollback)).
3. Installed state: `InstalledModuleVerifier.Verify(moduleFolder, module, Full|SizeOnly)` → missing / changed /
   unreadable / extra files ("Verify my install" = Full).
4. `LocalContent` (installed files with known hashes, cached objects, chunks of local files via a kept chunk index
   `AddChunks` or `AddChunksByScanning`) + `DownloadPlanner.Compute(target, installed, installedManifest,
   chunkIndexResolver, local)` → per file Keep / CopyLocal / FromCache / Download / Rebuild (chunked) / NeedsIndex,
   the distinct objects to download with their sizes, and the deletions (files of the previous release only; extra
   files are never deleted). Run it twice when chunk indexes must be fetched first.
5. Each object: `ObjectNames.RelativePath(name)` under `<base><files><releaseId>/`; decode with
   `TransportCodecs.Get(ext)` (`ZstdCodec.OpenDecoded` streams with windowLogMax 27), hash the decoded bytes, compare
   to the name. `RelativePath.ToFileSystem(moduleFolder, path)` resolves paths and refuses any escape.

## 5. What the site reads

`export-site-json` writes `src/data/releases.json` (pretty JSON, atomic):
`current` (releaseId, channel, publishedAt, notes, game, signature {keyId, fingerprint, verified}, size, download,
modules [id, name, version, role, folder, size, files, download, workshopItem], security summary from
`security.json`: VirusTotal files/pending/detections/suspicious/reports, Hybrid Analysis files/pending/verdicts/reports),
`updater` (`files\updater\latest.json` as is, or null), `history` (last 10 releases of the channel: releaseId,
publishedAt, versions by module id). `current` is null until the first publish.

## 6. Measured (15/10/2026, Ryzen 9 7900X 24 threads, NVMe, real LotRMPEvent v1.0.2.2 from the team repo clone)

| Step | Result |
|---|---|
| Module | 863 files, 365.1 MB (382,866,103 bytes), 5 files ≥ 16 MiB chunked, 12 scenes |
| Hash only (`plan`, warm cache) | 0.44 s (≈ 830 MB/s on 8 threads, net8) / 0.49 s (net48) |
| First publish (zstd-19, 8 threads) | 24 s (net8) / 28 s (net48 test) / 26 s (CLI, net48) |
| Stored / fresh-install download | 1,240 objects, 301.8 MB = 82.6 % of raw; 729 `.zst` + 511 `.raw` objects |
| Verify (decode + hash every object) | 1,269 object reads, 0.4 s |
| Second publish after a small change (1 XML edited, 1 file added, 1 PNG deleted, 1 byte flipped in a 26.8 MB WAV) | 1.1 s; **4 new objects, 484,839 bytes written**; 1,236 objects hard-linked (301.1 MB, 0 bytes copied) |
| Update download for a player on the first release | 484,839 bytes in 4 objects (XML, new file, 1 chunk of the WAV, its new chunk index) |
| Updater plan from an installed copy | keep 860 files, download 3 objects (480,659 bytes) + the index, rebuild the WAV reusing 26.3 MB locally, delete 1 file |
| "Verify my install" full re-hash | 863 files, 365.1 MB in 1.0 s |
