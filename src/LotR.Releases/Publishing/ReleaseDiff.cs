using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Json;
using LotR.Releases.Model;

namespace LotR.Releases.Publishing
{
    public sealed class FileChange
    {
        public string Path { get; set; }

        public long OldSize { get; set; }

        public long NewSize { get; set; }
    }

    public sealed class ModuleDiff
    {
        public string Id { get; set; }

        public string Folder { get; set; }

        public string OldVersion { get; set; }

        public string NewVersion { get; set; }

        /// <summary>added | removed | changed | unchanged</summary>
        public string Status { get; set; }

        public List<FileChange> Added { get; } = new List<FileChange>();

        public List<FileChange> Changed { get; } = new List<FileChange>();

        public List<FileChange> Removed { get; } = new List<FileChange>();

        public int Unchanged { get; set; }

        public int FileCount { get; set; }

        public long TotalSize { get; set; }
    }

    /// <summary>
    /// What changes between the current release of a channel and a candidate. "Download" is what a player on the
    /// current release fetches: the stored size of every object (file, chunk, chunk index) of the candidate that the
    /// current release does not have. Chunked files therefore only count their missing chunks.
    /// </summary>
    public sealed class ReleaseDiff
    {
        public string FromReleaseId { get; set; }

        public List<ModuleDiff> Modules { get; } = new List<ModuleDiff>();

        public int NewObjects { get; set; }

        public long UpdateDownloadBytes { get; set; }

        public long FreshInstallBytes { get; set; }

        public long TotalSize { get; set; }

        /// <summary>True when some new objects were counted at their raw size (fast plan without compression).</summary>
        public bool Estimated { get; set; }

        public static ReleaseDiff Compute(ReleaseManifest previous, IReadOnlyDictionary<string, ObjectRef> previousObjects, ReleaseManifest next, IReadOnlyDictionary<string, ObjectRef> nextObjects)
        {
            var d = new ReleaseDiff { FromReleaseId = previous?.ReleaseId };
            foreach (var mod in next.Modules)
            {
                var old = previous?.FindModule(mod.Id);
                var md = new ModuleDiff
                {
                    Id = mod.Id,
                    Folder = mod.Folder,
                    OldVersion = old?.Version,
                    NewVersion = mod.Version,
                    FileCount = mod.Files.Count,
                    TotalSize = mod.TotalSize,
                };
                var oldFiles = (old?.Files ?? new List<ManifestFile>()).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                foreach (var f in mod.Files)
                {
                    if (!oldFiles.TryGetValue(f.Path, out var of))
                    {
                        md.Added.Add(new FileChange { Path = f.Path, NewSize = f.Size });
                    }
                    else if (of.Sha256 != f.Sha256 || !string.Equals(of.Path, f.Path, StringComparison.Ordinal))
                    {
                        md.Changed.Add(new FileChange { Path = f.Path, OldSize = of.Size, NewSize = f.Size });
                    }
                    else
                    {
                        md.Unchanged++;
                    }

                    oldFiles.Remove(f.Path);
                }

                md.Removed.AddRange(oldFiles.Values.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => new FileChange { Path = f.Path, OldSize = f.Size }));
                md.Status = old == null ? "added" : (md.Added.Count + md.Changed.Count + md.Removed.Count == 0 && old.Version == mod.Version ? "unchanged" : "changed");
                d.Modules.Add(md);
            }

            if (previous != null)
            {
                foreach (var gone in previous.Modules.Where(pm => next.FindModule(pm.Id) == null))
                {
                    var md = new ModuleDiff { Id = gone.Id, Folder = gone.Folder, OldVersion = gone.Version, Status = "removed" };
                    md.Removed.AddRange(gone.Files.Select(f => new FileChange { Path = f.Path, OldSize = f.Size }));
                    d.Modules.Add(md);
                }
            }

            foreach (var o in nextObjects.Values)
            {
                d.FreshInstallBytes += o.ObjectSize;
                d.Estimated |= o.Estimated;
                if (previousObjects == null || !previousObjects.ContainsKey(o.Sha256))
                {
                    d.NewObjects++;
                    d.UpdateDownloadBytes += o.ObjectSize;
                }
            }

            d.TotalSize = next.TotalSize;
            return d;
        }

        public JsonObject ToJson(int maxListed = 200)
        {
            JsonArray List(IEnumerable<FileChange> items) => new JsonArray(items.Take(maxListed).Select(c => (JsonNode)new JsonObject { ["path"] = c.Path, ["oldSize"] = c.OldSize, ["newSize"] = c.NewSize }));
            return new JsonObject
            {
                ["from"] = FromReleaseId,
                ["totalSize"] = TotalSize,
                ["freshInstallBytes"] = FreshInstallBytes,
                ["updateDownloadBytes"] = UpdateDownloadBytes,
                ["newObjects"] = NewObjects,
                ["estimated"] = Estimated,
                ["modules"] = new JsonArray(Modules.Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.Id,
                    ["folder"] = m.Folder,
                    ["status"] = m.Status,
                    ["oldVersion"] = m.OldVersion,
                    ["newVersion"] = m.NewVersion,
                    ["files"] = m.FileCount,
                    ["size"] = m.TotalSize,
                    ["unchanged"] = m.Unchanged,
                    ["addedCount"] = m.Added.Count,
                    ["changedCount"] = m.Changed.Count,
                    ["removedCount"] = m.Removed.Count,
                    ["added"] = List(m.Added),
                    ["changed"] = List(m.Changed),
                    ["removed"] = List(m.Removed),
                })),
            };
        }
    }
}
