# lotr-updater: the LotR MP Event updater for Linux and Steam Deck

`lotr-updater` installs and updates the LotR MP Event mod (LotRMPEvent + LotRMPCore) for Bannerlord on Linux and on the
Steam Deck. It is the same engine as the Windows `LotRMPUpdater.exe`: same server, same signature check, same
download queue, same backups. It runs in a terminal, and it is one program file: nothing else to install.

Bannerlord itself stays the Windows game run by Steam through Proton. The updater only puts the mod files into the
game's `Modules` folder; the game finds them there exactly as on Windows.

---

## For players

### What you need

- Bannerlord installed with Steam and started once (Proton). Native Steam, Flatpak Steam and the Steam Deck all work.
- A 64-bit PC (x86-64). Any current distribution: SteamOS 3, Ubuntu, Fedora, Arch, Mint, Bazzite...
- Nothing else: no .NET, no Wine, no `sudo`. **Never run the updater with `sudo`.**

### Download

On the site's **Install** page, choose **Linux** (the Steam Deck is Linux too) and download
`lotr-updater-<version>-linux-x64.tar.gz` (about 7 MB). The page shows its SHA-256; to check the download, compare it
with:

```sh
sha256sum lotr-updater-<version>-linux-x64.tar.gz
```

(or download the `.sha256` file at the same address + `.sha256` and run `sha256sum -c lotr-updater-<version>-linux-x64.tar.gz.sha256`).

### Steam Deck (desktop mode)

1. Press the **Steam** button > **Power** > **Switch to Desktop**.
2. Open the site in the browser and download the `.tar.gz` file. It goes to your `Downloads` folder.
3. Open **Dolphin** (the file manager), go to `Downloads`, right-click the file > **Extract** > **Extract archive here**.
4. Open the new `lotr-updater-<version>` folder, right-click an empty spot > **Open Terminal Here**.
5. Type `./lotr-updater` and press Enter. A small menu appears:

   ```
     1) Status: is an update available?
     2) Install or update the mod
     3) Verify my install
     4) Play (starts Bannerlord through Steam)
     5) Send a support log
     6) Quit
   ```

6. Type **2**. The updater shows what it will do (files to add, replace or remove, download size) and asks
   `Install these changes? [y/N]`. Type **y**.
7. When it says `Done`, start Bannerlord, open **Multiplayer** in the launcher and tick the modules the updater names
   (`LotRMPEvent`, `LotRMPCore`).
8. Back to Game Mode: the **Return to Gaming Mode** icon on the desktop.

Optional: `./lotr-updater shortcut` adds **LotR MP Event updater** to the application menu, so next time you do not need
the terminal step. If you later move the folder, run `./lotr-updater shortcut` again from the new place.

### Other Linux desktops

```sh
tar xzf lotr-updater-<version>-linux-x64.tar.gz
cd lotr-updater-<version>
./lotr-updater            # the menu
./lotr-updater install    # or straight to the install / update
```

If you get `Permission denied`, the program lost its "executable" mark during the extraction (some archive tools drop
it). Give it back once: `chmod +x lotr-updater`.

### Updating lotr-updater itself

`lotr-updater` keeps the mod up to date, but it does not replace itself. When a newer version of the program is out,
`status` and `install` say so at the end:

```
A new version of lotr-updater is available: 0.2.0 (you have 0.1.0).
Get it from https://<site>/install/ (Linux / Steam Deck), extract it and run ./lotr-updater from the new folder
(then ./lotr-updater shortcut again if you use the menu entry). Your settings and the installed mod stay as they are.
```

Download and extract the new archive like the first time; the old folder can then be deleted. Your settings, cache and
logs live in `~/.local/share/LotRMPUpdater/`, not next to the program, so nothing is lost. The old version keeps
working meanwhile. (It only believes a notice signed with the LotR team key, the same check as for the mod files.)

### Commands

| Command | What it does |
|---|---|
| `./lotr-updater` | the menu (in a terminal) |
| `./lotr-updater status` | is an update available? Downloads nothing, changes nothing |
| `./lotr-updater install` | shows the plan, asks, then installs or updates (`update` works too) |
| `./lotr-updater verify` | re-reads every installed file; `--repair` puts back what differs |
| `./lotr-updater play` | starts Bannerlord through Steam (warns first if an update is waiting) |
| `./lotr-updater detect` | shows where Bannerlord is (Steam library, Proton prefix, Workshop) |
| `./lotr-updater support` | shows the support log, then sends it if you agree: you get a code for your Discord ticket |
| `./lotr-updater shortcut` | adds the updater to the application menu |
| `./lotr-updater --help` | every option |

Useful options: `--game <folder>` (the game folder, if it was not found; remembered), `--yes` (do not ask),
`--list` (every file of the plan), `--plain` (simple progress lines), `--connections N` (1 to 8 parallel downloads).

