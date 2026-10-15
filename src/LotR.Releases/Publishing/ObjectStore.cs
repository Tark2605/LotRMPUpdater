using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LotR.Releases.Model;
using LotR.Releases.Transport;

namespace LotR.Releases.Publishing
{
    public sealed class StoredObject
    {
        public string Sha256 { get; set; }

        public string Ext { get; set; }

        public long ObjectSize { get; set; }

        /// <summary>Where it already lives (an older release), or null when it was written by this publish.</summary>
        public string ExistingPath { get; set; }

        public string Name => ObjectNames.Make(Sha256, Ext);
    }

    /// <summary>
    /// Objects of the releases folder. The contract layout keeps objects inside each release
    /// (&lt;releaseId&gt;/objects/ab/&lt;sha&gt;.&lt;ext&gt;); an object that an older release already holds is NTFS
    /// hard-linked into the new release instead of being written again (zero extra bytes, and deleting an old release
    /// later never breaks a newer one). Copy is the fallback when hard links are impossible (other volume, FAT).
    /// CONTRACTS §1/§8: objects stay inside each release folder, deduplicated with hard links (no shared pool).
    /// </summary>
    public sealed class ObjectStore
    {
        private readonly ConcurrentDictionary<string, StoredObject> existing = new ConcurrentDictionary<string, StoredObject>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Lazy<StoredObject>> placed = new ConcurrentDictionary<string, Lazy<StoredObject>>(StringComparer.Ordinal);
        private long writtenObjects;
        private long writtenBytes;
        private long linkedObjects;
        private long linkedBytes;
        private long copiedObjects;

        /// <summary>Indexes every object of every release under <paramref name="releasesRoot"/> (staging folders excluded).</summary>
        public ObjectStore(string releasesRoot, string stagingDir)
        {
            ReleasesRoot = releasesRoot;
            StagingDir = stagingDir;
            if (Directory.Exists(releasesRoot))
            {
                foreach (var rel in Directory.GetDirectories(releasesRoot))
                {
                    string name = Path.GetFileName(rel);
                    if (name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string objects = Path.Combine(rel, "objects");
                    if (!Directory.Exists(objects))
                    {
                        continue;
                    }

                    foreach (var f in Directory.EnumerateFiles(objects, "*.*", SearchOption.AllDirectories))
                    {
                        string fileName = Path.GetFileName(f);
                        string sha = ObjectNames.Sha(fileName);
                        string ext = ObjectNames.Extension(fileName);
                        if (sha.Length != 64 || TransportCodecs.Find(ext) == null || fileName.EndsWith(".partial", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        existing.TryAdd(sha, new StoredObject { Sha256 = sha, Ext = ext, ObjectSize = new FileInfo(f).Length, ExistingPath = f });
                    }
                }
            }
        }

        public string ReleasesRoot { get; }

        /// <summary>Null in dry-run mode: nothing is written.</summary>
        public string StagingDir { get; }

        public int ExistingCount => existing.Count;

        public long WrittenObjects => Interlocked.Read(ref writtenObjects);

        public long WrittenBytes => Interlocked.Read(ref writtenBytes);

        public long LinkedObjects => Interlocked.Read(ref linkedObjects);

        public long LinkedBytes => Interlocked.Read(ref linkedBytes);

        public long CopiedObjects => Interlocked.Read(ref copiedObjects);

        public bool TryGetExisting(string sha, out StoredObject obj) => existing.TryGetValue(sha, out obj);

        /// <summary>
        /// Makes sure the object for <paramref name="sha"/> is in the new release: link an existing one, or encode with
        /// <paramref name="encode"/> and write it. Thread-safe; each sha is handled once.
        /// </summary>
        public StoredObject Place(string sha, Func<EncodedObject> encode)
        {
            var lazy = placed.GetOrAdd(sha, s => new Lazy<StoredObject>(() => PlaceCore(s, encode), LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }

        public IEnumerable<StoredObject> Placed
        {
            get
            {
                foreach (var kv in placed)
                {
                    yield return kv.Value.Value;
                }
            }
        }

        private StoredObject PlaceCore(string sha, Func<EncodedObject> encode)
        {
            if (existing.TryGetValue(sha, out var old))
            {
                if (StagingDir != null)
                {
                    string target = TargetPath(old.Name);
                    if (!File.Exists(target))
                    {
                        if (HardLink.TryCreate(target, old.ExistingPath))
                        {
                            Interlocked.Increment(ref linkedObjects);
                        }
                        else
                        {
                            File.Copy(old.ExistingPath, target, false);
                            Interlocked.Increment(ref copiedObjects);
                        }

                        Interlocked.Add(ref linkedBytes, old.ObjectSize);
                    }
                }

                return old;
            }

            var enc = encode();
            var obj = new StoredObject { Sha256 = sha, Ext = enc.Extension, ObjectSize = enc.Bytes.LongLength };
            if (StagingDir != null)
            {
                string target = TargetPath(obj.Name);
                string tmp = target + ".partial";
                File.WriteAllBytes(tmp, enc.Bytes);
                File.Move(tmp, target);
                Interlocked.Increment(ref writtenObjects);
                Interlocked.Add(ref writtenBytes, obj.ObjectSize);
            }

            return obj;
        }

        private string TargetPath(string objectName)
        {
            string rel = ObjectNames.RelativePath(objectName).Replace('/', Path.DirectorySeparatorChar);
            string full = Path.Combine(StagingDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            return full;
        }
    }

    public static class HardLink
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public static bool TryCreate(string newPath, string existingPath)
        {
            try
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT && CreateHardLinkW(newPath, existingPath, IntPtr.Zero);
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }
}
