using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Chunking;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Modules;
using LotR.Releases.Transport;

namespace LotR.Releases.Publishing
{
    public enum ProcessMode
    {
        /// <summary>Hash and chunk only; new objects are counted at their raw size (upper bound). Nothing written.</summary>
        PlanFast,

        /// <summary>Also compress new objects in memory to get exact download sizes. Nothing written.</summary>
        PlanExact,

        /// <summary>Hash, compress and write new objects into the staging release.</summary>
        Publish,
    }

    public sealed class ModuleSource
    {
        public string Folder { get; set; }

        public string Role { get; set; }

        public string WorkshopItem { get; set; }

        public List<string> Excludes { get; set; } = new List<string>();
    }

    /// <summary>One object referenced by a release (file object, chunk, or chunk index).</summary>
    public sealed class ObjectRef
    {
        public string Sha256 { get; set; }

        /// <summary>Null when not known (fast plan of a new object).</summary>
        public string Ext { get; set; }

        /// <summary>Stored size, or the raw size when <see cref="Estimated"/>.</summary>
        public long ObjectSize { get; set; }

        public bool Estimated { get; set; }

        /// <summary>file | chunk | index</summary>
        public string Kind { get; set; }
    }

    public sealed class ProcessProgress
    {
        public string Phase { get; set; }

        public int FilesDone { get; set; }

        public int FilesTotal { get; set; }

        public long BytesDone { get; set; }

        public long BytesTotal { get; set; }

        public string Current { get; set; }
    }

    public sealed class ProcessedModule
    {
        public ManifestModule Module { get; set; }

        /// <summary>Every object the module needs, by sha (distinct).</summary>
        public Dictionary<string, ObjectRef> Objects { get; } = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);

        public List<string> Warnings { get; } = new List<string>();

        public long BytesHashed { get; set; }

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Reads one module folder into a manifest module: SubModule.xml identity, scenes, then every file hashed in
    /// parallel (largest first). Files under 16 MiB are one object; bigger ones are cut with FastCDC and get a chunk
    /// index object. New objects are compressed with the 3 % rule and written through the <see cref="ObjectStore"/>.
    /// </summary>
    public static class ModuleProcessor
    {
        public static ProcessedModule Process(ModuleSource source, ObjectStore store, ProcessMode mode, int threads, IProgress<ProcessProgress> progress = null, CancellationToken cancel = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new ProcessedModule();
            string folder = Path.GetFullPath(source.Folder);
            string subModulePath = Path.Combine(folder, "SubModule.xml");
            if (!File.Exists(subModulePath))
            {
                throw new FileNotFoundException("No SubModule.xml in " + folder + " (is this a module folder?)", subModulePath);
            }

            var sub = SubModuleReader.ReadFile(subModulePath);
            result.Warnings.AddRange(sub.Warnings.Select(w => "SubModule.xml: " + w));
            var scenes = SceneReader.ReadModule(folder, result.Warnings);
            var files = ModuleScanner.Scan(folder, source.Excludes, result.Warnings);

            var module = new ManifestModule
            {
                Id = sub.Id,
                Name = sub.Name,
                Version = sub.Version,
                Folder = Path.GetFileName(folder.TrimEnd('\\', '/')),
                Role = source.Role,
                WorkshopItem = source.WorkshopItem,
                DependsOn = sub.DependedModules.ToList(),
                Scenes = scenes,
            };

            long total = files.Sum(f => f.Size);
            long done = 0;
            int filesDone = 0;
            var entries = new ManifestFile[files.Count];
            var objects = new ConcurrentDictionary<string, ObjectRef>(StringComparer.Ordinal);
            var lastReport = Stopwatch.StartNew();
            object reportLock = new object();

            void Report(string current, bool force)
            {
                if (progress == null)
                {
                    return;
                }

                lock (reportLock)
                {
                    if (!force && lastReport.ElapsedMilliseconds < 250)
                    {
                        return;
                    }

                    lastReport.Restart();
                    progress.Report(new ProcessProgress
                    {
                        Phase = module.Name,
                        FilesDone = Volatile.Read(ref filesDone),
                        FilesTotal = files.Count,
                        BytesDone = Interlocked.Read(ref done),
                        BytesTotal = total,
                        Current = current,
                    });
                }
            }

            // Largest first so one big file does not run alone at the end.
            var order = Enumerable.Range(0, files.Count).OrderByDescending(i => files[i].Size).ToList();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cancel };
            Parallel.ForEach(order, options, i =>
            {
                var f = files[i];
                entries[i] = f.Size >= FastCdc.ChunkThreshold
                    ? ProcessChunked(f, store, mode, objects, threads, n => { Interlocked.Add(ref done, n); Report(f.Path, false); })
                    : ProcessWhole(f, store, mode, objects, n => Interlocked.Add(ref done, n));
                Interlocked.Increment(ref filesDone);
                Report(f.Path, false);
            });
            Report(null, true);

            module.Files = entries.ToList();
            result.Module = module;
            foreach (var kv in objects)
            {
                result.Objects[kv.Key] = kv.Value;
            }

            result.BytesHashed = total;
            result.Elapsed = sw.Elapsed;
            return result;
        }