Exit codes, for scripts: 0 ok, 1 game not found, 2 wrong command line, 3 server or network problem, 4 release refused
(bad signature or older release), 5 install failed, 6 verify found differences, 7 game running, 8 downloads paused by
the server, 9 another updater is running, 10 update available (`status`), 130 cancelled.

### What it touches, and what it never touches

- It writes only inside the game's `Modules` folder, in the mod's own module folders (`Modules/LotRMPEvent`,
  `Modules/LotRMPCore`). Your own files in those folders are kept. Every replaced or removed file is saved first.
- Its own data (cache, logs, backups, settings) is in `~/.local/share/LotRMPUpdater/`.
- It never changes Steam, Proton, the Proton prefix, other mods or anything outside those folders, and it never needs
  administrator rights.
- `Ctrl+C` during an install stops safely: anything already changed is put back.

### Troubleshooting

**"Bannerlord was not found"**. The updater looks in every Steam library it knows (native Steam, Flatpak Steam,
`~/.steam`, SD cards and drives under `/run/media` and `/media`). If your game is somewhere else, point to it once:
`./lotr-updater install --game "/path/to/Mount & Blade II Bannerlord"` (the folder that holds `bin` and `Modules`).
`./lotr-updater detect --verbose` shows where it looked.

**"This app cannot write in ..."**. The game folder must belong to your user. Usual causes: files once copied with
`sudo` (fix: `sudo chown -R $USER: "<game folder>"`), an SD card mounted read-only, or Flatpak Steam without access to
that drive (Flatseal can give it). Never run the updater itself with `sudo`.

**"Bannerlord is running"**. Close the game (and its launcher) first: files in use cannot be replaced safely.

**"Downloads are paused"**. On event nights the server pauses downloads for the battle. If you are already up to date,
just play. Otherwise install before or after the event.

**The mods do not show up in the launcher**. Start Bannerlord once from Steam, open **Multiplayer**, and tick the
modules. If the game still does not see them, run `./lotr-updater verify`, then send a support log (`./lotr-updater support`)
and paste the code in your Discord ticket.

**A file is shown as different after the update**. Run `./lotr-updater verify --repair`.

**Log files**: `~/.local/share/LotRMPUpdater/logs/`. Your user name is masked in the support log.

---

## For the team

### How it is built

| Project | What |
|---|---|
| `src/Lotr.Updater.Cli` | `lotr-updater` (net8.0 console app): commands, menu, terminal progress, Steam start (through `setsid`, so closing the terminal never closes Steam or the game), menu shortcut |
| `src/Lotr.Updater.Core/Game/Linux` | `LinuxSteamLocator` (Steam libraries, Flatpak, Snap, SD cards, appmanifest, Proton prefix) and `LinuxProcessProbe` (`/proc`: running game under Proton) |
| `src/LotR.Releases/Model/OsPath.cs` | path rules per OS: Linux paths are case-sensitive, Windows paths are not |
| `tests/Lotr.Updater.Cli.Tests` | the real command line against a local fake release |
| `tests/Lotr.Updater.Tests/Linux*Tests.cs` | detection on simulated Linux home folders, path rules, Linux error texts |
| `tools/linux/e2e-local.sh` | end-to-end run of the real binaries against a local fake release |
| `tools/Build-LinuxUpdater.ps1` | production build + `.tar.gz` |
| `src/LotR.Releases/Publishing/LinuxUpdaterPublisher.cs` | archive check + `publish-updater --platform linux` (`linux/latest.json`, signed); `apply-signed` and `export-site-json` (`updaterLinux`) use it |
| `src/Lotr.Updater.Core/SelfUpdate/LinuxLauncherCheck.cs` | "a new lotr-updater is available" notice (reads `linux/latest.json` only, downloads nothing) |

Linux specifics handled in the shared engine (the Windows app is unchanged): case-sensitive paths (a stale copy of a
mod file under another letter case is moved to the backup, otherwise the game might read the wrong one), Linux error
numbers (disk full `ENOSPC`, quota `EDQUOT`, read-only file system `EROFS`), free space and "same drive" by mount
point, `/home/<user>` masked in support logs, Linux wording in player messages.

### Build the player download

On the Windows PC that builds the Windows updater (no Linux needed, .NET cross-compiles):

```powershell
.\tools\Build-LinuxUpdater.ps1 -Domain middleearthevent.com
```

