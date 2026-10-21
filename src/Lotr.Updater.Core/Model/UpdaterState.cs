using System;
using System.IO;
using System.Text;
using Lotr.Updater.Core.Json;

namespace Lotr.Updater.Core.Model
{
    /// <summary>%LOCALAPPDATA%\LotRMPUpdater layout (CONTRACTS §6). Everything the app writes outside the module folders is here.</summary>
    public sealed class UpdaterPaths
    {
        public UpdaterPaths(string home)
        {
            Home = Path.GetFullPath(home);
        }

        /// <summary>Default home, or LOTR_UPDATER_HOME when set (tests, dev runs against a fake release).</summary>
        public static UpdaterPaths Default()
        {
            var env = Environment.GetEnvironmentVariable("LOTR_UPDATER_HOME");
            if (!string.IsNullOrEmpty(env)) return new UpdaterPaths(env);
            return new UpdaterPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LotRMPUpdater"));
        }

        public string Home { get; }
        public string AppDir => Path.Combine(Home, "app");
        /// <summary>The installed copy of the updater (what the Start menu and the Startup shortcut run).</summary>
        public string InstalledExe => Path.Combine(AppDir, "LotRMPUpdater.exe");
        /// <summary>The previous updater, kept after a self-update for rollback.</summary>
        public string PreviousExe => Path.Combine(AppDir, "LotRMPUpdater.old.exe");
        /// <summary>Downloaded, verified new updater waiting for the next start.</summary>
        public string UpdateDir => Path.Combine(AppDir, "update");
        public string PendingUpdateFile => Path.Combine(UpdateDir, "pending.json");
        public string ObjectCache => Path.Combine(Home, "cache", "objects");
        public string HashCacheFile => Path.Combine(Home, "cache", "hashes.json");
        public string LogDir => Path.Combine(Home, "logs");
        public string StateFile => Path.Combine(Home, "state.json");
        public string BackupRoot => Path.Combine(Home, "backup");
        public string BackupDir(string releaseId) => Path.Combine(BackupRoot, releaseId);
        public string CachedObjectPath(string objectName) => Path.Combine(ObjectCache, objectName.Substring(0, 2), objectName);
    }

    /// <summary>state.json: settings + what is installed. Small, rewritten atomically.</summary>
    public sealed class UpdaterState
    {
        /// <summary>Random GUID, lowercase, no braces (lotr-services validates it). The only identifier ever sent.</summary>
        public string InstallId { get; set; } = Guid.NewGuid().ToString("D");
        public string GamePath { get; set; }
        public string Channel { get; set; } = "stable";
        public string InstalledReleaseId { get; set; }
        public string PreviousReleaseId { get; set; }
        public string InstalledAt { get; set; }
        public bool PreDownload { get; set; } = true;
        /// <summary>Upper bound of the random delay before a background pre-download (CONTRACTS/PLAN bandwidth: 0-6 h).</summary>
        public int PreDownloadMaxJitterMinutes { get; set; } = 360;
        public string PendingReleaseId { get; set; }
        public string PendingSeenAtUtc { get; set; }
        public int PendingJitterMinutes { get; set; }
        public bool PendingDownloaded { get; set; }
        public string LastCheckUtc { get; set; }
        /// <summary>Release id the player explicitly accepted as a rollback (CONTRACTS §8).</summary>
        public string AllowRollbackTo { get; set; }
        /// <summary>Updater version the player rolled back from: never offered again (a newer one is).</summary>
        public string SkippedUpdaterVersion { get; set; }
        /// <summary>Opt-in, off by default: after each successful update, tick our modules in the launcher's Multiplayer list
        /// (LauncherSelection, with a backup). Never done without this setting or a click.</summary>
        public bool AutoTickLauncherMods { get; set; }

        public static UpdaterState Load(string file)
        {
            try
            {
                if (!File.Exists(file)) return new UpdaterState();
                var o = Json.Json.ParseObject(File.ReadAllText(file, Encoding.UTF8));
                var s = new UpdaterState
                {
                    InstallId = Guid.TryParse(o.Str("installId"), out var g) ? g.ToString("D") : Guid.NewGuid().ToString("D"),
                    GamePath = o.Str("gamePath"),
                    Channel = o.Str("channel") ?? "stable",
                    InstalledReleaseId = o.Str("installedReleaseId"),
                    PreviousReleaseId = o.Str("previousReleaseId"),
                    InstalledAt = o.Str("installedAt"),
                    PreDownload = o.Bool("preDownload", true),
                    PreDownloadMaxJitterMinutes = (int)o.Long("preDownloadMaxJitterMinutes", 360),
                    PendingReleaseId = o.Str("pendingReleaseId"),
                    PendingSeenAtUtc = o.Str("pendingSeenAtUtc"),
                    PendingJitterMinutes = (int)o.Long("pendingJitterMinutes"),
                    PendingDownloaded = o.Bool("pendingDownloaded"),
                    LastCheckUtc = o.Str("lastCheckUtc"),
                    AllowRollbackTo = o.Str("allowRollbackTo"),
                    SkippedUpdaterVersion = o.Str("skippedUpdaterVersion"),
                    AutoTickLauncherMods = o.Bool("autoTickLauncherMods"),
                };
                return s;
            }
            catch (Exception)
            {
                // A damaged state file must never brick the app: start fresh (the install itself is re-verified from disk).
                return new UpdaterState();
            }
        }

        public void Save(string file)
        {
            var o = new JsonObject
            {
                ["installId"] = InstallId,
                ["gamePath"] = GamePath,
                ["channel"] = Channel,
                ["installedReleaseId"] = InstalledReleaseId,
                ["previousReleaseId"] = PreviousReleaseId,
                ["installedAt"] = InstalledAt,
                ["preDownload"] = PreDownload,
                ["preDownloadMaxJitterMinutes"] = (long)PreDownloadMaxJitterMinutes,
                ["pendingReleaseId"] = PendingReleaseId,
                ["pendingSeenAtUtc"] = PendingSeenAtUtc,
                ["pendingJitterMinutes"] = (long)PendingJitterMinutes,
                ["pendingDownloaded"] = PendingDownloaded,
                ["lastCheckUtc"] = LastCheckUtc,
                ["allowRollbackTo"] = AllowRollbackTo,
                ["skippedUpdaterVersion"] = SkippedUpdaterVersion,
                ["autoTickLauncherMods"] = AutoTickLauncherMods,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, Json.Json.Write(o), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
    }
}
