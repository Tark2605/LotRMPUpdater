using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Planning;

namespace Lotr.Updater.Core.Install
{
    /// <summary>
    /// Swap journal kept in &lt;Modules&gt;\&lt;folder&gt;\.lotr-staging\journal.json. Written once before the first rename, so an
    /// interrupted swap (crash, power loss, locked file) can always be undone. Each step is idempotent to roll back because its
    /// state can be read from disk (where the file is: target, staging\new or staging\old).
    /// Layout: staging\new\&lt;path&gt; = new files, staging\old\&lt;path&gt; = files moved out of the way.
    /// </summary>
    public sealed class SwapJournal
    {
        public const string Swapping = "swapping";
        public const string Committed = "committed";

        public string ModuleFolder { get; set; }
        public string ReleaseId { get; set; }
        public string BackupDir { get; set; }
        public string State { get; set; } = Swapping;
        public List<(FileActionKind Kind, string Path)> Ops { get; } = new List<(FileActionKind, string)>();

        public static string StagingDir(string moduleFolder) => Path.Combine(moduleFolder, InstallLayout.StagingFolderName);
        public static string JournalFile(string moduleFolder) => Path.Combine(StagingDir(moduleFolder), "journal.json");
        public string NewPath(string rel) => InstallPlanner.ToLocal(Path.Combine(StagingDir(ModuleFolder), "new"), rel);
        public string OldPath(string rel) => InstallPlanner.ToLocal(Path.Combine(StagingDir(ModuleFolder), "old"), rel);
        public string TargetPath(string rel) => InstallPlanner.ToLocal(ModuleFolder, rel);

        public void Save(IFileSystem fs)
        {
            var ops = Ops.Select(o => (object)new JsonObject { ["kind"] = o.Kind.ToString(), ["path"] = o.Path }).ToList();
            var json = Json.Json.Write(new JsonObject
            {
                ["state"] = State,
                ["releaseId"] = ReleaseId,
                ["backupDir"] = BackupDir,
                ["ops"] = ops,
            });
            var file = JournalFile(ModuleFolder);
            fs.CreateDirectory(Path.GetDirectoryName(file));
            var tmp = file + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var s = fs.OpenWrite(tmp, false))
            {
                s.Write(bytes, 0, bytes.Length);
                (s as FileStream)?.Flush(true);
            }
            if (fs.FileExists(file)) fs.DeleteFile(file);
            fs.MoveFile(tmp, file);
        }

        public static SwapJournal Load(IFileSystem fs, string moduleFolder)
        {
            var file = JournalFile(moduleFolder);
            if (!fs.FileExists(file)) return null;
            string text;
            using (var s = fs.OpenRead(file))
            using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
            var o = Json.Json.ParseObject(text);
            var j = new SwapJournal { ModuleFolder = moduleFolder, State = o.Str("state"), ReleaseId = o.Str("releaseId"), BackupDir = o.Str("backupDir") };
            foreach (var op in o.Objs("ops"))
            {
                var path = op.Str("path");
                if (!LotR.Releases.Model.RelativePath.IsSafe(path)) throw new InvalidDataException("Unsafe path in swap journal: " + path);
                j.Ops.Add(((FileActionKind)Enum.Parse(typeof(FileActionKind), op.Str("kind")), path));
            }
            return j;
        }
    }
}
