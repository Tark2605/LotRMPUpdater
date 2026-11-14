using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LotR.GameDetection;
using LotR.GameDetection.Parsing;
using LotR.Releases.Model;
using Lotr.Updater.Core.IO;

namespace Lotr.Updater.Core.Game.Linux
{
    /// <summary>
    /// Finds Bannerlord on Linux and the Steam Deck. The game runs through Steam Proton, which installs the unchanged Windows
    /// depot: &lt;library&gt;/steamapps/common/Mount &amp; Blade II Bannerlord/bin/Win64_Shipping_Client/Bannerlord.exe and
    /// Modules/Native/SubModule.xml, exactly the rule of docs/DETECTION.md. Steam folders looked at (docs/LINUX.md):
    /// ~/.local/share/Steam (also the Deck), ~/.steam/steam and ~/.steam/root (links), ~/.steam/debian-installation, Flatpak
    /// (~/.var/app/com.valvesoftware.Steam/...), Snap (~/snap/steam/common/...), $XDG_DATA_HOME/Steam; libraries from both
    /// libraryfolders.vdf files, and a last-resort look at removable drives (/run/media, /media, /mnt).
    /// Read-only, apart from the optional delete-on-close write probe in Modules (same as on Windows).
    /// Every input (home, environment, mount roots, file system, link resolution) is injectable, so the tests run anywhere.
    /// </summary>
    public sealed class LinuxSteamLocator : IGameLocator
    {
        public const string FlatpakId = "com.valvesoftware.Steam";
        public static readonly string[] DefaultMountRoots = { "/run/media", "/media", "/mnt" };
        private const int MaxLevelsUp = 6;

        private readonly IFileSystem _fs;
        private readonly Func<string, string> _env;
        private readonly Func<string, string> _resolve;
        private readonly IReadOnlyList<string> _mountRoots;
        private readonly bool _probeWritable;

