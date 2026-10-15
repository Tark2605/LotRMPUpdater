using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Model;
using Lotr.Updater.Core.Json;

namespace Lotr.Updater.Core.Model
{
    /// <summary>Player-facing helpers on the shared manifest model (LotR.Releases).</summary>
    public static class ManifestExtensions
    {
        public static string DisplayName(this ManifestModule m) => string.IsNullOrEmpty(m.Name) ? m.Id : m.Name;

        public static bool IsCore(this ManifestModule m) => string.Equals(m.Role, ModuleRoles.Core, StringComparison.Ordinal);

        public static string NotesText(this ReleaseManifest m, string lang = "en")
        {
            if (m.Notes == null || m.Notes.Count == 0) return null;
            return m.Notes.TryGetValue(lang, out var t) ? t : m.Notes.Values.First();
        }

        public static string GameMin(this ReleaseManifest m) => m.Game?.MinVersion;

        public static string GameMax(this ReleaseManifest m) => m.Game?.MaxVersion;

        public static DateTime? PublishedUtc(this ReleaseManifest m) =>
            DateTime.TryParseExact(m.PublishedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
    }

    /// <summary>Updater-side rules on top of ManifestValidator: things only the installer cares about.</summary>
    public static class UpdaterManifestRules
    {
        private static readonly string[] OfficialFolders = { "Native", "SandBoxCore", "Sandbox", "Multiplayer", "CustomBattle", "StoryMode", "BirthAndDeath", "NavalDLC" };

        /// <summary>Null when acceptable, else the reason the release is refused.</summary>
        public static string Check(ReleaseManifest m)
        {
            foreach (var mod in m.Modules)
            {
                if (OfficialFolders.Any(o => string.Equals(o, mod.Folder, StringComparison.OrdinalIgnoreCase)))
                    return $"Module {mod.Id} targets an official game folder ({mod.Folder}).";
                if (!RelativePath.IsSafeSegment(mod.Folder))
                    return $"Module {mod.Id} has an unsafe folder name.";
                foreach (var f in mod.Files)
                    if (InstallLayout.IsStagingPath(f.Path)) return $"Module {mod.Id} ships a file inside the updater's working folder ({f.Path}).";
            }
            var dup = m.Modules.GroupBy(x => x.Folder, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            return dup != null ? "Two modules use the folder " + dup.Key + "." : null;
        }
    }

    public sealed class SecurityScan
    {
        public string Kind { get; set; }
        public string Sha256 { get; set; }
        public string Url { get; set; }
        public string Verdict { get; set; }
        public string ScannedAt { get; set; }
        public long Detections { get; set; }
        public long Engines { get; set; }

        /// <summary>Reads files/releases/&lt;id&gt;/security.json (unsigned sidecar, CONTRACTS §8): { virustotal: [...], hybridAnalysis: [...] }.</summary>
        public static List<SecurityScan> Parse(JsonObject sec)
        {
            var list = new List<SecurityScan>();
            if (sec == null) return list;
            foreach (var v in sec.Objs("virustotal"))
                list.Add(new SecurityScan
                {
                    Kind = "VirusTotal", Sha256 = v.Str("sha256"), Url = v.Str("url"), Detections = v.Long("detections"), Engines = v.Long("engines"),
                    Verdict = v.Long("detections") == 0 ? $"clean ({v.Long("engines")} engines)" : $"{v.Long("detections")}/{v.Long("engines")} engines flagged it",
                    ScannedAt = v.Str("scannedAt"),
                });
            foreach (var v in sec.Objs("hybridAnalysis"))
                list.Add(new SecurityScan { Kind = "Hybrid Analysis", Sha256 = v.Str("sha256"), Url = v.Str("url"), Verdict = v.Str("verdict") });
            return list;
        }
    }

    public sealed class ManifestException : Exception
    {
        public ManifestException(string message) : base(message) { }
    }

    /// <summary>Names the updater uses inside a module folder.</summary>
    public static class InstallLayout
    {
        /// <summary>
        /// Staging lives INSIDE &lt;Modules&gt;\&lt;folder&gt; (same volume, so the swap is a rename; and CONTRACTS §2 forbids writing
        /// outside the module folder). The game/launcher only read SubModule.xml at the module root, so this subfolder is invisible
        /// to them. It is removed once an update is committed or rolled back.
        /// </summary>
        public const string StagingFolderName = ".lotr-staging";

        public static bool IsStagingPath(string relPath) =>
            relPath.StartsWith(StagingFolderName + "/", StringComparison.OrdinalIgnoreCase) || string.Equals(relPath, StagingFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
