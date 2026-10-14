using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LotR.GameDetection.Abstractions;
using LotR.GameDetection.Parsing;
using LotR.GameDetection.Sources;

namespace LotR.GameDetection
{
    /// <summary>
    /// Runs every discovery source, validates each candidate (anchor on Bannerlord.exe, walk up to the root,
    /// require Modules\Native\SubModule.xml), de-duplicates by canonical path and returns a report.
    /// Strictly read-only: nothing on disk or in the registry is created, modified or deleted.
    /// </summary>
    public sealed class GameDetector
    {
        private readonly SystemAccess _sys;
        private readonly GameRootResolver _resolver;

        public GameDetector(SystemAccess system)
        {
            _sys = system ?? throw new ArgumentNullException(nameof(system));
            _resolver = new GameRootResolver(system.FileSystem);
        }

        public static GameDetector CreateForThisPc() => new GameDetector(SystemAccess.CreateReal());

        public static IReadOnlyList<IDiscoverySource> DefaultSources(DetectionOptions options)
        {
            var list = new List<IDiscoverySource>
            {
                new RunningProcessSource(),
                new SteamSource(),
                new EpicSource(),
                new GogSource(),
                new XboxSource(),
                new ShortcutSource(),
                new UninstallRegistrySource(),
            };
            if (options.DeepScan)
                list.Add(new DeepScanSource());
            return list;
        }

        public DetectionReport Detect(DetectionOptions options = null)
        {
            options = options ?? new DetectionOptions();
            var sw = Stopwatch.StartNew();
            var report = new DetectionReport();
            var ctx = new DiscoveryContext(_sys, options);
            var candidates = new List<Candidate>();

            foreach (var source in DefaultSources(options))
            {
                if (options.DisabledSources.Contains(source.Kind))
                    continue;
                report.SourcesRun.Add(source.Kind);
                try
                {
                    candidates.AddRange(source.Discover(ctx));
                }
                catch (Exception ex)
                {
                    report.SourceErrors.Add(new SourceError(source.Kind, ex.GetType().Name + ": " + ex.Message));
                }
            }

            Build(candidates, options, report);
            report.Rejected.AddRange(ctx.Rejections);
            report.Hints.AddRange(ctx.Hints.Distinct());
            sw.Stop();
            report.Duration = sw.Elapsed;
            return report;
        }

        /// <summary>
        /// Validates a folder or file the player picked by hand (exe, bin folder, game folder, Modules folder or Xbox folder).
        /// Returns a report with one installation, or one rejection explaining why.
        /// </summary>
        public DetectionReport ValidateManualSelection(string path, DetectionOptions options = null)
        {
            options = options ?? new DetectionOptions();
            var report = new DetectionReport();
            report.SourcesRun.Add(DiscoverySource.Manual);
            var sw = Stopwatch.StartNew();
            Build(new[] { new Candidate(path, DiscoverySource.Manual, "chosen by the player") }, options, report);
            report.Duration = sw.Elapsed;
            return report;
        }

        private void Build(IEnumerable<Candidate> candidates, DetectionOptions options, DetectionReport report)
        {
            var byKey = new Dictionary<string, GameInstallation>(StringComparer.Ordinal);
            var cache = new Dictionary<string, RootResolution>(StringComparer.Ordinal);
            var rejectedKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var c in candidates.OrderBy(c => c.Source))
            {
                var pathKey = PathUtil.Key(c.Path);
                if (pathKey.Length == 0)
                    continue;
                if (!cache.TryGetValue(pathKey, out var res))
                {
                    res = _resolver.Resolve(c.Path);
                    cache[pathKey] = res;
                }

                if (!res.Success)
                {
                    if (!c.Claimed)
                        continue;
                    var reason = res.Reason;
                    var message = res.Message;
                    if (c.Source == DiscoverySource.Shortcut && reason == RejectionReason.PathNotFound)
                    {
                        reason = RejectionReason.ShortcutBroken;
                        message = "The shortcut points to a folder that no longer exists (game moved, renamed or uninstalled).";
                    }
                    var rk = pathKey + "|" + c.Source + "|" + reason;
                    if (rejectedKeys.Add(rk))
                        report.Rejected.Add(new RejectedCandidate(PathUtil.Normalize(c.Path), c.Source, c.Detail, reason, message));
                    continue;
                }

                var canonical = _sys.FileSystem.GetCanonicalPath(res.RootPath) ?? res.RootPath;
                var key = PathUtil.Key(canonical);
                if (!byKey.TryGetValue(key, out var inst))
                {
                    // Show the real case ("D:\Program Files (x86)\Steam" rather than the registry's "d:/program files (x86)/steam")
                    // but keep the discovered path when it goes through a junction/symlink: that is the path the player knows.
                    var root = res.RootPath;
                    var exe = res.ExecutablePath;
                    if (PathUtil.Key(canonical) == PathUtil.Key(root))
                    {
                        if (exe != null && exe.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            exe = canonical + exe.Substring(root.Length);
                        root = canonical;
                    }
                    inst = new GameInstallation
                    {
                        RootPath = root,
                        CanonicalRootPath = canonical,
                        ModulesPath = PathUtil.Combine(root, BannerlordConstants.ModulesFolder),
                        ExecutablePath = exe,
                        BinFolderName = res.BinFolderName,
                    };
                    byKey[key] = inst;
                }
                if (!inst.Discoveries.Any(d => d.Source == c.Source && d.Detail == c.Detail))
                    inst.Discoveries.Add(new DiscoveryRecord(c.Source, c.Detail, PathUtil.Normalize(c.Path), c.StoreHint));
            }

            foreach (var inst in byKey.Values)
            {
                ResolveStore(inst);
                ReadVersion(inst);
                CheckWritable(inst, options.WriteProbe);
            }

            report.Installations.AddRange(byKey.Values.OrderBy(i => i.PrimarySource).ThenBy(i => i.RootPath, StringComparer.OrdinalIgnoreCase));

            if (report.Installations.Count > 1)
            {
                foreach (var i in report.Installations)
                    i.Warnings.Add($"Bannerlord is installed {report.Installations.Count} times on this PC; make sure the mod goes into the copy you actually launch.");
            }
        }

