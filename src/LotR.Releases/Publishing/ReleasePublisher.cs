using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using LotR.Releases.Hashing;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;

namespace LotR.Releases.Publishing
{
    public sealed class PublishRequest
    {
        /// <summary>The files\releases folder (contains channels.json and one folder per release).</summary>
        public string ReleasesRoot { get; set; }

        public List<ModuleSource> Modules { get; set; } = new List<ModuleSource>();

        public string Channel { get; set; } = "stable";

        public SortedDictionary<string, string> Notes { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public string PublishedBy { get; set; } = "lotr-publish";

        /// <summary>Null = inherit from the channel's current release.</summary>
        public string GameMinVersion { get; set; }

        public string GameMaxVersion { get; set; }

        /// <summary>Site base URL (https://domain). Null = inherit from the current release.</summary>
        public string BaseUrl { get; set; }

        public int Threads { get; set; } = Math.Min(Environment.ProcessorCount, 8);

        public List<ReleaseSigner> Signers { get; set; } = new List<ReleaseSigner>();

        /// <summary>Extra keys trusted when reading the current release (rotation: the old key's public part).</summary>
        public List<TrustedKey> TrustedKeys { get; set; } = new List<TrustedKey>();

        public bool UpdateChannel { get; set; } = true;

        public bool VerifyAfterPublish { get; set; } = true;

        /// <summary>For tests: fixed clock.</summary>
        public DateTime? NowUtc { get; set; }

        public IProgress<ProcessProgress> Progress { get; set; }

        public IProgress<string> Log { get; set; }
    }

    public sealed class PlanResult
    {
        public ReleaseManifest Manifest { get; set; }

        public ReleaseDiff Diff { get; set; }

        public SignatureCheck CurrentSignature { get; set; }

        public List<string> Warnings { get; } = new List<string>();

        public long BytesHashed { get; set; }

        public int FilesHashed { get; set; }

        public TimeSpan HashElapsed { get; set; }
    }

    public sealed class PublishResult
    {
        public string ReleaseId { get; set; }

        public string ReleaseDir { get; set; }

        public PlanResult Plan { get; set; }

        public long WrittenObjects { get; set; }

        public long WrittenBytes { get; set; }

        public long LinkedObjects { get; set; }

        public long LinkedBytes { get; set; }

        public long CopiedObjects { get; set; }

        public ReleaseVerification Verification { get; set; }

        public bool ChannelUpdated { get; set; }

        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>plan / publish / set-channel / attach-security over a releases folder (CONTRACTS §1-3).</summary>
    public static class ReleasePublisher
    {
        public static PlanResult Plan(PublishRequest req, bool exact) => Build(req, exact ? ProcessMode.PlanExact : ProcessMode.PlanFast, null, out _);

