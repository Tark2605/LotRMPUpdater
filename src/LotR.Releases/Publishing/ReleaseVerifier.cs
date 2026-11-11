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
using LotR.Releases.Signing;
using LotR.Releases.Transport;

namespace LotR.Releases.Publishing
{
    public sealed class ReleaseVerification
    {
        public string ReleaseId { get; set; }

        public SignatureCheck Signature { get; set; }

        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public int FilesChecked { get; set; }

        public int ObjectsChecked { get; set; }

        public long BytesDecoded { get; set; }

        public long ObjectBytes { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>True when the release was checked without requiring a signature (pending team signature).</summary>
        public bool SignatureOptional { get; set; }

        /// <summary>Content OK (and signature OK, unless checked as a pending release).</summary>
        public bool Ok => Errors.Count == 0 && Signature != null && (Signature.Valid || SignatureOptional);
    }

    /// <summary>
    /// Full check of a release folder: signature over the exact manifest bytes, schema and path rules, canonical form,
    /// and every object: present, stored size as declared, zstd frame rules, decoded SHA-256 equal to its name, and for
    /// chunked files the whole-file SHA-256 rebuilt from the chunks in order.
    /// </summary>
    public static class ReleaseVerifier
    {
        public static ReleaseVerification Verify(string releaseDir, IEnumerable<TrustedKey> trusted, int threads = 4, IProgress<string> log = null, bool signatureRequired = true)
        {
            var sw = Stopwatch.StartNew();
            var report = new ReleaseVerification { ReleaseId = Path.GetFileName(Path.GetFullPath(releaseDir).TrimEnd('\\', '/')), SignatureOptional = !signatureRequired };
            ReleaseFolder rel;
            try
            {
                rel = ReleaseFolder.Open(releaseDir);
            }
            catch (Exception e) when (e is IOException || e is FormatException || e is UnauthorizedAccessException)
            {
                report.Errors.Add("manifest unreadable: " + e.Message);
                report.Signature = new SignatureCheck { Valid = false, Message = "not checked" };
                return report;
            }

            report.Signature = rel.SignatureBytes == null
                ? new SignatureCheck { Valid = false, Message = signatureRequired ? "manifest.json.sig is missing" : "not signed yet (pending the team signature)" }
                : ManifestSignature.Verify(rel.ManifestBytes, rel.SignatureBytes, trusted);
            // A pending release (published by a server without the key, docs/TEAM-SIGNING.md) is checked for everything
            // but the signature; a signature that IS there must still verify.
            if (!report.Signature.Valid && (signatureRequired || rel.SignatureBytes != null))
            {
                report.Errors.Add("signature: " + report.Signature.Message);
            }

            var m = rel.Manifest;
            var validation = ManifestValidator.Validate(m);
            report.Errors.AddRange(validation.Errors.Select(e => "manifest: " + e));
            report.Warnings.AddRange(validation.Warnings.Select(e => "manifest: " + e));
            if (!ManifestCodec.ToCanonicalBytes(m).SequenceEqual(rel.ManifestBytes))
            {
                report.Warnings.Add("manifest.json is not in canonical form (hand-edited?); the signature still covers its exact bytes");
            }

            if (!string.Equals(m.ReleaseId, rel.Id, StringComparison.Ordinal))
            {
                report.Errors.Add("folder name '" + rel.Id + "' differs from releaseId '" + m.ReleaseId + "'");
            }

            var errors = new ConcurrentBag<string>();
            var referenced = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            int objects = 0;
            long decoded = 0;
            long stored = 0;
            var files = m.Modules.SelectMany(mod => mod.Files.Select(f => (mod, f))).OrderByDescending(x => x.f.Size).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) }, x =>
            {
                string where = x.mod.Folder + "/" + x.f.Path;
                try
                {
                    if (!x.f.Chunked)
                    {
                        referenced.TryAdd(x.f.Object, 0);
                        long len = CheckObject(rel, x.f.Object, x.f.Size, x.f.ObjectSize);
                        Interlocked.Increment(ref objects);
                        Interlocked.Add(ref decoded, x.f.Size);
                        Interlocked.Add(ref stored, len);
                        return;
                    }

                    referenced.TryAdd(x.f.ChunkIndex, 0);
                    var index = rel.ReadChunkIndex(x.f.ChunkIndex);
                    Interlocked.Increment(ref objects);
                    if (index.Sha256 != x.f.Sha256 || index.Size != x.f.Size)
                    {
                        errors.Add(where + ": chunk index describes another file (sha/size mismatch)");
                        return;
                    }

                    if (index.Chunker != ChunkIndex.ContractChunker)
                    {
                        errors.Add(where + ": chunk index made with '" + index.Chunker + "', contract is '" + ChunkIndex.ContractChunker + "'");
                    }

                    if (index.DistinctObjectBytes != x.f.ObjectSize)
                    {
                        errors.Add(where + ": objectSize " + x.f.ObjectSize + " differs from the distinct chunk bytes " + index.DistinctObjectBytes);
                    }

                    using (var whole = SHA256.Create())
                    {
                        foreach (var c in index.Chunks)
                        {
                            referenced.TryAdd(c.Object, 0);
                            byte[] raw = File.ReadAllBytes(rel.ObjectPath(c.Object));
                            if (raw.LongLength != c.ObjectSize)
                            {
                                throw new InvalidDataException("chunk " + c.Object + " is " + raw.LongLength + " bytes, index says " + c.ObjectSize);
                            }

                            byte[] data = ObjectIO.Decode(raw, c.Ext, c.Size);
                            if (Sha256Util.Of(data) != c.Sha256)
                            {
                                throw new InvalidDataException("chunk " + c.Object + " content does not match its name");
                            }

                            whole.TransformBlock(data, 0, data.Length, null, 0);
                            Interlocked.Increment(ref objects);
                            Interlocked.Add(ref stored, raw.LongLength);
                        }

                        whole.TransformFinalBlock(new byte[0], 0, 0);
                        if (Sha256Util.Hex(whole.Hash) != x.f.Sha256)
                        {
                            errors.Add(where + ": chunks reassemble to a different file");
                        }
                    }

                    Interlocked.Add(ref decoded, x.f.Size);
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException || e is FormatException || e is UnauthorizedAccessException)
                {
                    errors.Add(where + ": " + e.Message);
                }
            });