        private static ManifestFile ProcessWhole(ScannedFile f, ObjectStore store, ProcessMode mode, ConcurrentDictionary<string, ObjectRef> objects, Action<long> onBytes)
        {
            byte[] data = File.ReadAllBytes(f.FullPath);
            if (data.LongLength != f.Size)
            {
                throw new IOException(f.Path + " changed size while publishing");
            }

            string sha = Sha256Util.Of(data);
            onBytes(data.Length);
            var obj = Resolve(sha, new ArraySegment<byte>(data), store, mode);
            objects.TryAdd(sha, new ObjectRef { Sha256 = sha, Ext = obj.Ext, ObjectSize = obj.ObjectSize, Estimated = obj.Ext == null, Kind = "file" });
            return new ManifestFile
            {
                Path = f.Path,
                Size = f.Size,
                Sha256 = sha,
                Object = obj.Ext == null ? null : ObjectNames.Make(sha, obj.Ext),
                ObjectSize = obj.ObjectSize,
                Exec = RelativePath.IsExecutable(f.Path),
            };
        }

        private static ManifestFile ProcessChunked(ScannedFile f, ObjectStore store, ProcessMode mode, ConcurrentDictionary<string, ObjectRef> objects, int parallel, Action<long> onBytes)
        {
            var index = new ChunkIndex { Size = f.Size };
            bool estimated = false;

            // Reading/cutting/hashing is sequential (it is cheap); compressing new chunks at level 19 is not, so new
            // chunks are copied out of the chunker buffer and compressed by up to `parallel` tasks, which keeps one
            // 2 GB Core pack from running on a single core. Memory: at most `parallel` 1 MiB chunks in flight.
            var pending = new List<Task>();
            using (var gate = new SemaphoreSlim(Math.Max(1, parallel)))
            {
                using (var whole = SHA256.Create())
                using (var fs = Sha256Util.OpenSequential(f.FullPath))
                {
                    FastCdc.Contract.Split(fs, (chunk, offset) =>
                    {
                        whole.TransformBlock(chunk.Array, chunk.Offset, chunk.Count, null, 0);
                        string sha = Sha256Util.Of(chunk);
                        var row = new ChunkRef { Sha256 = sha, Size = chunk.Count };
                        index.Chunks.Add(row);
                        onBytes(chunk.Count);
                        if (mode == ProcessMode.PlanFast || store.TryGetExisting(sha, out _))
                        {
                            Fill(row, Resolve(sha, chunk, store, mode), objects, ref estimated);
                            return;
                        }

                        var copy = new byte[chunk.Count];
                        Buffer.BlockCopy(chunk.Array, chunk.Offset, copy, 0, chunk.Count);
                        gate.Wait();
                        pending.Add(Task.Run(() =>
                        {
                            try
                            {
                                var obj = Resolve(sha, new ArraySegment<byte>(copy), store, mode);
                                bool unused = false;
                                Fill(row, obj, objects, ref unused);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }));
                    });
                    whole.TransformFinalBlock(new byte[0], 0, 0);
                    index.Sha256 = Sha256Util.Hex(whole.Hash);
                }

                Task.WaitAll(pending.ToArray());
            }

            if (index.Chunks.Sum(c => c.Size) != f.Size)
            {
                throw new IOException(f.Path + " changed size while publishing");
            }

            string indexObject = null;
            if (!estimated)
            {
                byte[] indexBytes = index.ToCanonicalBytes();
                string indexSha = Sha256Util.Of(indexBytes);
                var io = Resolve(indexSha, new ArraySegment<byte>(indexBytes), store, mode);
                objects.TryAdd(indexSha, new ObjectRef { Sha256 = indexSha, Ext = io.Ext, ObjectSize = io.ObjectSize, Kind = "index" });
                indexObject = ObjectNames.Make(indexSha, io.Ext);
            }

            return new ManifestFile
            {
                Path = f.Path,
                Size = f.Size,
                Sha256 = index.Sha256,
                Chunked = true,
                ChunkIndex = indexObject,
                ObjectSize = index.DistinctObjectBytes,
                Exec = RelativePath.IsExecutable(f.Path),
            };
        }

        private static void Fill(ChunkRef row, StoredObject obj, ConcurrentDictionary<string, ObjectRef> objects, ref bool estimated)
        {
            estimated |= obj.Ext == null;
            row.Ext = obj.Ext ?? "?";
            row.ObjectSize = obj.ObjectSize;
            objects.TryAdd(row.Sha256, new ObjectRef { Sha256 = row.Sha256, Ext = obj.Ext, ObjectSize = obj.ObjectSize, Estimated = obj.Ext == null, Kind = "chunk" });
        }

        private static StoredObject Resolve(string sha, ArraySegment<byte> data, ObjectStore store, ProcessMode mode)
        {
            if (store.TryGetExisting(sha, out var existing))
            {
                return mode == ProcessMode.Publish ? store.Place(sha, null) : existing;
            }

            switch (mode)
            {
                case ProcessMode.PlanFast:
                    return new StoredObject { Sha256 = sha, Ext = null, ObjectSize = data.Count };
                case ProcessMode.PlanExact:
                    var enc = TransportCodecs.EncodeBest(data);
                    return new StoredObject { Sha256 = sha, Ext = enc.Extension, ObjectSize = enc.Bytes.LongLength };
                default:
                    // The chunk buffer is reused by the chunker: encode now, inside the callback.
                    var copy = data;
                    return store.Place(sha, () => TransportCodecs.EncodeBest(copy));
            }
        }
    }
}
