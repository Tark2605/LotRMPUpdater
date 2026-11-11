# Team signing: the release key lives on our PCs, not on the server

Since 2026-11-11. Everything players install through the launcher is signed with ONE key (ECDSA P-256, the
"release key"): the mod releases (`manifest.json.sig`) and the launcher itself (`latest.json.sig`). The launcher
embeds the public part (`src/Lotr.Updater.Core/Security/Keys/release-signing.pub.pem`) and refuses anything else.

Until now the private key sat on the web server. If the server were hacked, the attacker could sign a fake mod or a
fake launcher and every player would install it. Now:

- the private key lives on a team member's PC, encrypted for that Windows account only (DPAPI user scope);
- the server keeps only the **public** key: it can check signatures, it can never make one;
- the PC never signs blindly: it checks the release against its own copy of the modules first.

Same key, same `keyId`: no new launcher, nothing changes for players.

## How a release goes out

```
Server (admin panel)                         Team PC (TEAM-PUBLISH.cmd)                 Server (automatic)
Modules > Dry run > "Prepare the release"
  lotr-publish publish --pending
  files\releases\<id>\  manifest + objects,
  verified, NOT signed; channel unchanged;
  files\releases\pending.json names it  ---> sign-release: reads pending.json and the
                                              manifest over https, checks:
                                              - the live release carries OUR signature
                                              - nothing but the domain may differ in the
                                                endpoints; newer than live; canonical
                                              - every CHANGED module: each file's SHA-256
                                                equals the PC's own copy (team repo clone,
                                                game Modules folder); unchanged modules
                                                must be byte-identical to the signed live one
                                              shows the summary, asks, signs
                                             publish-updater (when dist\updater has a newer
                                              launcher): Defender scan, signs latest.json
                                             Build-DeployPackage -SignedDir: package + signed\
                                             site-update.mjs send (signed mailbox)  -------> Update-Site.ps1 (auto-install
                                                                                              or "Install now"):
                                                                                              package signature checked,
                                                                                              Deploy-Site -CodeOnly,
                                                                                              lotr-publish apply-signed:
                                                                                              every signature checked with
                                                                                              the PUBLIC key, whole release
                                                                                              re-verified, channel switched,
                                                                                              launcher published (never a
                                                                                              downgrade), site rebuilt
```

Players keep the live release until the signature arrives. A refused check signs nothing and sends nothing.

## Every time (team member)

1. Admin panel > **Modules**: pick the folders, **Dry run**, then **Prepare the release** (the button says so when
   the server has no private key).