        private void ResolveStore(GameInstallation inst)
        {
            var fs = _sys.FileSystem;
            var root = inst.RootPath;
            var markers = new List<KeyValuePair<GameStore, string>>();

            if (PathUtil.ContainsSegment(root, "WindowsApps"))
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Xbox, "inside a WindowsApps folder"));
            if (fs.FileExists(PathUtil.Combine(root, "MicrosoftGame.config")) || fs.FileExists(PathUtil.Combine(root, "appxmanifest.xml")))
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Xbox, "MicrosoftGame.config/appxmanifest.xml in the game folder"));
            if (PathUtil.Key(root).Contains(@"\STEAMAPPS\COMMON\"))
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Steam, @"inside a Steam library (steamapps\common)"));
            if (fs.DirectoryExists(PathUtil.Combine(root, ".egstore")))
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Epic, ".egstore folder in the game folder"));
            if (fs.EnumerateFiles(root, "goggame-*.info").Any())
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Gog, "goggame-*.info in the game folder"));
            if (markers.Count == 0 && string.Equals(inst.BinFolderName, BannerlordConstants.BinXbox, StringComparison.OrdinalIgnoreCase))
                markers.Add(new KeyValuePair<GameStore, string>(GameStore.Xbox, "only the Xbox binaries (Gaming.Desktop.x64_Shipping_Client) contain Bannerlord.exe"));

            var vouched = inst.Discoveries.Where(d => d.StoreHint != GameStore.Unknown).OrderBy(d => d.Source).FirstOrDefault();
            if (vouched != null)
            {
                inst.Store = vouched.StoreHint;
                inst.StoreEvidence = $"{vouched.Source} source: {vouched.Detail}";
                foreach (var m in markers.Where(m => m.Key != vouched.StoreHint))
                    inst.Warnings.Add($"Store markers disagree: {m.Value} suggests {m.Key}, but {vouched.Source} lists this folder.");
            }
            else if (markers.Count > 0)
            {
                inst.Store = markers[0].Key;
                inst.StoreEvidence = "folder markers: " + string.Join("; ", markers.Select(m => m.Value));
            }
            else
            {
                inst.Store = GameStore.Unknown;
                inst.StoreEvidence = "no store lists this folder and it carries no store marker (copied or moved install?)";
            }

            if (PathUtil.ContainsSegment(root, "WindowsApps"))
            {
                inst.IsProtectedLocation = true;
                inst.Warnings.Add("This Xbox install lives in the protected WindowsApps folder, where mods cannot be added. In the Xbox app, move or reinstall the game to a folder such as <drive>:\\XboxGames.");
            }
        }

        private void ReadVersion(GameInstallation inst)
        {
            var fs = _sys.FileSystem;
            var binDirs = new List<string>();
            if (inst.ExecutablePath != null)
                binDirs.Add(PathUtil.GetParent(inst.ExecutablePath));
            binDirs.Add(PathUtil.Combine(inst.RootPath, "bin", BannerlordConstants.BinWin64));
            binDirs.Add(PathUtil.Combine(inst.RootPath, "bin", BannerlordConstants.BinXbox));

            foreach (var dir in binDirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var file = PathUtil.Combine(dir, BannerlordConstants.VersionXml);
                if (!fs.FileExists(file))
                    continue;
                try
                {
                    var v = VersionFiles.ReadGameVersionXml(fs.ReadAllText(file));
                    if (!string.IsNullOrEmpty(v))
                    {
                        inst.GameVersion = v;
                        inst.GameVersionSource = file;
                        return;
                    }
                }
                catch
                {
                    // try the next one
                }
            }

            var native = PathUtil.Combine(inst.RootPath, BannerlordConstants.ModulesFolder, BannerlordConstants.NativeModule, BannerlordConstants.SubModuleXml);
            try
            {
                var v = VersionFiles.ReadSubModuleVersion(fs.ReadAllText(native));
                if (!string.IsNullOrEmpty(v))
                {
                    inst.GameVersion = v;
                    inst.GameVersionSource = native;
                    inst.Warnings.Add("bin\\...\\Version.xml is missing; the version comes from Modules\\Native\\SubModule.xml.");
                    return;
                }
            }
            catch
            {
                // ignore
            }
            inst.Warnings.Add("The game version could not be read.");
        }

        private void CheckWritable(GameInstallation inst, WriteProbeMode mode)
        {
            inst.WriteCheckMode = mode;
            if (mode == WriteProbeMode.None)
            {
                inst.WriteCheckDetail = "not checked";
                return;
            }
            inst.IsWritable = _sys.FileSystem.CheckWriteAccess(inst.ModulesPath, out var detail);
            inst.WriteCheckDetail = detail;
            if (inst.IsWritable == false && !inst.IsProtectedLocation)
                inst.Warnings.Add("Your Windows account cannot write to the Modules folder. The updater never asks for administrator rights: move the game to a folder you own (e.g. a Steam library outside Program Files) or ask the PC owner to grant you write access to Modules.");
        }
    }
}
