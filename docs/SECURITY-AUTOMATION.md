# Security automation (workstream G)

How every release of the LotR MP Event modules and of the updater exe gets scanned, signed and made reproducible,
so a cautious player can check what they run (docs/PLAN.md, transparency principles 8 and 11).

Everything lives in `tools/security/`. Nothing here needs an account to build or test; each service is skipped with
a clear message until its key exists. **No account has been created yet** — section 8 (French) lists what the owner
has to do.

| Piece | Where | State |
|---|---|---|
| VirusTotal scans (API v3) | `tools/security/Lotr.Security` (`lotr-security vt-scan`) | ready, tested with fake responses |
| Hybrid Analysis (Falcon Sandbox API v2) | `lotr-security ha-scan` | ready, tested with fake responses |
| One-shot wrapper for a release | `tools/security/Invoke-ReleaseScans.ps1` | ready |
| Microsoft false-positive submission | section 4 + `Get-FalsePositiveEvidence.ps1` | manual checklist (no public API) |
| Code signing (SignPath / Azure Trusted Signing / test cert) | `tools/security/signing/` | test cert path exercised; the two real providers wait for accounts |
| Reproducible build check | `tools/security/repro/Test-ReproducibleBuild.ps1` | works today on `lotr-detect`; switch to the updater when it exists |

---

## 1. Release pipeline order

```
build updater (deterministic, section 6)
   -> sign updater exe (section 5)            the signed bytes are what players download
   -> publish release (publish tool: manifest + objects + signature)
   -> lotr-security vt-scan  (release exec files + signed updater exe)
   -> lotr-security ha-scan  (signed updater exe only)
   -> security.json next to manifest.json  -> publish tool merges it into manifest.security, site shows it
   -> if anything is flagged: section 4 (false positives) before advertising the release
```

Scans are asynchronous and quota-limited; `docs/CONTRACTS.md` §2 already says `security` may be filled after
publication and never blocks an install.

## 2. API keys and secrets

Keys are **never** in the repository. `lotr-security` looks for them in this order (first hit wins):

