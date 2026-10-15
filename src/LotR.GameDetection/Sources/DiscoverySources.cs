using System;
using System.Collections.Generic;
using System.Linq;
using LotR.GameDetection.Abstractions;
using LotR.GameDetection.Parsing;

namespace LotR.GameDetection.Sources
{
    public sealed class DiscoveryContext
    {
        public DiscoveryContext(SystemAccess system, DetectionOptions options)
        {
            System = system;
            Options = options;
        }

        public SystemAccess System { get; }
        public DetectionOptions Options { get; }
        public IFileSystem Fs => System.FileSystem;
        public IRegistryReader Registry => System.Registry;
        public ISystemEnvironment Env => System.Environment;
        public List<string> Hints { get; } = new List<string>();
        public List<RejectedCandidate> Rejections { get; } = new List<RejectedCandidate>();

        public string RegString(RegistryHiveKind hive, RegistryViewKind view, string key, string value) =>
            Registry.GetValue(hive, view, key, value) as string;
    }

    public interface IDiscoverySource
    {
        DiscoverySource Kind { get; }

        /// <summary>Returns candidate paths. May throw; the detector records the error and continues.</summary>
        IReadOnlyList<Candidate> Discover(DiscoveryContext ctx);
    }

    /// <summary>1. Running Bannerlord.exe / launcher: the most reliable source when the game is open.</summary>
    public sealed class RunningProcessSource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.RunningProcess;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            foreach (var p in ctx.System.Processes.GetProcesses(BannerlordConstants.ProcessNames))
            {
                if (p.ExecutablePath == null)
                {
                    ctx.Hints.Add($"Process {p.Name}.exe (pid {p.Id}) is running but its path could not be read: {p.Error}");
                    continue;
                }
                list.Add(new Candidate(p.ExecutablePath, Kind, $"running process {p.Name}.exe (pid {p.Id})"));
            }
            return list;
        }
    }

    /// <summary>2. Steam: registry -> every library in libraryfolders.vdf -> appmanifest_261550.acf installdir; plus the Steam uninstall key.</summary>
    public sealed class SteamSource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.Steam;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            var steamRoots = new List<KeyValuePair<string, string>>();

            void AddRoot(string path, string origin)
            {
                var n = PathUtil.Normalize(path);
                if (n != null && !steamRoots.Any(r => PathUtil.PathEquals(r.Key, n)))
                    steamRoots.Add(new KeyValuePair<string, string>(n, origin));
            }

            AddRoot(ctx.RegString(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, @"Software\Valve\Steam", "SteamPath"), @"HKCU\Software\Valve\Steam\SteamPath");
            AddRoot(ctx.RegString(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath"), @"HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath");
            AddRoot(ctx.RegString(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, @"SOFTWARE\Valve\Steam", "InstallPath"), @"HKLM\SOFTWARE\Valve\Steam\InstallPath");
            var pf86 = ctx.Env.GetFolder(KnownFolderKind.ProgramFilesX86);
            if (pf86 != null && ctx.Fs.DirectoryExists(PathUtil.Combine(pf86, "Steam")))
                AddRoot(PathUtil.Combine(pf86, "Steam"), "default Steam folder");

            // Libraries: each Steam root is itself a library, plus the ones listed in libraryfolders.vdf
            var libraries = new List<KeyValuePair<string, string>>();
            void AddLibrary(string path, string origin)
            {
                var n = PathUtil.Normalize(path);
                if (n != null && !libraries.Any(l => PathUtil.PathEquals(l.Key, n)))
                    libraries.Add(new KeyValuePair<string, string>(n, origin));
            }

            foreach (var root in steamRoots)
            {
                if (!ctx.Fs.DirectoryExists(root.Key))
                {
                    ctx.Hints.Add($"Steam registry entry {root.Value} points to a missing folder: {root.Key}");
                    continue;
                }
                AddLibrary(root.Key, root.Value);
                foreach (var vdfPath in new[] { PathUtil.Combine(root.Key, "steamapps", "libraryfolders.vdf"), PathUtil.Combine(root.Key, "config", "libraryfolders.vdf") })
                {
                    if (!ctx.Fs.FileExists(vdfPath))
                        continue;
                    foreach (var lib in ParseLibraryFolders(ctx.Fs.ReadAllText(vdfPath), out var appListedIn))
                    {
                        AddLibrary(lib, root.Value + " -> " + vdfPath);
                        if (appListedIn.Contains(lib) && !ctx.Fs.FileExists(PathUtil.Combine(lib, "steamapps", "appmanifest_" + BannerlordConstants.SteamAppId + ".acf")))
                            ctx.Hints.Add($"Steam lists Bannerlord (app {BannerlordConstants.SteamAppId}) in library {lib} but its appmanifest is missing.");
                    }
                }
            }

            foreach (var lib in libraries)
            {
                var steamapps = PathUtil.Combine(lib.Key, "steamapps");
                var acf = PathUtil.Combine(steamapps, "appmanifest_" + BannerlordConstants.SteamAppId + ".acf");
                if (ctx.Fs.FileExists(acf))
                {
                    string installDir = null;
                    try { installDir = Vdf.Parse(ctx.Fs.ReadAllText(acf))["AppState"]?.GetString("installdir"); }
                    catch { /* unreadable manifest: fall back to the default folder name */ }
                    var dir = PathUtil.Combine(steamapps, "common", string.IsNullOrEmpty(installDir) ? BannerlordConstants.SteamDefaultInstallDir : installDir);
                    list.Add(new Candidate(dir, Kind, $"{lib.Value} -> library {lib.Key} -> appmanifest_{BannerlordConstants.SteamAppId}.acf installdir \"{installDir}\"", GameStore.Steam, claimed: true));
                }
                else
                {
                    var dir = PathUtil.Combine(steamapps, "common", BannerlordConstants.SteamDefaultInstallDir);
                    if (ctx.Fs.DirectoryExists(dir))
                        list.Add(new Candidate(dir, Kind, $"{lib.Value} -> library {lib.Key} -> default folder (no appmanifest_261550.acf)", GameStore.Steam, claimed: false));
                }
            }

            foreach (var view in new[] { RegistryViewKind.Registry64, RegistryViewKind.Registry32 })
            {
                var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + BannerlordConstants.SteamUninstallKey;
                var loc = ctx.RegString(RegistryHiveKind.LocalMachine, view, key, "InstallLocation");
                if (!string.IsNullOrEmpty(loc))
                    list.Add(new Candidate(loc, Kind, $@"HKLM\{key}\InstallLocation ({view})", GameStore.Steam, claimed: true));
            }
            return list;
        }

        /// <summary>Library paths from libraryfolders.vdf (new nested format and the pre-2021 flat format).</summary>
        public static List<string> ParseLibraryFolders(string text, out HashSet<string> librariesListingBannerlord)
        {
            librariesListingBannerlord = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            var root = Vdf.Parse(text);
            var top = root.Children.Count > 0 ? root.Children[0].Value : null;
            if (top == null)
                return result;

            foreach (var kv in top.Children)
            {
                if (!kv.Key.All(char.IsDigit))
                    continue;
                string path = kv.Value.Value ?? kv.Value.GetString("path");
                path = PathUtil.Normalize(path);
                if (path == null)
                    continue;
                result.Add(path);
                var apps = kv.Value["apps"];
                if (apps != null && apps[BannerlordConstants.SteamAppId] != null)
                    librariesListingBannerlord.Add(path);
            }
            return result;
        }
    }

    /// <summary>3. Epic: launcher manifests (*.item) and LauncherInstalled.dat.</summary>
    public sealed class EpicSource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.Epic;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            var programData = ctx.Env.GetFolder(KnownFolderKind.ProgramData);
            if (programData == null)
                return list;

            var manifests = PathUtil.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            foreach (var item in ctx.Fs.EnumerateFiles(manifests, "*.item"))
            {
                object json;
                try { json = MiniJson.Parse(ctx.Fs.ReadAllText(item)); }
                catch (Exception ex) { ctx.Hints.Add($"Unreadable Epic manifest {item}: {ex.Message}"); continue; }

                var appName = MiniJson.GetString(json, "AppName");
                var display = MiniJson.GetString(json, "DisplayName");
                var exe = MiniJson.GetString(json, "LaunchExecutable");
                var location = MiniJson.GetString(json, "InstallLocation");
                if (string.IsNullOrEmpty(location))
                    continue;
                bool match = string.Equals(appName, BannerlordConstants.EpicAppName, StringComparison.OrdinalIgnoreCase)
                             || BannerlordConstants.MatchesName(display)
                             || (exe != null && exe.IndexOf("Bannerlord", StringComparison.OrdinalIgnoreCase) >= 0);
                list.Add(new Candidate(location, Kind, $"Epic manifest {PathUtil.GetFileName(item)} (AppName \"{appName}\", DisplayName \"{display}\")", GameStore.Epic, claimed: match));
            }

            var dat = PathUtil.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
            if (ctx.Fs.FileExists(dat))
            {
                try
                {
                    var json = MiniJson.Parse(ctx.Fs.ReadAllText(dat)) as Dictionary<string, object>;
                    if (json != null && json.TryGetValue("InstallationList", out var arr) && arr is List<object> entries)
                    {
                        foreach (var e in entries)
                        {
                            var appName = MiniJson.GetString(e, "AppName");
                            var location = MiniJson.GetString(e, "InstallLocation");
                            if (string.IsNullOrEmpty(location))
                                continue;
                            bool match = string.Equals(appName, BannerlordConstants.EpicAppName, StringComparison.OrdinalIgnoreCase) || BannerlordConstants.MatchesName(location);
                            list.Add(new Candidate(location, Kind, $"Epic LauncherInstalled.dat (AppName \"{appName}\")", GameStore.Epic, claimed: match));
                        }
                    }
                }
                catch (Exception ex)
                {
                    ctx.Hints.Add($"Unreadable Epic LauncherInstalled.dat: {ex.Message}");
                }
            }
            return list;
        }
    }

    /// <summary>4. GOG: HKLM\SOFTWARE\(WOW6432Node\)GOG.com\Games\&lt;id&gt; "path" (written by the GOG installer and Galaxy).</summary>
    public sealed class GogSource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.Gog;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            foreach (var view in new[] { RegistryViewKind.Registry32, RegistryViewKind.Registry64 })
            {
                const string games = @"SOFTWARE\GOG.com\Games";
                foreach (var id in ctx.Registry.GetSubKeyNames(RegistryHiveKind.LocalMachine, view, games))
                {
                    var key = games + "\\" + id;
                    var name = ctx.RegString(RegistryHiveKind.LocalMachine, view, key, "gameName");
                    if (!BannerlordConstants.IsGogId(id) && !BannerlordConstants.MatchesName(name))
                        continue;
                    var path = ctx.RegString(RegistryHiveKind.LocalMachine, view, key, "path");
                    var exe = ctx.RegString(RegistryHiveKind.LocalMachine, view, key, "exe");
                    var target = !string.IsNullOrEmpty(path) ? path : exe;
                    if (!string.IsNullOrEmpty(target))
                        list.Add(new Candidate(target, Kind, $@"HKLM\{key} ({view}) gameName ""{name}""", GameStore.Gog, claimed: true));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 5. Xbox PC / Game Pass: ".GamingRoot" files at drive roots (+ default &lt;drive&gt;:\XboxGames), the AppModel package
    /// repository (PackageRootFolder) and the Gaming Services package repository ("Root" values).
    /// </summary>
    public sealed class XboxSource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.Xbox;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            var gamingFolders = new List<KeyValuePair<string, string>>();
            foreach (var drive in ctx.Fs.GetFixedDriveRoots())
            {
                var marker = PathUtil.Combine(drive, ".GamingRoot");
                if (ctx.Fs.FileExists(marker))
                {
                    try
                    {
                        foreach (var f in GamingRootFile.Parse(ctx.Fs.ReadAllBytes(marker)))
                            gamingFolders.Add(new KeyValuePair<string, string>(PathUtil.Combine(drive, f), marker));
                    }
                    catch (Exception ex)
                    {
                        ctx.Hints.Add($"Unreadable {marker}: {ex.Message}");
                    }
                }
                var def = PathUtil.Combine(drive, "XboxGames");
                if (ctx.Fs.DirectoryExists(def) && !gamingFolders.Any(g => PathUtil.PathEquals(g.Key, def)))
                    gamingFolders.Add(new KeyValuePair<string, string>(def, "default XboxGames folder"));
            }

            foreach (var g in gamingFolders)
            {
                foreach (var game in ctx.Fs.EnumerateDirectories(g.Key))
                {
                    bool named = BannerlordConstants.MatchesName(PathUtil.GetFileName(game));
                    // Unnamed folders are probed silently (cheap: one file check) in case the folder was renamed.
                    if (!named && new GameRootResolver(ctx.Fs).FindGameExecutables(PathUtil.Combine(game, "Content")).Count == 0)
                        continue;
                    list.Add(new Candidate(game, Kind, $"Xbox games folder {g.Key} (from {g.Value})", GameStore.Xbox, claimed: named));
                }
            }

            const string packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            foreach (var pkg in ctx.Registry.GetSubKeyNames(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, packages))
            {
                if (!pkg.StartsWith(BannerlordConstants.XboxPackageName, StringComparison.OrdinalIgnoreCase) && !BannerlordConstants.MatchesName(pkg))
                    continue;
                var root = ctx.RegString(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, packages + "\\" + pkg, "PackageRootFolder");
                if (!string.IsNullOrEmpty(root))
                    list.Add(new Candidate(root, Kind, $@"HKCU\...\AppModel\Repository\Packages\{pkg}\PackageRootFolder", GameStore.Xbox, claimed: true));
            }

            const string gsRoot = @"SOFTWARE\Microsoft\GamingServices\PackageRepository\Root";
            foreach (var a in ctx.Registry.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, gsRoot))
            {
                foreach (var b in ctx.Registry.GetSubKeyNames(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, gsRoot + "\\" + a))
                {
                    var key = gsRoot + "\\" + a + "\\" + b;
                    var pkg = ctx.RegString(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "Package");
                    if (!BannerlordConstants.MatchesName(pkg) && !BannerlordConstants.MatchesName(b))
                        continue;
                    var root = ctx.RegString(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "Root");
                    if (!string.IsNullOrEmpty(root))
                        list.Add(new Candidate(root, Kind, $@"HKLM\...\GamingServices\PackageRepository\Root\...\{b} (Package {pkg})", GameStore.Xbox, claimed: true));
                }
            }
            return list;
        }
    }

    /// <summary>6. Shortcuts (.lnk / .url) on the user and public desktops and Start menus.</summary>
    public sealed class ShortcutSource : IDiscoverySource
    {
        private const int MaxDepth = 4;
        public DiscoverySource Kind => DiscoverySource.Shortcut;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            var folders = new[] { KnownFolderKind.UserDesktop, KnownFolderKind.CommonDesktop, KnownFolderKind.UserStartMenu, KnownFolderKind.CommonStartMenu }
                .Select(ctx.Env.GetFolder).Where(f => f != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var folder in folders)
                Walk(ctx, folder, 0, list);
            return list;
        }

        private void Walk(DiscoveryContext ctx, string dir, int depth, List<Candidate> list)
        {
            foreach (var lnk in ctx.Fs.EnumerateFiles(dir, "*.lnk"))
            {
                bool named = BannerlordConstants.MatchesName(PathUtil.GetFileName(lnk));
                var target = ctx.System.Shortcuts.Resolve(lnk);
                if (target.Error != null || target.TargetPath == null)
                {
                    if (named)
                        ctx.Rejections.Add(new RejectedCandidate(lnk, Kind, "shortcut " + lnk, RejectionReason.ShortcutUnreadable, "The shortcut could not be read: " + target.Error));
                    continue;
                }
                var targetName = PathUtil.GetFileName(target.TargetPath);
                bool targetIsGame = BannerlordConstants.IsGameExecutableName(targetName);

                // Store launcher shortcuts: "steam.exe -applaunch 261550", "EpicGamesLauncher.exe com.epicgames.launcher://..."
                var launcherStore = StoreFromLauncherShortcut(targetName, target.Arguments);
                if (launcherStore != GameStore.Unknown)
                {
                    StoreLookup(ctx, launcherStore, $"shortcut {lnk} -> {target.TargetPath} {target.Arguments}".TrimEnd(), list);
                    continue;
                }

                if (!named && !targetIsGame)
                    continue;
                list.Add(new Candidate(target.TargetPath, Kind, $"shortcut {lnk} -> {target.TargetPath}", GameStore.Unknown, claimed: true));
            }

            foreach (var url in ctx.Fs.EnumerateFiles(dir, "*.url"))
            {
                string link;
                try { link = UrlShortcutFile.ReadUrl(ctx.Fs.ReadAllText(url)); }
                catch { continue; }
                if (link == null)
                    continue;
                var store = StoreFromUrl(link, PathUtil.GetFileName(url));
                if (store == GameStore.Unknown)
                {
                    if (link.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = Uri.TryCreate(link, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;
                        if (path != null && (BannerlordConstants.IsGameExecutableName(PathUtil.GetFileName(path)) || BannerlordConstants.MatchesName(PathUtil.GetFileName(url))))
                            list.Add(new Candidate(path, Kind, $"internet shortcut {url} -> {link}", GameStore.Unknown, claimed: true));
                    }
                    continue;
                }
                StoreLookup(ctx, store, $"internet shortcut {url} ({link})", list);
            }

            if (depth >= MaxDepth)
                return;
            foreach (var sub in ctx.Fs.EnumerateDirectories(dir))
                Walk(ctx, sub, depth + 1, list);
        }

        /// <summary>Store a launcher URL points to, when it is a Bannerlord URL (steam://rungameid/261550, com.epicgames.launcher://apps/...Chickadee..., goggalaxy://openGameView/1564781494).</summary>
        internal static GameStore StoreFromUrl(string link, string shortcutName)
        {
            if (string.IsNullOrEmpty(link))
                return GameStore.Unknown;
            if (link.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) && ContainsId(link, BannerlordConstants.SteamAppId))
                return GameStore.Steam;
            if (link.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase)
                && (link.IndexOf(BannerlordConstants.EpicAppName, StringComparison.OrdinalIgnoreCase) >= 0 || BannerlordConstants.MatchesName(Uri.UnescapeDataString(link) + " " + shortcutName)))
                return GameStore.Epic;
            if (link.StartsWith("goggalaxy://", StringComparison.OrdinalIgnoreCase) && BannerlordConstants.GogIds.Any(g => ContainsId(link, g)))
                return GameStore.Gog;
            return GameStore.Unknown;
        }

        internal static GameStore StoreFromLauncherShortcut(string targetFileName, string arguments)
        {
            if (string.IsNullOrEmpty(arguments) || string.IsNullOrEmpty(targetFileName))
                return GameStore.Unknown;
            if (string.Equals(targetFileName, "steam.exe", StringComparison.OrdinalIgnoreCase) && ContainsId(arguments, BannerlordConstants.SteamAppId))
                return GameStore.Steam;
            if (targetFileName.StartsWith("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase))
                return StoreFromUrl(arguments.Trim().Trim('"'), string.Empty) == GameStore.Epic ? GameStore.Epic : GameStore.Unknown;
            if (targetFileName.StartsWith("GalaxyClient", StringComparison.OrdinalIgnoreCase) && BannerlordConstants.GogIds.Any(g => ContainsId(arguments, g)))
                return GameStore.Gog;
            return GameStore.Unknown;
        }

        /// <summary>True when <paramref name="id"/> appears as a whole number in <paramref name="text"/> (2615500 does not match 261550).</summary>
        private static bool ContainsId(string text, string id)
        {
            int idx = 0;
            while ((idx = text.IndexOf(id, idx, StringComparison.Ordinal)) >= 0)
            {
                bool before = idx == 0 || !char.IsDigit(text[idx - 1]);
                bool after = idx + id.Length >= text.Length || !char.IsDigit(text[idx + id.Length]);
                if (before && after)
                    return true;
                idx += id.Length;
            }
            return false;
        }

        /// <summary>
        /// A store shortcut does not contain a path: ask that store where Bannerlord is installed and record the result
        /// as found through the shortcut (store hint kept). Nothing found = a hint only (the game may have been uninstalled).
        /// </summary>
        private void StoreLookup(DiscoveryContext ctx, GameStore store, string origin, List<Candidate> list)
        {
            IDiscoverySource source;
            switch (store)
            {
                case GameStore.Steam: source = new SteamSource(); break;
                case GameStore.Epic: source = new EpicSource(); break;
                case GameStore.Gog: source = new GogSource(); break;
                default: return;
            }
            IReadOnlyList<Candidate> found;
            try { found = source.Discover(ctx); }
            catch (Exception ex) { ctx.Hints.Add($"{origin}: {store} lookup failed: {ex.Message}"); return; }

            var claimed = found.Where(c => c.Claimed).ToList();
            if (claimed.Count == 0)
            {
                ctx.Hints.Add($"{store} shortcut to Bannerlord found ({origin}), but {store} does not list an installed copy (uninstalled, or installed for another Windows user?).");
                return;
            }
            foreach (var c in claimed)
                list.Add(new Candidate(c.Path, Kind, $"{origin} -> {store} lookup: {c.Detail}", store, claimed: true));
        }
    }

    /// <summary>7. Windows "Apps &amp; features" uninstall entries (HKLM/HKCU, 64 and 32-bit views).</summary>
    public sealed class UninstallRegistrySource : IDiscoverySource
    {
        public DiscoverySource Kind => DiscoverySource.UninstallRegistry;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var scopes = new[]
            {
                new KeyValuePair<RegistryHiveKind, RegistryViewKind>(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64),
                new KeyValuePair<RegistryHiveKind, RegistryViewKind>(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32),
                new KeyValuePair<RegistryHiveKind, RegistryViewKind>(RegistryHiveKind.CurrentUser, RegistryViewKind.Default),
            };
            foreach (var scope in scopes)
            {
                foreach (var sub in ctx.Registry.GetSubKeyNames(scope.Key, scope.Value, uninstall))
                {
                    var key = uninstall + "\\" + sub;
                    var name = ctx.RegString(scope.Key, scope.Value, key, "DisplayName");
                    if ((name ?? string.Empty).IndexOf("Bannerlord", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    // "Mount & Blade II: Bannerlord" itself is a claim; DLC, Modding Kit, soundtrack... are only probed.
                    bool isGame = IsGameDisplayName(name);
                    var loc = ctx.RegString(scope.Key, scope.Value, key, "InstallLocation");
                    if (string.IsNullOrEmpty(loc))
                    {
                        var icon = ctx.RegString(scope.Key, scope.Value, key, "DisplayIcon");
                        if (!string.IsNullOrEmpty(icon))
                        {
                            icon = icon.Trim().Trim('"');
                            int comma = icon.LastIndexOf(',');
                            if (comma > 2) icon = icon.Substring(0, comma).Trim('"');
                            loc = icon;
                        }
                    }
                    if (string.IsNullOrEmpty(loc))
                        continue;

                    var hint = GameStore.Unknown;
                    if (sub.Equals(BannerlordConstants.SteamUninstallKey, StringComparison.OrdinalIgnoreCase)) hint = GameStore.Steam;
                    else if (BannerlordConstants.GogIds.Any(g => sub.StartsWith(g, StringComparison.OrdinalIgnoreCase))) hint = GameStore.Gog;

                    var hiveName = scope.Key == RegistryHiveKind.LocalMachine ? "HKLM" : "HKCU";
                    list.Add(new Candidate(loc, Kind, $@"{hiveName}\{key} ({scope.Value}) ""{name}""", hint, claimed: isGame));
                }
            }
            return list;
        }

        internal static bool IsGameDisplayName(string name)
        {
            var letters = new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return letters == "MOUNTBLADEIIBANNERLORD" || letters == "MOUNTANDBLADEIIBANNERLORD" || letters == "MOUNTBLADE2BANNERLORD" || letters == "BANNERLORD";
        }
    }

    /// <summary>8. Opt-in slow scan of common game folders on fixed drives (depth and size limited).</summary>
    public sealed class DeepScanSource : IDiscoverySource
    {
        private static readonly string[] RootNames =
        {
            "Program Files", "Program Files (x86)", "Games", "Jeux", "SteamLibrary", "Steam", "XboxGames", "Epic Games", "GOG Games", "GOG Galaxy",
            @"Program Files\Epic Games", @"Program Files (x86)\Steam", @"Program Files (x86)\GOG Galaxy\Games", @"Program Files\ModifiableWindowsApps",
        };

        private static readonly HashSet<string> SkipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "WindowsApps", "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "Modules", "Microsoft", "Common Files", "WindowsPowerShell", "dotnet",
        };

        public DiscoverySource Kind => DiscoverySource.DeepScan;

        public IReadOnlyList<Candidate> Discover(DiscoveryContext ctx)
        {
            var list = new List<Candidate>();
            int budget = ctx.Options.DeepScanMaxDirectories;
            // folder -> remaining depth it was explored with (a folder first seen at the edge of a shallow scan is explored again deeper)
            var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var drive in ctx.Fs.GetFixedDriveRoots())
            {
                // drive root itself, one level only (catches D:\Mount & Blade II Bannerlord)
                Scan(ctx, drive, 1, list, visited, ref budget);
                foreach (var name in RootNames)
                {
                    var root = PathUtil.Combine(drive, name);
                    if (ctx.Fs.DirectoryExists(root))
                        Scan(ctx, root, ctx.Options.DeepScanMaxDepth, list, visited, ref budget);
                }
            }
            if (budget <= 0)
                ctx.Hints.Add("Deep scan stopped early: directory budget exhausted.");
            return list;
        }

        private void Scan(DiscoveryContext ctx, string root, int maxDepth, List<Candidate> list, Dictionary<string, int> visited, ref int budget)
        {
            var resolver = new GameRootResolver(ctx.Fs);
            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            while (queue.Count > 0 && budget > 0)
            {
                var item = queue.Dequeue();
                int remaining = maxDepth - item.Value;
                bool seen = visited.TryGetValue(item.Key, out var before);
                if (seen && before >= remaining)
                    continue;
                visited[item.Key] = remaining;
                if (!seen)
                    budget--;

                if (resolver.FindGameExecutables(item.Key).Count > 0)
                {
                    if (!seen)
                        list.Add(new Candidate(item.Key, Kind, $"deep scan of {root}", GameStore.Unknown, claimed: true));
                    continue; // do not descend into a game
                }

                if (item.Value >= maxDepth)
                    continue;
                foreach (var sub in ctx.Fs.EnumerateDirectories(item.Key))
                {
                    if (SkipNames.Contains(PathUtil.GetFileName(sub)) && !PathUtil.PathEquals(sub, root))
                        continue;
                    queue.Enqueue(new KeyValuePair<string, int>(sub, item.Value + 1));
                }
            }
        }
    }
}
