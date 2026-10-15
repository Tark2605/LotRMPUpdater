using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LotR.Releases.Chunking;
using LotR.Releases.Hashing;
using LotR.Releases.Model;

namespace LotR.Releases.Install
{
    /// <summary>Content the player already has locally: installed files with a known hash, cached objects, chunks.</summary>
    public sealed class LocalContent
    {
        private readonly Dictionary<string, string> files = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, LocalChunk> chunks = new Dictionary<string, LocalChunk>(StringComparer.Ordinal);
        private readonly HashSet<string> cachedObjects = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Registers every installed file whose hash was verified (Full check).</summary>
        public void AddInstalled(InstallCheck check)
        {
            foreach (var f in check.Files.Where(f => f.ActualSha256 != null))
            {
                if (!files.ContainsKey(f.ActualSha256))
                {
                    files[f.ActualSha256] = f.FullPath;
                }
            }
        }

        public void AddFile(string sha256, string fullPath)
        {
            if (!files.ContainsKey(sha256))
            {
                files[sha256] = fullPath;
            }
        }

        /// <summary>An object already decoded in the updater cache (cache\objects).</summary>
        public void AddCachedObject(string sha256) => cachedObjects.Add(sha256);

        /// <summary>Chunks of a local file, from the chunk index kept since the last install (no re-read needed).</summary>
        public void AddChunks(string fullPath, ChunkIndex index)
        {
            long offset = 0;
            foreach (var c in index.Chunks)
            {
                if (!chunks.ContainsKey(c.Sha256))
                {
                    chunks[c.Sha256] = new LocalChunk { FullPath = fullPath, Offset = offset, Size = c.Size, Sha256 = c.Sha256 };
                }

                offset += c.Size;
            }
        }

        /// <summary>Re-chunks a local file with the contract FastCDC (≈ 1-2 GB/s) when no index was kept.</summary>
        public void AddChunksByScanning(string fullPath)
        {
            using (var fs = Sha256Util.OpenSequential(fullPath))
            {
                FastCdc.Contract.Split(fs, (seg, offset) =>
                {
                    string sha = Sha256Util.Of(seg);
                    if (!chunks.ContainsKey(sha))
                    {
                        chunks[sha] = new LocalChunk { FullPath = fullPath, Offset = offset, Size = seg.Count, Sha256 = sha };
                    }
                });
            }
        }

        public bool TryGetFile(string sha256, out string fullPath) => files.TryGetValue(sha256, out fullPath);

        public bool HasCachedObject(string sha256) => cachedObjects.Contains(sha256);

        public bool TryGetChunk(string sha256, out LocalChunk chunk) => chunks.TryGetValue(sha256, out chunk);
    }

    public sealed class LocalChunk
    {
        public string FullPath { get; set; }

        public long Offset { get; set; }

        public long Size { get; set; }

        public string Sha256 { get; set; }
    }

    public enum FileAction
    {
        /// <summary>Already correct on disk.</summary>
        Keep,

        /// <summary>Copy from another local file with the same content (moved/duplicated file).</summary>
        CopyLocal,

        /// <summary>Decode from the updater cache.</summary>
        FromCache,

        /// <summary>Download the whole-file object.</summary>
        Download,

        /// <summary>Chunked file: assemble from local chunks plus downloaded ones.</summary>
        Rebuild,

        /// <summary>Chunked file whose index object must be fetched first; re-plan afterwards.</summary>
        NeedsIndex,
    }

    public sealed class PlannedFile
    {
        public string ModuleFolder { get; set; }

        public ManifestFile Target { get; set; }

        public FileAction Action { get; set; }

        public string LocalSource { get; set; }

        /// <summary>For Rebuild: chunks in file order, each either local or to download.</summary>
        public List<(ChunkRef Chunk, LocalChunk Local)> Chunks { get; set; }
    }

    public sealed class PlannedDownload
    {
        public string Object { get; set; }

        public long ObjectSize { get; set; }

        /// <summary>Uncompressed size (for the decode check). -1 for an index whose size is not known yet.</summary>
        public long Size { get; set; }

        public string Kind { get; set; }
    }

    public sealed class DownloadPlan
    {
        public List<PlannedFile> Files { get; } = new List<PlannedFile>();

        /// <summary>Files of the previously installed release that the target no longer has (module folder, path).</summary>
        public List<(string ModuleFolder, string Path)> Deletes { get; } = new List<(string, string)>();

        /// <summary>Distinct objects to download.</summary>
        public List<PlannedDownload> Downloads { get; } = new List<PlannedDownload>();

