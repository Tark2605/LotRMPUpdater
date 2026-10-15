using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Hashing;
using LotR.Releases.Model;

namespace LotR.Releases.Install
{
    public enum InstalledFileState
    {
        Ok,
        Missing,
        SizeDiffers,
        HashDiffers,
        Unreadable,
    }

    public enum InstallCheckMode
    {
        /// <summary>Re-hash every file ("Verify my install").</summary>
        Full,

        /// <summary>Existence and size only (fast pre-check before an update; the update re-hashes what it keeps).</summary>
        SizeOnly,
    }

    public sealed class InstalledFile
    {
        public string Path { get; set; }

        public string FullPath { get; set; }

        public ManifestFile Expected { get; set; }

        public InstalledFileState State { get; set; }

        public long ActualSize { get; set; }

        /// <summary>Null when not hashed (missing, or SizeOnly mode).</summary>
        public string ActualSha256 { get; set; }

        public string Error { get; set; }
    }

    public sealed class InstallCheck
    {
        public string ModuleId { get; set; }

        public string ModuleFolder { get; set; }

        public List<InstalledFile> Files { get; } = new List<InstalledFile>();

        /// <summary>Files on disk the manifest does not list (reported, never deleted by the updater).</summary>
        public List<string> Extra { get; } = new List<string>();

        public bool Ok => Files.All(f => f.State == InstalledFileState.Ok);

        public IEnumerable<InstalledFile> Problems => Files.Where(f => f.State != InstalledFileState.Ok);

        public long BytesHashed { get; set; }
    }

    /// <summary>Compares an installed module folder with a (verified) manifest module.</summary>
    public static class InstalledModuleVerifier
    {
        public static InstallCheck Verify(string moduleFolder, ManifestModule module, InstallCheckMode mode = InstallCheckMode.Full, int threads = 4, IProgress<long> bytesProgress = null, CancellationToken cancel = default)
        {
            var check = new InstallCheck { ModuleId = module.Id, ModuleFolder = Path.GetFullPath(moduleFolder) };
            var results = new InstalledFile[module.Files.Count];
            long hashed = 0;
            Parallel.For(0, module.Files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cancel }, i =>
            {
                var f = module.Files[i];
                var r = new InstalledFile { Path = f.Path, Expected = f };
                try
                {
                    r.FullPath = RelativePath.ToFileSystem(moduleFolder, f.Path);
                    var fi = new FileInfo(r.FullPath);
                    if (!fi.Exists)
                    {
                        r.State = InstalledFileState.Missing;
                    }
                    else
                    {
                        r.ActualSize = fi.Length;
                        if (fi.Length != f.Size)
                        {
                            r.State = InstalledFileState.SizeDiffers;
                        }
                        else if (mode == InstallCheckMode.Full)
                        {
                            r.ActualSha256 = Sha256Util.OfFile(r.FullPath, n =>
                            {
                                Interlocked.Add(ref hashed, n);
                                bytesProgress?.Report(n);
                            });
                            r.State = r.ActualSha256 == f.Sha256 ? InstalledFileState.Ok : InstalledFileState.HashDiffers;
                        }
                        else
                        {
                            r.State = InstalledFileState.Ok;
                        }
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
                {
                    r.State = InstalledFileState.Unreadable;
                    r.Error = e.Message;
                }

                results[i] = r;
            });

            check.Files.AddRange(results);
            check.BytesHashed = hashed;
            if (Directory.Exists(moduleFolder))
            {
                var listed = new HashSet<string>(module.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(moduleFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = RelativePath.FromFileSystem(moduleFolder, file);
                    if (!listed.Contains(rel))
                    {
                        check.Extra.Add(rel);
                    }
                }

                check.Extra.Sort(StringComparer.Ordinal);
            }

            return check;
        }
    }
}