        public static PublishResult Publish(PublishRequest req)
        {
            if (req.Signers == null || req.Signers.Count == 0)
            {
                throw new InvalidOperationException("publish needs at least one signing key");
            }

            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(req.ReleasesRoot);
            string staging = Path.Combine(req.ReleasesRoot, ".staging-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(staging);
            try
            {
                var plan = Build(req, ProcessMode.Publish, staging, out var store);
                var m = plan.Manifest;
                byte[] manifestBytes = ManifestCodec.ToCanonicalBytes(m);
                byte[] sig = ManifestSignature.Create(manifestBytes, req.Signers);
                var self = ManifestSignature.Verify(manifestBytes, sig, req.Signers.Select(s => new TrustedKey(s.Spki)));
                if (!self.Valid)
                {
                    throw new InvalidOperationException("self-check of the new signature failed: " + self.Message);
                }

                File.WriteAllBytes(Path.Combine(staging, ReleaseFolder.ManifestFile), manifestBytes);
                File.WriteAllBytes(Path.Combine(staging, ReleaseFolder.SignatureFile), sig);

                string final = Path.Combine(req.ReleasesRoot, m.ReleaseId);
                if (Directory.Exists(final))
                {
                    throw new IOException("release folder " + final + " already exists");
                }

                Directory.Move(staging, final);
                staging = null;
                var result = new PublishResult
                {
                    ReleaseId = m.ReleaseId,
                    ReleaseDir = final,
                    Plan = plan,
                    WrittenObjects = store.WrittenObjects,
                    WrittenBytes = store.WrittenBytes,
                    LinkedObjects = store.LinkedObjects,
                    LinkedBytes = store.LinkedBytes,
                    CopiedObjects = store.CopiedObjects,
                };

                if (req.VerifyAfterPublish)
                {
                    req.Log?.Report("verifying " + m.ReleaseId + " (decode + hash every object)...");
                    var trusted = req.Signers.Select(s => new TrustedKey(s.Spki)).ToList();
                    result.Verification = ReleaseVerifier.Verify(final, trusted, req.Threads);
                    if (!result.Verification.Ok)
                    {
                        string failed = Path.Combine(req.ReleasesRoot, ".failed-" + m.ReleaseId);
                        Directory.Move(final, failed);
                        throw new InvalidDataException("the new release failed verification and was moved to " + failed + ": " + string.Join("; ", result.Verification.Errors.Take(5)));
                    }
                }

                if (req.UpdateChannel)
                {
                    SetChannel(req.ReleasesRoot, m.Channel, m.ReleaseId);
                    result.ChannelUpdated = true;
                }

                result.Elapsed = sw.Elapsed;
                return result;
            }
            finally
            {
                if (staging != null && Directory.Exists(staging))
                {
                    try
                    {
                        Directory.Delete(staging, true);
                    }
                    catch (IOException)
                    {
                        // left for manual cleanup; staging folders are ignored by readers
                    }
                }
            }
        }

        private static PlanResult Build(PublishRequest req, ProcessMode mode, string staging, out ObjectStore store)
        {
            if (req.Modules.Count == 0)
            {
                throw new InvalidOperationException("no module folder given");
            }

            if (!ManifestValidator.ChannelPattern.IsMatch(req.Channel ?? string.Empty))
            {
                throw new ArgumentException("bad channel name '" + req.Channel + "'");
            }

            var result = new PlanResult();
            ReleaseFolder current = null;
            Dictionary<string, ObjectRef> currentObjects = null;
            var channels = ChannelIndex.Load(Path.Combine(req.ReleasesRoot, ReleaseFolder.ChannelsFile));
            string currentId = channels.Get(req.Channel);
            if (currentId != null)
            {
                current = ReleaseFolder.Open(Path.Combine(req.ReleasesRoot, currentId));
                var trusted = req.TrustedKeys.Concat(req.Signers.Select(s => new TrustedKey(s.Spki))).ToList();
                result.CurrentSignature = trusted.Count == 0
                    ? new SignatureCheck { Valid = false, Message = "not checked (no key given)" }
                    : current.SignatureBytes == null ? new SignatureCheck { Valid = false, Message = "missing" } : ManifestSignature.Verify(current.ManifestBytes, current.SignatureBytes, trusted);
                if (!result.CurrentSignature.Valid)
                {
                    result.Warnings.Add("current release " + currentId + " signature: " + result.CurrentSignature.Message);
                }

                currentObjects = current.CollectObjects();
            }

            store = new ObjectStore(req.ReleasesRoot, staging);
            var allObjects = new Dictionary<string, ObjectRef>(StringComparer.Ordinal);
            var modules = new List<ManifestModule>();
            var hashSw = Stopwatch.StartNew();
            foreach (var src in req.Modules)
            {
                req.Log?.Report("reading " + src.Folder + " (" + src.Role + ")");
                var pm = ModuleProcessor.Process(src, store, mode, req.Threads, req.Progress);
                result.Warnings.AddRange(pm.Warnings.Where(w => !w.StartsWith("excluded:", StringComparison.Ordinal)).Select(w => pm.Module.Folder + ": " + w));
                result.BytesHashed += pm.BytesHashed;
                result.FilesHashed += pm.Module.Files.Count;
                var inherited = current?.Manifest.FindModule(pm.Module.Id);
                if (pm.Module.WorkshopItem == null && inherited != null)
                {
                    pm.Module.WorkshopItem = inherited.WorkshopItem;
                }

                modules.Add(pm.Module);
                foreach (var kv in pm.Objects)
                {
                    allObjects[kv.Key] = kv.Value;
                }
            }

            result.HashElapsed = hashSw.Elapsed;
            var now = (req.NowUtc ?? DateTime.UtcNow).ToUniversalTime();
            var prev = current?.Manifest;
            var manifest = new ReleaseManifest
            {
                Channel = req.Channel,
                PublishedAt = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                PublishedBy = req.PublishedBy,
                Notes = req.Notes ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
                Game = new GameRange
                {
                    MinVersion = req.GameMinVersion ?? prev?.Game?.MinVersion, // REVIEW-AFTER(game-version): not in SubModule.xml; CLI option, else inherited
                    MaxVersion = req.GameMaxVersion ?? prev?.Game?.MaxVersion,
                },
                Modules = modules,
                Endpoints = req.BaseUrl != null
                    ? new ReleaseEndpoints { Base = req.BaseUrl.TrimEnd('/') }
                    : prev?.Endpoints, // REVIEW-AFTER(domain): no domain yet; first real publish must pass --base-url https://<domain>
            };

            // releaseId = "<yyyyMMdd-HHmmss>-<moduleSet short hash>": first 8 hex of the SHA-256 of the canonical
            // modules array (ids, versions, every file hash): same content => same suffix.
            string moduleSetHash = Sha256Util.Of(new JsonArray(ManifestCodec.ToJson(manifest)["modules"].AsArray()).ToCanonicalBytes()).Substring(0, 8);
            manifest.ReleaseId = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + moduleSetHash;

            if (mode != ProcessMode.PlanFast)
            {
                var v = ManifestValidator.Validate(manifest);
                result.Warnings.AddRange(v.Warnings);
                if (!v.IsValid)
                {
                    throw new InvalidDataException("the manifest would be invalid:" + Environment.NewLine + string.Join(Environment.NewLine, v.Errors));
                }
            }

            result.Manifest = manifest;
            result.Diff = ReleaseDiff.Compute(prev, currentObjects, manifest, allObjects);
            return result;
        }

        /// <summary>Points a channel at a release (atomic replace of channels.json). Used by publish and for rollback.</summary>
        public static void SetChannel(string releasesRoot, string channel, string releaseId)
        {
            if (!ManifestValidator.ChannelPattern.IsMatch(channel) || !ManifestValidator.ReleaseIdPattern.IsMatch(releaseId))
            {
                throw new ArgumentException("bad channel or release id");
            }

            if (!File.Exists(Path.Combine(releasesRoot, releaseId, ReleaseFolder.ManifestFile)))
            {
                throw new FileNotFoundException("release " + releaseId + " does not exist in " + releasesRoot);
            }

            string path = Path.Combine(releasesRoot, ReleaseFolder.ChannelsFile);
            var idx = ChannelIndex.Load(path);
            idx.Channels[channel] = releaseId;
            AtomicFile.Write(path, idx.ToBytes());
        }
    }

    public static class AtomicFile
    {
        public static void Write(string path, byte[] bytes)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(tmp, path, null, true);
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }

                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    // Caddy may hold the file open for a moment while serving it.
                    Thread.Sleep(100 * (attempt + 1));
                }
            }
        }
    }
}