            report.Errors.AddRange(errors.OrderBy(e => e, StringComparer.Ordinal));
            report.FilesChecked = files.Count;
            report.ObjectsChecked = objects;
            report.BytesDecoded = decoded;
            report.ObjectBytes = stored;

            string objectsDir = Path.Combine(rel.Path, "objects");
            if (Directory.Exists(objectsDir))
            {
                foreach (var f in Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories))
                {
                    if (!referenced.ContainsKey(Path.GetFileName(f)))
                    {
                        report.Warnings.Add("unreferenced object " + Path.GetFileName(f));
                    }
                }
            }

            report.Elapsed = sw.Elapsed;
            return report;
        }

        private static long CheckObject(ReleaseFolder rel, string objectName, long size, long objectSize)
        {
            string path = rel.ObjectPath(objectName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("object " + objectName + " is missing");
            }

            byte[] raw = File.ReadAllBytes(path);
            if (raw.LongLength != objectSize)
            {
                throw new InvalidDataException("object " + objectName + " is " + raw.LongLength + " bytes, manifest says " + objectSize);
            }

            byte[] data = ObjectIO.Decode(raw, ObjectNames.Extension(objectName), size);
            if (Sha256Util.Of(data) != ObjectNames.Sha(objectName))
            {
                throw new InvalidDataException("object " + objectName + " content does not match its name");
            }

            return raw.LongLength;
        }
    }
}