2. On your PC: update your copy of the modules (team repo clone: `git pull`), then double-click **`TEAM-PUBLISH.cmd`**
   (in `LOTR\`, or `site\scripts\`). Read the summary, answer `y`.
3. Nothing else. With "Install automatically" ON (Admin panel > Updates) the server installs it within 2 minutes;
   otherwise click **Install now** there.

A new launcher: put the built `LotRMPUpdater-<version>.exe` in `LOTR\dist\updater\` and run `TEAM-PUBLISH.cmd`; it
publishes it only if it is newer than the one the site serves. Same for website-only changes: `TEAM-PUBLISH.cmd`
sends the package even when there is nothing to sign (it asks first).

Options (`site\scripts\Team-Publish.ps1`): `-DryRun` (everything but the upload), `-Notes "..."`, `-Yes`,
`-SkipBuild`, `-NoUpdater`. Log of each run: `site\.build-deploy\team-publish\<stamp>.log`.

## Settings of the PC

`%USERPROFILE%\.lotrmp\team-publish.json`, created on the first run:

| Key | Meaning |
|---|---|
| `site` | `https://middleearthevent.com` |
| `moduleRoots` | folders holding module folders by name (`LotRMPEvent`, `LotRMPCore`...): by default the team repo clone and the game's `Modules` folder. Every candidate is tried; the first that matches wins. |
| `modules` | explicit `{ "LotRMPCore": "D:\\...\\LotRMPCore" }`, wins over `moduleRoots` |
| `updaterDist` | where the built launcher exes are (default `LOTR\dist\updater`) |

Files on the PC: `%USERPROFILE%\.lotrmp\release-signing.key` (release key) and
`%USERPROFILE%\.lotrmp\site-update-signing.key.pem` (site mailbox key, site/docs/SITE-UPDATES.md).

## Move the key to your PC (once, by the key owner)

Nobody but the owner handles the key. No USB stick needed: the file goes through the Remote Desktop session, which
is encrypted. On the **server**, elevated PowerShell inside Remote Desktop:

```powershell
& 'C:\www\lotrmp\tools\lotr-publish\lotr-publish.exe' export-key --root 'C:\www\lotrmp' --out "$env:USERPROFILE\Desktop\release-signing.plain.key" --protection plain --grant current --force
```

`--grant current` matters: without it only an elevated process can read the file, and the Remote Desktop
copy-paste (never elevated) is refused. Since 2026-11-11 `export-key --protection plain` adds it by default.

Copy the file from the server's desktop and paste it on your PC's desktop (Remote Desktop file copy-paste). If that is
refused, copy the text instead: on the server `Get-Content "$env:USERPROFILE\Desktop\release-signing.plain.key" -Raw |
Set-Clipboard`, on the PC `Get-Clipboard -Raw | Set-Content "$env:USERPROFILE\Desktop\release-signing.plain.key"
-Encoding ascii`. Then delete it on the server: `Remove-Item "$env:USERPROFILE\Desktop\release-signing.plain.key"`.

On **your PC** (normal PowerShell, your own account), from the `LOTR\installer` folder after
`dotnet build tools\LotR.Publish -c Release`:

```powershell
$lp = '.\tools\LotR.Publish\bin\Release\net48\lotr-publish.exe'
& $lp export-key --key "$env:USERPROFILE\Desktop\release-signing.plain.key" --protection dpapi-user
& $lp show-key --key "$env:USERPROFILE\.lotrmp\release-signing.key"
& $lp show-key --pub .\src\Lotr.Updater.Core\Security\Keys\release-signing.pub.pem
```

The two `keyId` lines must be identical (`export-key` also reads the copy back). Then:

1. Backup: the plain file is a 5-line text. Paste its content into a password manager (secure note "LotR release
   key") or print it and keep the sheet at home. Never Discord, mail or Google Drive. Then delete the file and empty
   the recycle bin.
2. On the server, delete `C:\www\lotrmp\data\secrets\release-signing.key` (keep `release-signing.pub.pem`!).
   The Modules screen then shows "Signing key: on the team's PCs".
3. Run `TEAM-PUBLISH.cmd -DryRun` once: it must say "Release key ... (the key the launcher trusts)".

A second team member gets the same key the same way (from the offline backup), on their own account.

`dpapi-user` means: only this Windows account on this PC can decrypt the file. A copied file, another account, a
service or an administrator cannot. Protect the Windows account (password, BitLocker on a laptop).

## What each side refuses

| Check | Where | Result |
|---|---|---|
| live release not signed by the team key | PC | nothing signed ("something is wrong on the server") |
| a changed module file differs from (or is missing in) every local copy | PC | refused, the file is named |
| endpoints changed (other domain, other download/queue/support path) | PC | refused |
| release older than the live one, wrong channel, non-canonical manifest, unknown fields | PC | refused |
| launcher not newer than the live one, Defender threat, version mismatch inside/outside the exe | PC | not published |
| signature by another key, altered manifest or latest.json | server | not installed, logged |
| an object damaged on the server after signing | server | signature removed again, channel unchanged |
| exe not matching the signed latest.json, or an older version | server | not installed (`--force` only by hand) |
| signature of a release older than the live one | server | signed, channel NOT moved (a rollback stays a deliberate `set-channel`) |

## Commands (lotr-publish)

| Command | Where | Does |
|---|---|---|
| `publish ... --pending` | server | release without signature, `pending.json` |
| `pending --root R` | server | what waits for the signature |
| `sign-release --from https://domain --out <dir> [--modules-root <dir>]... [--module F=<dir>]... [--base-url ..] [--yes]` | PC | review + sign (exit 0 also when nothing is pending, 1 refused, 3 declined) |
| `publish-updater --exe <exe> --updater-dir <dir>\updater --base-url https://domain` | PC | signed launcher files (the PC key by default) |
| `apply-signed --from <dir> --root R` | server | installs `updater\` and `releases\<id>\manifest.json.sig` after checking them |
| `export-key ... --protection dpapi-user` | PC | the key for this account only, read back after writing |

`set-channel` (rollback) still works on the server without the private key: it only checks signatures.

## Lost or stolen PC

- Lost PC, key backup exists: restore it on a new PC (section above). Nothing changes for players.
- Key stolen (or backup lost too): a new key needs a new launcher build with the new public key in
  `Security/Keys/`, signed with the OLD key so installed launchers accept it, then the old key is destroyed. Plan it
  with the whole team; until then keep "Install automatically" OFF on the server.