1. environment variables `LOTR_VT_APIKEY`, `LOTR_HA_APIKEY`;
2. the secrets file: `--secrets <path>`, else `%LOTR_SECURITY_SECRETS%`, else
   **`%ProgramData%\LotRMP\secrets\security-api.json`** (default, on the server: `C:\ProgramData\LotRMP\secrets\`).

Format (template: `tools/security/templates/security-api.example.json`; placeholders count as "no key"):

```json
{ "virustotal": { "apiKey": "..." }, "hybridAnalysis": { "apiKey": "..." } }
```

Create and lock it down (elevated PowerShell on the server):

```powershell
tools\security\Initialize-SecretsFile.ps1                                   # default path
tools\security\Initialize-SecretsFile.ps1 -ReaderAccount 'NT SERVICE\xyz'   # + the account that runs the scans
notepad C:\ProgramData\LotRMP\secrets\security-api.json                     # paste the keys yourself
tools\security\Lotr.Security\bin\Release\net48\lotr-security.exe check-config   # shows keys masked: abcd********wxyz
```

ACL set by the script: inheritance removed; SYSTEM and Administrators full control; the account running the script
modify; optional reader account read-only. The script refuses a path inside a git working tree, and
`lotr-security` warns if the file is in a git tree or readable by Everyone / Users / Authenticated Users. Keys are
never printed (masked as `abcd********wxyz`) and never written to the state or result files.

The same folder is the right place for the other server secrets (`release-signing.key` lives under
`<root>\data\secrets\` per CONTRACTS §3; `trusted-signing-metadata.json` is not secret but site-specific).

## 3. VirusTotal and Hybrid Analysis: `lotr-security`

### 3.1 Why a net48 console tool (and not only PowerShell)

- Runs on the Windows Server with nothing to install (.NET Framework 4.8 is part of Windows), same toolchain as the
  rest of `LOTR/installer` (SDK-style projects, `Directory.Build.props`).
- The hard parts — quota bookkeeping across runs, a resumable per-file state machine, multipart uploads of up to
  650 MB, retries — are much easier to test in C# with a fake `HttpMessageHandler` and a fake clock (35 tests run in
  about 2 s and never sleep) than in PowerShell 5.1 with the Pester 3.4 that ships with Windows.
- The signing/reproducibility parts are thin wrappers around Windows tools (`signtool`, `Set-AuthenticodeSignature`,
  `dotnet build`), so they are PowerShell 5.1 scripts.

Build: `dotnet build tools\security\Lotr.Security -c Release` → `tools\security\Lotr.Security\bin\Release\net48\lotr-security.exe`
(copy the whole `net48` folder to the server, e.g. `C:\lotrmp\tools\lotr-security\`).

### 3.2 Commands

```text
lotr-security vt-scan  [--manifest <release>\manifest.json] [--file <path>]... [--out <security.json>]
                       [--objects-dir <dir>] [--source-modules <Modules dir>] [--rescan-days N]
                       [--max-wait-minutes 20] [--per-minute 4] [--per-day 500]
lotr-security ha-scan  --file <LotRMPUpdater.exe> --out <security.json> [--sandbox] [--environment-id 160]
lotr-security check-config
lotr-security manifest-block --in <security.json>
common: [--secrets <file>] [--state-dir <dir>] [--require-keys] [--dry-run]
```

Typical server use (or `Invoke-ReleaseScans.ps1 -ReleaseDir ... -UpdaterExe ...`, which runs all three and prints
the manifest block):

```powershell
$rel = 'C:\www\lotrmp\files\releases\20261015-140000-a1b2c3d4'
lotr-security vt-scan --manifest $rel\manifest.json
lotr-security vt-scan --file C:\www\lotrmp\files\updater\LotRMPUpdater-0.1.0.exe --out $rel\security.json
lotr-security ha-scan --file C:\www\lotrmp\files\updater\LotRMPUpdater-0.1.0.exe --out $rel\security.json
lotr-security manifest-block --in $rel\security.json
```

Exit codes: `0` done (or service skipped: no key), `1` some file failed (others are done), `2` usage / missing key
with `--require-keys`, `3` **incomplete — run the same command again later** (daily quota spent or an analysis still
running; progress is saved), `4` key rejected (401/403) or bad input.

### 3.3 VirusTotal flow (API v3, header `x-apikey`)

For each **distinct** SHA-256 among the `exec: true` files of the manifest (a DLL shared by two modules is scanned
once) plus every `--file`:

1. `GET /api/v3/files/{sha256}` — if VirusTotal already knows the file, its last analysis is used: **no upload**
   (with `--rescan-days N`, an analysis older than N days triggers `POST /files/{sha256}/analyse` instead; a file
   known but never analysed gets the same treatment).
2. Unknown file → upload, only if a local copy with the right hash exists:
   - local copy = `<release>\objects\<sha[0:2]>\<sha>.raw` (default `--objects-dir`), else
     `<--source-modules>\<folder>\<path>`; the hash is re-checked before upload, a mismatch is never uploaded;
   - ≤ 32 MB: `POST /api/v3/files` (multipart, field `file`, the player-facing file name, not the object name);
   - 32–650 MB: `GET /api/v3/files/upload_url`, then `POST` to that one-time URL;
   - > 650 MB: skipped with a note (upload by hand on the website if ever needed — none of our DLLs is close).
3. `GET /api/v3/analyses/{id}` every 30 s until `completed`. After `--max-wait-minutes` (20) the run stops with
   exit 3; the next run polls the same analysis id instead of re-uploading.

Quotas: the public (free) API allows **4 requests/minute and 500/day** (also a monthly cap). Every HTTP call counts.
`lotr-security` keeps a sliding one-minute window and a per-UTC-day counter in
`%ProgramData%\LotRMP\security\virustotal-quota.json` (shared by all runs with that key) and waits instead of
overspending. A `429` backs off 60 s (or `Retry-After`); three in a row = the server says the day is spent →
exit 3, resume after 00:00 UTC. `5xx` and network errors are retried 3 times (5 s, 15 s, 45 s), then only that
file is marked `error` and the others continue. `401/403` stops the run.

Cost of a typical release: one lookup per distinct exec file (a handful of DLLs + the updater); only new, unknown
files cost an upload plus a few polls. Re-publishing unchanged DLLs costs one lookup each.

State: per output file, `%ProgramData%\LotRMP\security\jobs\<service>-<hash>.json` (never in the public release
folder). Change the folder with `--state-dir` or `LOTR_SECURITY_STATE_DIR`.

**Note on privacy/terms.** Files uploaded with the free API are shared with the security community, and the public
API is for non-commercial use. Our files are public downloads of a free mod, so both are fine — but never upload
anything private (e.g. server-only admin modules) with this tool.

### 3.4 Hybrid Analysis flow (Falcon Sandbox API v2, headers `api-key` + `User-Agent: Falcon Sandbox`)

Updater exe only (`ha-scan` refuses `--manifest`):

1. `GET /api/v2/search/hash?hash=<sha256>` — an existing finished public report with a verdict is reused.
2. Otherwise `POST /api/v2/quick-scan/file` (`scan_type=all`), then `GET /api/v2/quick-scan/{id}` until `finished`;
   the verdict is derived from the scanners (`malicious` / `suspicious` / `no specific threat`), `source: quick-scan`.
3. With `--sandbox`: `POST /api/v2/submit/file` (`environment_id`, default 160 = Windows 10 64-bit), then
   `GET /report/{job}/state` until `SUCCESS`, then `GET /report/{job}/summary` → `verdict`, `threat_score`.

Report link: `https://www.hybrid-analysis.com/sample/<sha256>` (plus `/<job id>` for a sandbox run).
Default client limit: 5 requests/min, 200/day (`--per-minute`, `--per-day`); same 429/5xx/auth handling as above.
The endpoint paths follow the public v2 documentation; they must be confirmed once with the first real key
(review-after "ha-api"). A brand-new Hybrid Analysis key can be restricted for submissions until the account is
vetted: a `403` on submit with a key that works for search means exactly that.

### 3.5 `security.json` (sidecar, one per release folder)

Written next to `manifest.json` (or wherever `--out` says), updated after every step, upserted by SHA-256 so the
release files and the updater can be scanned by separate runs into the same file:

```json
{
  "schema": 1,
  "releaseId": "20261015-140000-a1b2c3d4",
  "updatedAt": "2026-10-15T14:20:00Z",
  "virustotal": [
    { "sha256": "…", "url": "https://www.virustotal.com/gui/file/…", "detections": 0, "engines": 71,
      "scannedAt": "2026-10-15T14:12:31Z", "suspicious": 0, "flaggedBy": [],
      "files": ["LotRMPEvent/bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll"], "status": "done" }
  ],
  "hybridAnalysis": [
    { "sha256": "…", "url": "https://www.hybrid-analysis.com/sample/…", "verdict": "no specific threat",
      "source": "quick-scan", "scannedAt": "…", "files": ["LotRMPUpdater-0.1.0.exe"], "status": "done" }
  ]
}
```

- `detections` = engines saying *malicious*; `suspicious` separately; `engines` = engines that gave a verdict
  (malicious + suspicious + undetected + harmless; "type-unsupported", timeouts and failures excluded).
- `flaggedBy` lists engine, category and detection name — what you need for section 4.
- `status`: `done | pending | skipped | error` (+ `note`). Only `done` entries go into the manifest.

**Merge into the manifest** (REVIEW-AFTER(publish-cli)): `lotr-security manifest-block --in security.json` prints
exactly the `security` object of CONTRACTS §2 (contract fields only, finished results only). Because the manifest
is signed (CONTRACTS §3), merging it **changes the signed bytes**: the publish tool must re-sign `manifest.json`
after the merge — or the contract keeps `security` out of the signed manifest and lets the site/updater read
`security.json` directly. That decision belongs to the coordinator (ledger, "manifest").

### 3.6 Resuming automatically (optional, server)

Exit 3 just means "come back later". On the server a scheduled task can re-run the same command every hour until
it returns 0 (left to the owner; nothing is registered by these scripts):

```bat
rem cmd.exe syntax (^ = line continuation)
schtasks /Create /TN "LotRMP\resume-scans" /SC HOURLY /RU SYSTEM /TR ^
  "powershell -NoProfile -File C:\lotrmp\tools\security\Invoke-ReleaseScans.ps1 -ReleaseDir C:\www\lotrmp\files\releases\<id>"
```

(Players' updater never runs scans and never contacts these services — PLAN principle 6.)

## 4. False positives (Microsoft and other vendors)

Unsigned, new, network-using exe files that write into a game folder are classic heuristic false-positive bait.
When a scan shows detections, or players report Windows Defender / SmartScreen blocks:

### 4.1 Microsoft Defender — manual submission (no public API for free submissions)

1. Collect the evidence (read-only):
   `tools\security\Get-FalsePositiveEvidence.ps1 -File <exe or dll> -DownloadUrl https://<domain>/files/...`
   It prints file name, size, SHA-256, signer, product/version, local Defender engine and definition versions, the
   detection name if Defender flagged it on that PC, the VirusTotal/Hybrid Analysis links and a suggested comment.
2. Open **https://www.microsoft.com/en-us/wdsi/filesubmission** and choose **"Software developer"** (requires signing
   in with a free Microsoft account; developer submissions are prioritised and tracked).
3. Product: **Microsoft Defender Antivirus (Windows 10/11)** for an AV detection, or **Microsoft Defender SmartScreen**
   for an "unrecognised app" / reputation block of the download.
4. Upload the exact file players get (the signed exe, or the module DLL). Zip it with password `infected` if the
   browser/AV blocks the upload.
5. "What do you believe this file is?" → **Incorrectly detected as malware/malicious** (or "should not be blocked").
6. Detection name: from Windows Security → *Protection history*, or the evidence block. Definition version: from
   the evidence block (`Get-MpComputerStatus`).
7. Comment: paste the suggested text and the link to `/security/` on our site (hashes, signature, source).
8. Keep the **submission ID**; answers usually take from hours to a few days. Once Microsoft clears it, players can
   fetch the new definitions immediately: `"%ProgramFiles%\Windows Defender\MpCmdRun.exe" -SignatureUpdate`.
   Write the outcome in the release notes / Discord announcement.

### 4.2 Other engines on VirusTotal

`flaggedBy` gives the engine names. VirusTotal keeps an up-to-date list of vendor false-positive contacts:
https://docs.virustotal.com/docs/false-positive-contacts — send the SHA-256, the VirusTotal link and the same
comment. After a vendor fixes it, re-run `lotr-security vt-scan --rescan-days 0 ...` to refresh the result.

### 4.3 What reduces false positives in the first place

Authenticode signature (section 5), stable publisher name across versions, version info resources filled in,
no packers/obfuscators, no self-extracting archives, reproducible build + public source, and the same exe hash
for a while (SmartScreen reputation is built per file and per certificate; since 2024 EV certificates no longer
give instant SmartScreen reputation either, so reputation always takes some downloads).

## 5. Code signing of the updater exe

`tools/security/signing/Sign-Updater.ps1 -File <exe> -Provider TestCert|AzureTrustedSigning|SignPath [-Release]`
signs, timestamps (RFC 3161, SHA-256) and verifies in one go; `Test-Signature.ps1` is the release gate
(Valid + timestamped + not the test certificate, optional `-ExpectedSubject`, `-Json` for the site).
`-WriteSha256` writes `<exe>.sha256` (`<hex>  <name>`) **after** signing, since signing changes the hash.
`signtool.exe` is found via `-SignToolPath`, `%SIGNTOOL%`, PATH, the Windows SDK, or the NuGet package
`Microsoft.Windows.SDK.BuildTools`; it is required for Trusted Signing only.

### 5.1 SignPath Foundation (free for open source)

What it is: SignPath signs with a certificate issued to **"SignPath Foundation"** (that is the publisher name
Windows shows, not ours) for approved open-source projects, at no cost.

Prerequisites (check the current terms at https://signpath.org before applying):
- the updater source in a **public GitHub repository** under an **OSI-approved licence**, no proprietary
  components in what gets signed, no malware/PUP behaviour (our transparency principles already match);
- the signed binary is **built by CI from that source** — GitHub Actions — and uploaded as a workflow artifact;
  SignPath verifies the origin (repository, workflow, branch/tag) before signing (*origin verification*);
- the project is maintained, has already published releases, and documents a **code signing policy** on its website
  (who can approve releases, privacy statement); team members use MFA on GitHub and SignPath;
- after approval: a SignPath organization, a project (`LotRMPUpdater`), an *artifact configuration* telling it to
  sign the exe, a `test-signing` and a `release-signing` policy, and a CI user API token.

Template: `tools/security/templates/signpath-github-workflow.yml` (**REVIEW-AFTER(open-source)**, inactive: the
repository is local-only today). Locally, `Sign-Updater.ps1 -Provider SignPath` can only use the **test-signing**
policy via the SignPath PowerShell module (`Install-Module SignPath`, env `SIGNPATH_API_TOKEN`,
`SIGNPATH_ORGANIZATION_ID`); `-Release` is refused because release signing must come from the workflow.

Fit for us: only if the owner agrees to publish the **updater** source (not the mod) publicly. Cost: 0.

### 5.2 Azure Trusted Signing (Microsoft, paid)

What it is: Microsoft-managed code signing; certificates are issued to your *validated identity* and live 3 days,
so **timestamping is mandatory** (`http://timestamp.acs.microsoft.com`). The product has been renamed during and
after its preview — in the Azure portal look for "Trusted Signing" (or "Artifact Signing").

Prerequisites:
- an Azure subscription (pay-as-you-go, card required) and the `Microsoft.CodeSigning` resource provider;
- a Trusted Signing **account** (pick a region: its endpoint goes into `metadata.json`, e.g.
  `https://weu.codesigning.azure.net` for West Europe);
- **identity validation** for *Public Trust*. Eligibility is the blocking point: at the time of writing Microsoft
  accepts **organisations** (with a verifiable history, ~3 years, in supported countries including the EU) and
  **individual developers only in the USA/Canada**. A French individual is therefore probably **not** eligible on
  their own; a registered entity (e.g. an association with a SIREN and enough history) may be. Check the current
  rules before paying;
- a **certificate profile** (Public Trust) and the role **"Trusted Signing Certificate Profile Signer"** for the
  identity that signs (your user via `az login`, or an app registration: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`,
  `AZURE_CLIENT_SECRET`, picked up by DefaultAzureCredential);
- tools on the signing machine: `signtool.exe` (Windows SDK 10.0.22621.755 or later), the **.NET 8 runtime**, and
  `Azure.CodeSigning.Dlib.dll` from the NuGet package **Microsoft.Trusted.Signing.Client** (`bin\x64\`).

Cost (list prices at the time of writing): **Basic ≈ 9.99 USD/month** (5,000 signatures), Premium ≈ 99.99 USD/month.
Basic is plenty for us.

Usage:
```powershell
copy tools\security\templates\trusted-signing-metadata.json C:\ProgramData\LotRMP\secrets\   # then fill it in
az login
tools\security\signing\Sign-Updater.ps1 -File LotRMPUpdater.exe -Provider AzureTrustedSigning -Release -WriteSha256 `
   -DlibPath C:\tools\trusted-signing\bin\x64\Azure.CodeSigning.Dlib.dll `
   -MetadataPath C:\ProgramData\LotRMP\secrets\trusted-signing-metadata.json
# = signtool sign /v /debug /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib <dlib> /dmdf <metadata.json> <exe>
```

### 5.3 Local test certificate (development only — NOT FOR RELEASE)

```powershell
tools\security\signing\New-TestSigningCertificate.ps1          # -> %LOCALAPPDATA%\LotRMP\test-signing\lotr-test-signing.pfx
tools\security\signing\Sign-Updater.ps1 -File <copy of the exe> -Provider TestCert
tools\security\signing\Test-Signature.ps1 -File <exe> -AllowTestCert
```

Self-signed, subject `CN=LotR MP Event TEST SIGNING - NOT FOR RELEASE`, created in memory and written to a PFX
outside the repo with a random password; it is **not** imported into any certificate store and never into
*Trusted Root*, so nothing system-wide changes and Windows reports the signature as `UnknownError` (untrusted root) —
expected. Its only purpose is to exercise the pipeline (sign → timestamp → verify → tamper detection).
`Sign-Updater.ps1 -Release` refuses it, `Test-Signature.ps1` without `-AllowTestCert` rejects it.

### 5.4 Verification and timestamping

- `Test-Signature.ps1 -File x.exe [-ExpectedSubject 'SignPath Foundation']` → Valid? timestamped? test cert? signer,
  thumbprint, timestamp authority; also runs `signtool verify /pa /v` when signtool exists. Exit 1 if not acceptable.
- A modified signed file shows `HashMismatch` (checked).
- Timestamp servers: Trusted Signing must use `http://timestamp.acs.microsoft.com`; others default to
  `http://timestamp.digicert.com` (`-TimestampUrl` overrides).
- Players can check by themselves: right-click → Properties → Digital Signatures, or
  `Get-AuthenticodeSignature .\LotRMPUpdater.exe` — the site's `/security/` page should say so.

## 6. Reproducible build

Goal (PLAN principle 11): anyone with the source can rebuild the updater and get **the same SHA-256** as the
unsigned build we published.

Rules for the updater project (`templates/Reproducible.props`, REVIEW-AFTER(updater)):
- `Deterministic=true` (already in `Directory.Build.props`): no timestamp or random MVID in the PE.
- `ContinuousIntegrationBuild=true` + `PathMap=<repo root>\=/_/`: no absolute build path in the PE/PDB.
- `IncludeSourceRevisionInInformationalVersion=false`: today the SDK appends the git commit hash to
  `AssemblyInformationalVersion` (seen: `0.1.0+f4162577…`), so a build from a source zip would differ.
- **Lock the SDK**: `global.json` with the exact SDK version and `"rollForward": "disable"`
  (`templates/global.json.example`, currently 8.0.422).
- **Lock NuGet**: `RestorePackagesWithLockFile=true`, commit every `packages.lock.json`, restore with
  `--locked-mode` in CI; pin every `PackageReference` to an exact version (no floating `*`).
- Same build command everywhere: `dotnet build -c Release -p:ContinuousIntegrationBuild=true`.
- No post-build packers/obfuscators; if the updater is ever merged into a single exe (Costura/ILMerge), that step
  must be deterministic too and is included in the check.
- Signing is the only allowed difference: compare **unsigned** outputs, or strip the signature before comparing
  (`signtool remove /s file.exe` — Windows SDK — then hash).

Check: `tools/security/repro/Test-ReproducibleBuild.ps1 [-Project <csproj>]` copies the source into **two different
folders**, builds both, and compares SHA-256 of every exe/dll (PDBs reported too). Default project is
`src/LotR.Detect.Cli` until the updater exists (REVIEW-AFTER(updater)). Result on 15/10/2026 with SDK 8.0.422:
`lotr-detect.exe` and `LotR.GameDetection.dll` (and even their PDBs) byte-identical → REPRODUCIBLE.

## 7. Tests

```powershell
dotnet test tools\security\Lotr.Security.Tests
```

35 tests, net48, no network, no real sleep (fake clock), fake HTTP server with responses shaped after the public
API docs (`Lotr.Security.Tests/Fixtures`, not recorded from the live services since no key exists yet). Covered:
no key → skip (and `--require-keys` → exit 2), placeholder keys, known hash (no upload, duplicate hashes once,
non-exec ignored, key header sent, key never logged), flagged engines, unknown small file (direct multipart upload
with the real file name, polling), > 32 MB file through `upload_url` (real 33 MB file), above-limit skip, missing
local copy then resume with `--source-modules`, hash-mismatch never uploaded, known-but-unanalysed reanalysis,
4 requests/minute sliding window, quota shared across runs, daily quota stop + next-day resume, 429 back-off,
persistent 429 = day spent, 401 abort, 5xx retries (5/15/45 s) then per-file error and retry next run, network
error retry, long analysis left pending then collected without re-upload, updater + release in one sidecar,
manifest block = contract fields only; Hybrid Analysis: skip, existing report (both answer shapes), quick scan,
malicious verdict, sandbox submit/state/summary, 403, 429, 400 error; secrets: env vs file precedence,
`LOTR_SECURITY_SECRETS`, example file has no usable key, git-tree warning, masking.

Scripts exercised by hand on 15/10/2026: `Initialize-SecretsFile.ps1` (scratch path; ACL result checked; refusal
inside the repo), the test-certificate chain (create → sign with DigiCert timestamp → `Test-Signature` rejects it
as release, accepts with `-AllowTestCert` → tampered copy = `HashMismatch`), `Test-ReproducibleBuild.ps1`,
`Invoke-ReleaseScans.ps1` and `Get-FalsePositiveEvidence.ps1` without keys.

---

## 8. Résumé pour le propriétaire (français)

**Ce qui est prêt.** L'outil `lotr-security` scanne automatiquement chaque release (VirusTotal pour toutes les
DLL/EXE, Hybrid Analysis pour l'updater), respecte les quotas gratuits, reprend tout seul là où il s'est arrêté, et
écrit `security.json` que l'outil de publication et le site utiliseront. Sans clé, il **saute proprement** avec un
message — rien ne casse. La signature de code et la build reproductible sont prêtes côté scripts ; seule la
signature de test (sans valeur pour les joueurs) fonctionne aujourd'hui.

**À faire, dans l'ordre :**

1. **VirusTotal** (gratuit) : créer un compte communautaire sur https://www.virustotal.com (bouton *Sign up*),
   confirmer l'e-mail, puis menu du profil → **API key** → copier la clé. Limites : 4 requêtes/min, 500/jour —
   largement suffisant (l'outil les respecte tout seul). Usage non commercial uniquement : c'est notre cas.
2. **Hybrid Analysis** (gratuit) : créer un compte sur https://www.hybrid-analysis.com (*Sign up*), puis
   *Profile* → **API key** → *Create key*. Si la soumission renvoie 403 alors que la recherche marche, demander le
   statut « vetted » depuis le profil (l'outil se contente alors des rapports existants).
3. **Ranger les clés sur le serveur** (PowerShell en administrateur) :
   `tools\security\Initialize-SecretsFile.ps1`, puis ouvrir `C:\ProgramData\LotRMP\secrets\security-api.json`,
   remplacer les deux `<PUT-YOUR-…>` par les clés, enregistrer, et vérifier avec `lotr-security check-config`
   (les clés s'affichent masquées). **Jamais** dans le dépôt, jamais dans Discord.
4. **Signature de l'updater** — choisir une voie :
   - **SignPath Foundation (gratuit)** si on accepte de publier le **code source de l'updater** (pas du mod) sur
     GitHub en public sous licence open source (MIT par ex.), compilé par GitHub Actions. L'éditeur affiché par
     Windows sera « SignPath Foundation ». Demande sur https://signpath.org (dossier examiné à la main), puis activer
     le modèle `tools/security/templates/signpath-github-workflow.yml`.
   - **Azure Trusted Signing (~9,99 $/mois)** : abonnement Azure + validation d'identité. **Attention** : à ce jour
     les particuliers ne sont acceptés qu'aux États-Unis/Canada ; en France il faut une **structure** (association
     ou société avec un historique vérifiable). À vérifier avant de payer.
   - Sinon : pas de signature au début, mais scans + empreintes + build reproductible affichés sur `/security/`.
     SmartScreen affichera « éditeur inconnu » les premières semaines ; c'est à expliquer sur la page d'installation.
5. **Faux positif** (si un antivirus bloque l'updater) : lancer `Get-FalsePositiveEvidence.ps1 -File <exe>`, puis
   soumettre le fichier sur https://www.microsoft.com/en-us/wdsi/filesubmission (compte Microsoft, choix
   « Software developer », « Incorrectly detected as malware ») en collant le bloc de texte. Garder le numéro de
   soumission. Pour les autres antivirus : liste des contacts sur
   https://docs.virustotal.com/docs/false-positive-contacts.

**Coûts :** VirusTotal 0 €, Hybrid Analysis 0 €, Microsoft 0 €, SignPath 0 € (si open source), Azure Trusted Signing
≈ 10 $/mois (si éligible). Aucun compte n'a été créé et aucune vraie API n'a été appelée pendant ce travail.

## 9. Open points

See the team's internal review notes (not published).
