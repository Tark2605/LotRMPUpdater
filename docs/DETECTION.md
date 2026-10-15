# Game detection (step 1)

`LotR.GameDetection` finds Mount & Blade II: Bannerlord client installs and the `Modules` folder to install into.

## Public API (for the updater)

```csharp
GameLocateResult r = GameLocator.FindAll();            // or FindAll(new DetectionOptions { DeepScan = true })
GameLocateResult p = GameLocator.Validate(pickedPath); // manual pick: exe, bin, root, Modules or Xbox outer folder
r.Found; r.Message;                                    // player-facing paragraph
foreach (GameInstall g in r.Installs)                  // most reliable first
    g.Root; g.ModulesPath; g.Store; g.GameVersion; g.Writable; g.Source; g.Details /* warnings, evidence */;
r.Report                                               // rejections (reason + message), hints, source errors
ReportJson.Serialize(r.Report, maskUserName: true)     // support log
```
`GameInstall` members are a stable contract: only additions. Tests use `GameLocator.FindAll(SystemAccess)` with fakes.

## Rule

A folder **D** is a game root when `D\bin\<platform>\Bannerlord.exe` exists **and** `D\Modules\Native\SubModule.xml`
exists. Any candidate (exe, bin folder, root, Modules, Xbox outer folder) is resolved by walking up at most 6 levels
(plus `Content\` for Xbox). Platforms: `Win64_Shipping_Client` (Steam/Epic/GOG) and
`Gaming.Desktop.x64_Shipping_Client` (Xbox PC). On Xbox, `Launcher.Native.exe` is accepted when `Bannerlord.exe` is
absent. Results are de-duplicated by canonical path (junctions, symlinks, 8.3 names, case).

Rejections with reasons: `PathNotFound`, `ShortcutBroken`, `ShortcutUnreadable`, `NoGameExecutable`,
`NativeModuleMissing`, `NativeSubModuleXmlMissing`, `DedicatedServer`, `ModdingKitOnly`.

## Sources (aggregated, de-duplicated, most reliable first)

1. **Running process** — Bannerlord / Bannerlord.Native / Launcher.Native / TaleWorlds.MountAndBlade.Launcher / BLSE exes (QueryFullProcessImageName, no elevation).
2. **Steam** — `HKCU\Software\Valve\Steam\SteamPath`, `HKLM\SOFTWARE\(WOW6432Node\)Valve\Steam\InstallPath`, default folder → every library of `steamapps\libraryfolders.vdf` (new and pre-2021 formats) → `appmanifest_261550.acf` `installdir`; plus the `Steam App 261550` uninstall key.
3. **Epic** — `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` and `%ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat` (AppName `Chickadee`, or display name / launch exe).
4. **GOG** — `HKLM\SOFTWARE\(WOW6432Node\)GOG.com\Games\<id>` `path` / `exe` (ids 1564781494, 1802539526).
5. **Xbox PC** — `<drive>:\.GamingRoot` on every fixed drive (format `RGBX` + uint32 + UTF-16 folder names, checked on a real PC) and `<drive>:\XboxGames`, each `<game>\Content`; `HKCU\...\AppModel\Repository\Packages\TaleWorldsEntertainment.MountBladeIIBannerlord_*\PackageRootFolder` (WindowsApps); `HKLM\SOFTWARE\Microsoft\GamingServices\PackageRepository\Root`.
6. **Shortcuts** — `.lnk` / `.url` on user + public Desktop and Start Menu (depth 4). `.lnk` parsed without COM ([MS-SHLLINK]); `steam://…261550`, `com.epicgames.launcher://…Chickadee`, `goggalaxy://…<id>`, and `steam.exe -applaunch 261550` shortcuts trigger a lookup in that store.
7. **Uninstall keys** — HKLM 64/32 + HKCU `…\Uninstall\*` with a Bannerlord DisplayName (`InstallLocation` or `DisplayIcon`); DLC / Modding Kit entries are only probed.
8. **Deep scan (opt-in)** — fixed drives only, common game roots (`Games`, `SteamLibrary`, `XboxGames`, `Program Files…`), depth 4, 200 000-folder budget.
9. **Manual pick** — clear rejection message when invalid.

Store: the source that vouches for the folder wins; otherwise markers (`Modules\Native\steam.target|epic.target|gog.target`,
`.egstore`, `goggame-*.info`, `MicrosoftGame.config`/`appxmanifest.xml`, `WindowsApps`, `steamapps\common`).
Version: `bin\<platform>\Version.xml` (`<Singleplayer Value="v1.4.8"/>`), fallback `Modules\Native\SubModule.xml` `<Version>`.

## Writable flag

`WriteProbeMode.TempFile` (default): creates `Modules\.lotr-write-probe-<guid>.tmp`, hidden, 0 bytes, with
`FILE_FLAG_DELETE_ON_CLOSE`, and closes it at once; Windows deletes it when the handle closes (even on a crash), the
detector checks it is gone. Never elevated (the exe manifest is `asInvoker`, which also disables UAC file
virtualization). `AccessCheck` asks Windows about the folder ACL and writes nothing; `None` skips. WindowsApps installs
are reported as protected / not writable, with advice to move the game to `<drive>:\XboxGames`.

## Research references

- Epic AppName `Chickadee`, Xbox package `TaleWorldsEntertainment.MountBladeIIBannerlord`, Xbox exe `Launcher.Native.exe`, GOG ids, bin folder names: BUTR Vortex extension, https://github.com/BUTR/game-mount-and-blade2/blob/master/src/common.ts
- Store marker files and Xbox (GDK) `appxmanifest.xml`: BLSE, https://github.com/BUTR/Bannerlord.BLSE/blob/master/src/Bannerlord.BLSE.Shared/Utils/PlatformHelper.cs
- Bin folder / exe constants: https://github.com/BUTR/Bannerlord.LauncherManager/blob/master/src/Bannerlord.LauncherManager/Constants.cs
- GOG ids: https://www.gogdb.org/product/1564781494 (game), https://www.gogdb.org/product/1802539526 (package)
- Shell link format: https://learn.microsoft.com/openspecs/windows_protocols/ms-shllink/
- Checked on the maintainer's PC (read-only): `bin\Win64_Shipping_Client\Version.xml` = `v1.4.8`, `Modules\Native\steam.target`, `.GamingRoot` bytes on C: and F:, Xbox layout `XboxGames\<game>\Content\{MicrosoftGame.config, appxmanifest.xml}`; BLSE puts its exes in `bin\Gaming.Desktop.x64_Shipping_Client` of a Steam install (handled: no Bannerlord.exe there).

## Open points

- `Bannerlord.exe` inside an Xbox install's `Gaming.Desktop.x64_Shipping_Client` is not confirmed by a public source (BUTR names `Launcher.Native.exe`); both are accepted. Needs one Game Pass player's folder listing.
- Real Xbox folder name under `XboxGames` (assumed "Mount & Blade II- Bannerlord"; renamed folders are probed anyway).
- Epic AppName comes from community tools, not Epic; the Epic source also matches on DisplayName.
