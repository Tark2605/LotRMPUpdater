using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.GameDetection;
using Lotr.Updater.Core.IO;
using LotR.Releases.Model;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Core.Game
{
    /// <summary>The Bannerlord installation the updater works on.</summary>
    public sealed class GameTarget
    {
        public string Root { get; set; }
        public string ModulesPath { get; set; }
        public GameStore Store { get; set; }
        public string GameVersion { get; set; }
        public bool? Writable { get; set; }
        public bool IsProtectedLocation { get; set; }
        public string BinFolderName { get; set; }
        public string Evidence { get; set; }
        public List<string> Warnings { get; } = new List<string>();

        public string StoreLabel
        {
            get
            {
                switch (Store)
                {
                    case GameStore.Steam: return "Steam";
                    case GameStore.Epic: return "Epic Games";
                    case GameStore.Gog: return "GOG";
                    case GameStore.Xbox: return "Xbox PC app";
                    default: return "Unknown store";
                }
            }
        }

        public static GameTarget From(GameInstall i) => new GameTarget
        {
            Root = i.Root,
            ModulesPath = i.ModulesPath,
            Store = i.Store,
            GameVersion = i.GameVersion,
            Writable = i.Writable,
            IsProtectedLocation = i.Details.IsProtectedLocation,
            BinFolderName = i.Details.BinFolderName,
            Evidence = i.Details.StoreEvidence,
        }.WithWarnings(i.Details.Warnings);

        private GameTarget WithWarnings(IEnumerable<string> w)
        {
            Warnings.AddRange(w ?? Enumerable.Empty<string>());
            return this;
        }
    }

    public sealed class GameSearch
    {
        public List<GameTarget> Found { get; } = new List<GameTarget>();
        public string Message { get; set; }
        /// <summary>Full detection report (JSON, user name masked) for the support log.</summary>
        public string ReportForLog { get; set; }
    }

    public interface IGameLocator
    {
        GameSearch FindAll();
        GameSearch Validate(string path);
    }

    /// <summary>Adapter to the detection library (step 1, LotR.GameDetection).</summary>
    public sealed class DetectionGameLocator : IGameLocator
    {
        public GameSearch FindAll() => Map(GameLocator.FindAll(new DetectionOptions()));

        public GameSearch Validate(string path) => Map(GameLocator.Validate(path, new DetectionOptions()));

        private static GameSearch Map(GameLocateResult r)
        {
            var s = new GameSearch { Message = r.Message };
            try { s.ReportForLog = ReportJson.Serialize(r.Report, maskUserName: true); } catch (Exception ex) { s.ReportForLog = "(report unavailable: " + ex.Message + ")"; }
            foreach (var i in r.Installs) s.Found.Add(GameTarget.From(i));
            return s;
        }
    }

    /// <summary>"v1.2.12" style comparisons (CONTRACTS §2 game.minVersion / maxVersion).</summary>
    public static class GameVersions
    {
        public static int[] Parse(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var m = Regex.Match(v, @"(\d+(?:\.\d+)*)");
            if (!m.Success) return null;
            return m.Groups[1].Value.Split('.').Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        }

        /// <summary>Compares on the components the bound specifies ("v1.2" matches every v1.2.x).</summary>
        public static int CompareToBound(int[] version, int[] bound)
        {
            for (int i = 0; i < bound.Length; i++)
            {
                int a = i < version.Length ? version[i] : 0;
                if (a != bound[i]) return a.CompareTo(bound[i]);
            }
            return 0;
        }

        /// <summary>Null when compatible or unknown; otherwise a sentence for the player.</summary>
        public static string Check(string gameVersion, string min, string max)
        {
            var v = Parse(gameVersion);
            if (v == null) return null;
            var lo = Parse(min);
            var hi = Parse(max);
            if (lo != null && CompareToBound(v, lo) < 0)
                return $"Your game is {gameVersion}, this release needs {min} or newer. Update Bannerlord in your store first.";
            if (hi != null && CompareToBound(v, hi) > 0)
                return $"Your game is {gameVersion}, this release was made for {max} at most. The mod may not load until the team publishes an update (or switch the game to the older branch in your store).";
            return null;
        }
    }

    public sealed class RunningProcessInfo
    {
        public string Name { get; set; }
        public int Pid { get; set; }
        public override string ToString() => Name + " (PID " + Pid + ")";
    }

    public interface IProcessProbe
    {
        IReadOnlyList<RunningProcessInfo> RunningGameProcesses();
    }

    public sealed class SystemProcessProbe : IProcessProbe
    {
        public IReadOnlyList<RunningProcessInfo> RunningGameProcesses()
        {
            var list = new List<RunningProcessInfo>();
            foreach (var n in BannerlordConstants.ProcessNames)
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); } catch (InvalidOperationException) { continue; }
                foreach (var p in ps)
                {
                    list.Add(new RunningProcessInfo { Name = n + ".exe", Pid = p.Id });
                    p.Dispose();
                }
            }
            return list;
        }
    }

    /// <summary>Names the processes that hold files open (Windows Restart Manager in the WPF layer).</summary>
    public interface ILockingProcessFinder
    {
        IReadOnlyList<RunningProcessInfo> WhoLocks(IReadOnlyList<string> paths);
    }

    public sealed class NoLockingProcessFinder : ILockingProcessFinder
    {
        public IReadOnlyList<RunningProcessInfo> WhoLocks(IReadOnlyList<string> paths) => new List<RunningProcessInfo>();
    }

    public sealed class DuplicateModule
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Where { get; set; }
        public override string ToString() => $"{Id} also in {Path} ({Where})";
    }

    /// <summary>Finds other copies of our modules (same SubModule Id in another folder, or a Steam Workshop subscription):
    /// the launcher would show two entries and may load the wrong one.</summary>
    public static class DuplicateModuleScanner
    {
        private static readonly Regex IdRx = new Regex("<Id\\s+value\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string ReadModuleId(IFileSystem fs, string moduleFolder)
        {
            var xml = Path.Combine(moduleFolder, BannerlordConstants.SubModuleXml);
            if (!fs.FileExists(xml)) return null;
            try
            {
                using (var s = fs.OpenRead(xml))
                using (var r = new StreamReader(s))
                {
                    var m = IdRx.Match(r.ReadToEnd());
                    return m.Success ? m.Groups[1].Value : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static List<DuplicateModule> Scan(IFileSystem fs, GameTarget game, ReleaseManifest manifest)
        {
            var result = new List<DuplicateModule>();
            var ours = manifest.Modules.ToDictionary(m => m.Id, m => m.Folder, StringComparer.OrdinalIgnoreCase);
            void Check(string folder, string where)
            {
                var id = ReadModuleId(fs, folder);
                if (id == null || !ours.TryGetValue(id, out var expectedFolder)) return;
                bool isTarget = where == "Modules" && string.Equals(Path.GetFileName(folder), expectedFolder, StringComparison.OrdinalIgnoreCase);
                if (!isTarget) result.Add(new DuplicateModule { Id = id, Path = folder, Where = where });
            }
            foreach (var d in SafeDirs(fs, game.ModulesPath)) Check(d, "Modules");
            if (game.Store == GameStore.Steam && game.Root != null)
            {
                // <library>\steamapps\common\<game>  →  <library>\steamapps\workshop\content\261550\<item>
                var steamapps = Path.GetDirectoryName(Path.GetDirectoryName(game.Root.TrimEnd('\\', '/')));
                if (steamapps != null)
                {
                    var ws = Path.Combine(steamapps, "workshop", "content", BannerlordConstants.SteamAppId);
                    foreach (var d in SafeDirs(fs, ws)) Check(d, "Steam Workshop item " + Path.GetFileName(d));
                }
            }
            return result;
        }

        private static IEnumerable<string> SafeDirs(IFileSystem fs, string dir)
        {
            try { return fs.EnumerateDirectories(dir).ToList(); }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }
    }
}