        public long DownloadBytes => Downloads.Sum(d => d.ObjectSize);

        public long LocalReuseBytes { get; set; }

        public bool NeedsIndexes => Files.Any(f => f.Action == FileAction.NeedsIndex);

        public bool NothingToDo => Downloads.Count == 0 && Deletes.Count == 0 && Files.All(f => f.Action == FileAction.Keep);
    }

    /// <summary>
    /// Computes what the updater must do to move an installed state to a target release: keep, copy locally, take
    /// from cache, download (whole object), or rebuild a chunked file from local + missing chunks. Pure computation,
    /// no I/O except what <see cref="LocalContent"/> already gathered.
    /// </summary>
    public static class DownloadPlanner
    {
        /// <param name="target">Verified target manifest.</param>
        /// <param name="installed">Install checks by module id (missing module = nothing installed).</param>
        /// <param name="installedManifest">Manifest of the installed release (for deletions), or null.</param>
        /// <param name="chunkIndexes">Returns a downloaded+verified chunk index by object name, or null if not fetched yet.</param>
        public static DownloadPlan Compute(ReleaseManifest target, IReadOnlyDictionary<string, InstallCheck> installed, ReleaseManifest installedManifest, Func<string, ChunkIndex> chunkIndexes, LocalContent local)
        {
            var plan = new DownloadPlan();
            var downloads = new Dictionary<string, PlannedDownload>(StringComparer.Ordinal);
            void Need(string obj, long objectSize, long size, string kind)
            {
                if (!downloads.ContainsKey(obj))
                {
                    var d = new PlannedDownload { Object = obj, ObjectSize = objectSize, Size = size, Kind = kind };
                    downloads[obj] = d;
                    plan.Downloads.Add(d);
                }
            }

            foreach (var mod in target.Modules)
            {
                InstallCheck check = null;
                installed?.TryGetValue(mod.Id, out check);
                var state = check?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, InstalledFile>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in mod.Files)
                {
                    var pf = new PlannedFile { ModuleFolder = mod.Folder, Target = f };
                    plan.Files.Add(pf);
                    // Keep when the hash was checked and matches, or (size-only check) when the installed release
                    // recorded the same content for this path.
                    string recorded = installedManifest?.FindModule(mod.Id)?.FindFile(f.Path)?.Sha256;
                    if (state.TryGetValue(f.Path, out var cur) && cur.State == InstalledFileState.Ok
                        && (cur.ActualSha256 != null ? cur.ActualSha256 == f.Sha256 : recorded == f.Sha256)
                        && string.Equals(cur.Path, f.Path, StringComparison.Ordinal))
                    {
                        pf.Action = FileAction.Keep;
                        continue;
                    }

                    if (local.TryGetFile(f.Sha256, out string src))
                    {
                        pf.Action = FileAction.CopyLocal;
                        pf.LocalSource = src;
                        plan.LocalReuseBytes += f.Size;
                        continue;
                    }

                    if (local.HasCachedObject(f.Sha256))
                    {
                        pf.Action = FileAction.FromCache;
                        continue;
                    }

                    if (!f.Chunked)
                    {
                        pf.Action = FileAction.Download;
                        Need(f.Object, f.ObjectSize, f.Size, "file");
                        continue;
                    }

                    var index = chunkIndexes?.Invoke(f.ChunkIndex);
                    if (index == null)
                    {
                        pf.Action = FileAction.NeedsIndex;
                        Need(f.ChunkIndex, 0, -1, "index");
                        continue;
                    }

                    if (index.Sha256 != f.Sha256 || index.Size != f.Size)
                    {
                        throw new InvalidDataException("chunk index " + f.ChunkIndex + " does not describe " + f.Path);
                    }

                    pf.Action = FileAction.Rebuild;
                    pf.Chunks = new List<(ChunkRef, LocalChunk)>();
                    foreach (var c in index.Chunks)
                    {
                        if (local.TryGetChunk(c.Sha256, out var lc))
                        {
                            pf.Chunks.Add((c, lc));
                            plan.LocalReuseBytes += c.Size;
                        }
                        else
                        {
                            pf.Chunks.Add((c, null));
                            Need(c.Object, c.ObjectSize, c.Size, "chunk");
                        }
                    }
                }

                if (installedManifest?.FindModule(mod.Id) is ManifestModule oldMod)
                {
                    var keep = new HashSet<string>(mod.Files.Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                    foreach (var old in oldMod.Files.Where(o => !keep.Contains(o.Path)))
                    {
                        plan.Deletes.Add((oldMod.Folder, old.Path));
                    }
                }
            }

            return plan;
        }
    }
}