        /// <param name="home">The player's home folder; default $HOME.</param>
        /// <param name="environment">Environment variable reader (XDG_DATA_HOME); default the real environment.</param>
        /// <param name="mountRoots">Folders whose sub folders may hold a Steam library (removable drives).</param>
        /// <param name="resolveLinks">Turns a path into its real path (symbolic links resolved), to count ~/.steam/steam and
        /// ~/.local/share/Steam once; default realpath(3) on Linux.</param>
        /// <param name="probeWritable">Creates a delete-on-close 0-byte file in Modules to tell whether it can be written.</param>
        public LinuxSteamLocator(string home = null, Func<string, string> environment = null, IEnumerable<string> mountRoots = null,
            IFileSystem fs = null, Func<string, string> resolveLinks = null, bool probeWritable = true)
        {
            _env = environment ?? Environment.GetEnvironmentVariable;
            Home = home ?? _env("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _mountRoots = (mountRoots ?? DefaultMountRoots).ToList();
            _fs = fs ?? new RealFileSystem();
            _resolve = resolveLinks ?? RealPath;
            _probeWritable = probeWritable;
        }

        public string Home { get; }

        /// <summary>Where a Steam installation may live, most common first (existing or not).</summary>
        public IReadOnlyList<string> SteamRootCandidates()
        {
            var list = new List<string>();
            var xdg = _env("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg)) list.Add(Path.Combine(xdg, "Steam"));
            if (!string.IsNullOrEmpty(Home))
            {
                list.Add(Path.Combine(Home, ".local", "share", "Steam"));
                list.Add(Path.Combine(Home, ".steam", "steam"));
                list.Add(Path.Combine(Home, ".steam", "root"));
                list.Add(Path.Combine(Home, ".steam", "debian-installation"));
                list.Add(Path.Combine(Home, ".var", "app", FlatpakId, "data", "Steam"));
                list.Add(Path.Combine(Home, ".var", "app", FlatpakId, ".local", "share", "Steam"));
                list.Add(Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam"));
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }

        public GameSearch FindAll()
        {
            var report = new StringBuilder();
            report.AppendLine("Linux Steam search, home " + Home);
            var roots = SteamRoots(report);
            var libraries = Libraries(roots, report);

            var s = new GameSearch();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lib in libraries)
            {
                var t = FromLibrary(lib.Path, roots, report);
                if (t == null) continue;
                if (!seen.Add(_resolve(t.Root))) { report.AppendLine("  (same install as one already found)"); continue; }
                t.Evidence = "Steam library " + lib.Path + " (" + lib.Source + ")";
                s.Found.Add(t);
            }
            s.Message = s.Found.Count > 0
                ? "Found Bannerlord in " + s.Found.Count + " place" + (s.Found.Count == 1 ? "" : "s") + "."
                : $"Bannerlord was not found in your Steam libraries ({roots.Count} Steam folder{(roots.Count == 1 ? "" : "s")}, {libraries.Count} librar{(libraries.Count == 1 ? "y" : "ies")} looked at). " +
                  "Start the updater with --game \"<folder of Mount & Blade II Bannerlord>\" (the folder that holds bin and Modules).";
            report.AppendLine(s.Message);
            s.ReportForLog = report.ToString();
            return s;
        }

        public GameSearch Validate(string path)
        {
            var s = new GameSearch();
            var report = new StringBuilder();
            report.AppendLine("Linux check of a chosen folder: " + path);
            string full = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var p = path.Trim();
                    // "~/..." typed inside quotes is not expanded by the shell.
                    if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) p = Path.Combine(Home ?? "", p.Length > 2 ? p.Substring(2) : "");
                    full = Path.GetFullPath(p);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { full = null; }

            string start = null;
            if (full != null && _fs.FileExists(full)) start = Path.GetDirectoryName(full);
            else if (full != null && _fs.DirectoryExists(full)) start = full;

            if (start == null)
            {
                s.Message = "This folder or file does not exist: " + (path ?? "(none)") + ".";
            }
            else
            {
                var dir = start;
                for (int i = 0; i <= MaxLevelsUp && dir != null; i++, dir = Parent(dir))
                {
                    var t = AsGame(dir, report);
                    if (t == null) continue;
                    t.Store = StoreOf(t);
                    t.Evidence = "chosen folder " + path;
                    if (t.Store == GameStore.Steam) AddProton(t, SteamAppsAbove(t.Root), SteamRoots(new StringBuilder()), report);
                    s.Found.Add(t);
                    break;
                }
                s.Message = s.Found.Count > 0
                    ? "Bannerlord found in " + s.Found[0].Root + "."
                    : "No Bannerlord installation in " + path + ". Pick the folder that holds bin and Modules (usually .../steamapps/common/Mount & Blade II Bannerlord).";
            }
            report.AppendLine(s.Message);
            s.ReportForLog = report.ToString();
            return s;
        }

        // ------------------------------------------------------------------ Steam roots and libraries

        private List<string> SteamRoots(StringBuilder report)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in SteamRootCandidates())
            {
                if (!_fs.DirectoryExists(c)) continue;
                var real = _resolve(c);
                if (!IsSteamRoot(real)) { report.AppendLine("  " + c + ": no steamapps folder"); continue; }
                if (!seen.Add(real)) { report.AppendLine("  " + c + " = " + real + " (already listed)"); continue; }
                report.AppendLine("  Steam folder: " + c + (real != c ? " -> " + real : ""));
                roots.Add(real);
            }
            if (roots.Count == 0) report.AppendLine("  no Steam folder found");
            return roots;
        }

        private bool IsSteamRoot(string dir) =>
            Child(dir, "steamapps", true) != null || Child(Child(dir, "config", true), "libraryfolders.vdf", false) != null;

        private sealed class Library
        {
            public string Path;
            public string Source;
        }

        private List<Library> Libraries(List<string> roots, StringBuilder report)
        {
            var libs = new List<Library>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string path, string source)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string real;
                try { real = _resolve(Path.GetFullPath(path)); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return; }
                if (!_fs.DirectoryExists(real) || !seen.Add(real)) return;
                libs.Add(new Library { Path = real, Source = source });
                report.AppendLine("  library: " + real + " (" + source + ")");
            }