It uses the release public key already in `src\Lotr.Updater.Core\Security\Keys\` (or `-PublicKey <file>` like
`Build-Updater.ps1`), runs

```
dotnet publish src/Lotr.Updater.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:LotrBaseUrl=https://<domain>
```

and writes `..\dist\updater\lotr-updater-<version>-linux-x64.tar.gz` (the program with mode 755 + a README) and its
`.sha256`. The version is `<Version>` in `src/Lotr.Updater.Cli/Lotr.Updater.Cli.csproj`, separate from the Windows app.
A Release publish without the production key or the domain fails (`LOTR001` / `LOTR002`), like the Windows build.

### Publish it (signed pointer, own folder)

The Linux updater has its own folder and its own signed pointer, versioned independently from the Windows exe. The
Windows files (`updater/latest.json`, `LotRMPUpdater-<version>.exe`...) are never touched, and the Windows launcher
never reads the Linux ones.

| Published path | R2 key | What |
|---|---|---|
| `/files/updater/linux/lotr-updater-<version>-linux-x64.tar.gz` | `updater/linux/...` | the download (a new version = a new name; old ones can stay) |
| `/files/updater/linux/lotr-updater-<version>-linux-x64.tar.gz.sha256` | `updater/linux/...` | `<hex>  <name>` + LF |
| `/files/updater/linux/latest.json` | `updater/linux/latest.json` | same schema as `updater/latest.json`; `url` = the `.tar.gz` above, `signed` = false; written LAST |
| `/files/updater/linux/latest.json.sig` | `updater/linux/latest.json.sig` | release-key signature, same format as `updater/latest.json.sig` |

On the PC that holds the release key (docs/TEAM-SIGNING.md), after the build:

```powershell
lotr-publish publish-updater --platform linux --archive ..\dist\updater\lotr-updater-0.1.0-linux-x64.tar.gz `
             --updater-dir <signed>\updater --base-url https://middleearthevent.com
```

The version is plain `x.y.z` (no 4th number, no `-beta`): every later step (r2-sync, check-edge, the website
package allow-list, the Cloudflare allowlist, the site) accepts only that, so lotr-publish refuses anything else. A
`lotr-publish.exe` built before the Linux launcher has no `--platform linux` (it answers with a usage error about
`--exe`): rebuild it with `dotnet build tools\LotR.Publish -c Release`. TEAM-PUBLISH does this by itself when the
build is older than its sources, and calls exactly the command above (`--archive`, `--key`, `--version`).

It reads the whole archive and refuses anything that is not this build (gzip tar, only plain files and folders under
`lotr-updater-<version>/`, no links or `../` paths, the program present, executable, a 64-bit x86-64 ELF file; the
build's `.sha256` must match), then writes `<signed>\updater\linux\` (archive, `.sha256`, `latest.json.sig`,
`latest.json` last). On the server, `lotr-publish apply-signed` installs that folder into `files\updater\linux\`
(signature checked with the public key, archive re-checked, never a downgrade), and `export-site-json` writes it into
the site data as `updaterLinux` (null until a signed one exists): the Install page's **Linux** button then downloads it
and shows its SHA-256 and size. The R2 copy is the same four files under `updater/linux/`, `latest.json` last.

There is no self-update on Linux (a running single-file program is not swapped): `lotr-updater status` / `install`
read `linux/latest.json` (+ `.sig`, same trust rule as the Windows self-update) and only TELL the player that a newer
version is on the Install page (see "Updating lotr-updater itself" above).

### Test it

Automated, on Windows (part of the normal suite):

```powershell
$env:LOTR_SKIP_SMOKE = '1'
dotnet test LotR.Installer.sln -c Release
```

End to end with the real binaries, on Linux, the Steam Deck or WSL (nothing installed system-wide, everything in a temp
folder, `localhost` only):

```sh
dotnet publish src/Lotr.Updater.Cli -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:AllowNoProductionKey=true -o /tmp/lu
dotnet publish tests/Lotr.Updater.DevServer -c Release -r linux-x64 --self-contained -o /tmp/lud
bash tools/linux/e2e-local.sh /tmp/lu/lotr-updater /tmp/lud/lotr-updater-devserver
```

(Without a .NET SDK on the Linux machine, publish both on Windows with `-r linux-x64` and copy the two folders.) The
same script runs in Git Bash on Windows with `-r win-x64` builds. It checks: status before install (10), install and a
byte-for-byte comparison, a second run with nothing to do, a damaged and a deleted file (`verify` 6, `verify --repair`
0), the download queue answering ok / 404 / 503 / 530 / never / busy (install still 0), a tampered manifest (4) and
the event-night lock (8).

### Test status

| What | Status |
|---|---|
| Unit tests (detection on simulated Linux homes, OS path rules, Linux errors, queue fallback, CLI) | pass on Windows, net8.0 and net48 |
| Publishing: archive check, `publish-updater --platform linux`, `apply-signed`, `updaterLinux` in the site data, launcher notice | pass (`LinuxUpdaterTests`, `LinuxLauncherCheckTests`, `LauncherNoticeTests`); checked on a real `Build-LinuxUpdater.ps1` archive (14/11) |
| `e2e-local.sh` with the win-x64 builds in Git Bash | 31/31 pass |
| linux-x64 build | builds, one ELF x86-64 file, no trim warnings |
| linux-x64 binary run on Linux / WSL / Steam Deck | **not yet**: to do on a real machine before announcing it |
| Real Proton game: process detection through `/proc`, modules seen by the launcher, `steam://` start | **not yet** |

Before the first public release: run `e2e-local.sh` on a Linux machine, then one real install on a Steam Deck (or any
Linux PC with Bannerlord) against the production server, start the game and check that the modules are listed.