            foreach (var root in roots)
            {
                Add(root, "Steam folder");
                foreach (var vdf in new[] { Child(Child(root, "steamapps", true), "libraryfolders.vdf", false), Child(Child(root, "config", true), "libraryfolders.vdf", false) })
                {
                    if (vdf == null) continue;
                    foreach (var p in LibraryPaths(vdf, report)) Add(p, "libraryfolders.vdf");
                }
            }
            // Last resort: a library on a removable drive that Steam has not listed (SD card of a Deck, second disk).
            foreach (var mr in _mountRoots)
            {
                foreach (var d1 in Dirs(mr))
                {
                    foreach (var lib in LibraryCandidates(d1)) Add(lib, "drive " + mr);
                    foreach (var d2 in Dirs(d1))
                        foreach (var lib in LibraryCandidates(d2)) Add(lib, "drive " + mr);
                }
            }
            return libs;
        }

        private IEnumerable<string> LibraryCandidates(string dir)
        {
            if (Child(dir, "steamapps", true) != null) yield return dir;
            var sl = Child(dir, "SteamLibrary", true);
            if (sl != null && Child(sl, "steamapps", true) != null) yield return sl;
        }

        /// <summary>Library paths of a libraryfolders.vdf: the current format ("libraryfolders" { "0" { "path" "..." } }) and the
        /// old one ("LibraryFolders" { "1" "/path" }). A file that cannot be read or parsed gives nothing.</summary>
        internal IEnumerable<string> LibraryPaths(string vdfFile, StringBuilder report)
        {
            var list = new List<string>();
            try
            {
                string text;
                using (var s = _fs.OpenRead(vdfFile))
                using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                var root = Vdf.Parse(text);
                var lf = root["libraryfolders"];
                if (lf == null) { report?.AppendLine("  " + vdfFile + ": no libraryfolders block"); return list; }
                foreach (var kv in lf.Children)
                {
                    var path = kv.Value.Value ?? kv.Value.GetString("path");
                    if (!string.IsNullOrWhiteSpace(path) && kv.Key.All(char.IsDigit)) list.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                report?.AppendLine("  " + vdfFile + ": unreadable (" + ex.Message + ")");
            }
            return list;
        }

        // ------------------------------------------------------------------ one library

        private GameTarget FromLibrary(string lib, List<string> roots, StringBuilder report)
        {
            var steamapps = Child(lib, "steamapps", true);
            if (steamapps == null) return null;
            string installDir = null;
            var acf = Child(steamapps, "appmanifest_" + BannerlordConstants.SteamAppId + ".acf", false);
            if (acf != null)
            {
                try
                {
                    using (var s = _fs.OpenRead(acf))
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        installDir = Vdf.Parse(r.ReadToEnd())["AppState"]?.GetString("installdir");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { report.AppendLine("  " + acf + ": unreadable (" + ex.Message + ")"); }
            }
            var common = Child(steamapps, "common", true);
            string dir = null;
            if (!string.IsNullOrWhiteSpace(installDir) && RelativePath.IsSafeSegment(installDir)) dir = Child(common, installDir, true);
            if (dir == null) dir = Child(common, BannerlordConstants.SteamDefaultInstallDir, true);
            if (dir == null)
            {
                if (acf != null) report.AppendLine("  " + lib + ": appmanifest_261550.acf present but the game folder is missing");
                return null;
            }
            var t = AsGame(dir, report);
            if (t == null) return null;
            t.Store = GameStore.Steam;
            AddProton(t, steamapps, roots, report);
            return t;
        }

        /// <summary>A GameTarget when <paramref name="dir"/> is a game root (bin/Win64_Shipping_Client/Bannerlord.exe and
        /// Modules/Native/SubModule.xml, letter case of the disk accepted), else null.</summary>
        private GameTarget AsGame(string dir, StringBuilder report)
        {
            var bin = Child(Child(dir, "bin", true), BannerlordConstants.BinWin64, true);
            var exe = Child(bin, BannerlordConstants.GameExecutable, false);
            var modules = Child(dir, BannerlordConstants.ModulesFolder, true);
            var native = Child(Child(modules, BannerlordConstants.NativeModule, true), BannerlordConstants.SubModuleXml, false);
            if (exe == null || native == null) return null;

            var t = new GameTarget
            {
                Root = dir,
                ModulesPath = modules,
                Store = GameStore.Unknown,
                BinFolderName = BannerlordConstants.BinWin64,
                GameVersion = ReadVersion(Child(bin, BannerlordConstants.VersionXml, false), native),
            };
            if (_probeWritable)
            {
                var probe = _fs.ProbeWritable(modules);
                t.Writable = probe.Writable;
                if (!probe.Writable) t.Warnings.Add("The Modules folder cannot be written: " + probe.Detail);
            }
            if (!string.Equals(Path.GetFileName(modules), BannerlordConstants.ModulesFolder, StringComparison.Ordinal))
                t.Warnings.Add("The Modules folder is named \"" + Path.GetFileName(modules) + "\" on disk.");
            report.AppendLine("  game: " + dir + " (version " + (t.GameVersion ?? "unknown") + ", writable " + (t.Writable?.ToString() ?? "not tested") + ")");
            return t;
        }

        private string ReadVersion(string versionXml, string nativeSubModule)
        {
            try
            {
                if (versionXml != null)
                {
                    var v = VersionFiles.ReadGameVersionXml(ReadText(versionXml));
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                return VersionFiles.ReadSubModuleVersion(ReadText(nativeSubModule));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private string ReadText(string file)
        {
            using (var s = _fs.OpenRead(file))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        private GameStore StoreOf(GameTarget t)
        {
            var native = Child(t.ModulesPath, BannerlordConstants.NativeModule, true);
            if (Child(native, BannerlordConstants.SteamTargetFile, false) != null || SteamAppsAbove(t.Root) != null) return GameStore.Steam;
            if (Child(native, BannerlordConstants.GogTargetFile, false) != null) return GameStore.Gog;
            if (Child(native, BannerlordConstants.EpicTargetFile, false) != null) return GameStore.Epic;
            return GameStore.Unknown;
        }

        /// <summary>&lt;library&gt;/steamapps when the game sits in &lt;library&gt;/steamapps/common/&lt;game&gt;, else null.</summary>
        private static string SteamAppsAbove(string root)
        {
            var common = Parent(root);
            var steamapps = common == null ? null : Parent(common);
            return steamapps != null && string.Equals(Path.GetFileName(common), "common", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(Path.GetFileName(steamapps), "steamapps", StringComparison.OrdinalIgnoreCase) ? steamapps : null;
        }

        /// <summary>Proton prefix of the game: compatdata is usually in the game's library, sometimes in the main one.</summary>
        private void AddProton(GameTarget t, string gameSteamApps, IEnumerable<string> roots, StringBuilder report)
        {
            var places = new List<string>();
            if (gameSteamApps != null) places.Add(gameSteamApps);
            places.AddRange(roots.Select(r => Child(r, "steamapps", true)).Where(x => x != null));
            foreach (var sa in places)
            {
                var pfx = Child(Child(Child(sa, "compatdata", true), BannerlordConstants.SteamAppId, true), "pfx", true);
                if (pfx == null) continue;
                t.ProtonPrefix = pfx;
                var launcherData = Path.Combine(pfx, "drive_c", "users", "steamuser", "Documents", "Mount and Blade II Bannerlord", "Configs", "LauncherData.xml");
                if (_fs.FileExists(launcherData)) t.LauncherDataPath = launcherData;
                report.AppendLine("  Proton prefix: " + pfx);
                return;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The child called <paramref name="name"/>: exact letter case first, else the only reasonable other spelling
        /// (Linux file names are case-sensitive; old Steam installs used "SteamApps"). Path as it is on disk; null if absent.</summary>
        internal string Child(string dir, string name, bool directory)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            List<string> entries;
            try
            {
                if (!_fs.DirectoryExists(dir)) return null;
                entries = (directory ? _fs.EnumerateDirectories(dir) : _fs.EnumerateFiles(dir, false)).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            return entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.Ordinal))
                   ?? entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e), name, StringComparison.OrdinalIgnoreCase));
        }

        private IEnumerable<string> Dirs(string dir)
        {
            try { return _fs.DirectoryExists(dir) ? _fs.EnumerateDirectories(dir).ToList() : new List<string>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return new List<string>(); }
        }

        private static string Parent(string dir)
        {
            var t = OsPath.TrimEndSeparators(dir);
            var p = Path.GetDirectoryName(t);
            return string.IsNullOrEmpty(p) ? null : p;
        }

        /// <summary>realpath(3) on Linux (links resolved); the full path elsewhere or when it fails.</summary>
        public static string RealPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (OsPath.IsWindows) return OsPath.TrimEndSeparators(full);
            IntPtr p = IntPtr.Zero;
            try
            {
                p = realpath(full, IntPtr.Zero);
                if (p == IntPtr.Zero) return OsPath.TrimEndSeparators(full);
                return Marshal.PtrToStringAnsi(p) ?? OsPath.TrimEndSeparators(full); // UTF-8 on Unix
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { return OsPath.TrimEndSeparators(full); }
            finally { if (p != IntPtr.Zero) free(p); }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr realpath(string path, IntPtr resolved);

        [DllImport("libc")]
        private static extern void free(IntPtr p);
    }
}
