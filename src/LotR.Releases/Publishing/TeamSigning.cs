using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.Releases.Hashing;
using LotR.Releases.Install;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;

namespace LotR.Releases.Publishing
{
    // Team signing (docs/TEAM-SIGNING.md). The release key lives on team members' PCs, never on the web server:
    //   server  publish --pending          -> files\releases\<id>\ (manifest + objects, NO signature) + releases\pending.json
    //   PC      sign-release               -> fetches the pending manifest, checks every CHANGED module file by file against
    //                                         the PC's own copy of the modules, signs -> <signed>\releases\<id>\manifest.json.sig
    //   PC      publish-updater            -> <signed>\updater\ (exe, .sha256, latest.json, latest.json.sig)
    //   server  apply-signed               -> verifies every signature with the public key, installs them, moves the channel
    // A hacked server can publish whatever it wants as "pending", but nothing gets a valid signature unless a team PC
    // holds the same files; and the server never holds anything it could sign with.

    /// <summary>&lt;releases&gt;\pending.json: the release published without a key, waiting for the team signature.</summary>
    public sealed class PendingMarker
    {
        public const string FileName = "pending.json";

        public string ReleaseId { get; set; }

        public string Channel { get; set; }

        public string PublishedAt { get; set; }

        public string PublishedBy { get; set; }

        public JsonObject ToJson() => new JsonObject
        {
            ["schema"] = 1,
            ["releaseId"] = ReleaseId,
            ["channel"] = Channel,
            ["publishedAt"] = PublishedAt,
            ["publishedBy"] = PublishedBy,
        };

        public static PendingMarker Parse(byte[] utf8)
        {
            var o = JsonParser.Parse(utf8) as JsonObject ?? throw new ManifestFormatException("pending.json: not an object");
            var m = new PendingMarker
            {
                ReleaseId = o["releaseId"]?.AsString(),
                Channel = o["channel"]?.AsString(),
                PublishedAt = o["publishedAt"]?.AsString(),
                PublishedBy = o["publishedBy"]?.AsString(),
            };
            if (m.ReleaseId == null || !ManifestValidator.ReleaseIdPattern.IsMatch(m.ReleaseId))
            {
                throw new ManifestFormatException("pending.json: bad releaseId '" + m.ReleaseId + "'");
            }

            if (m.Channel == null || !ManifestValidator.ChannelPattern.IsMatch(m.Channel))
            {
                throw new ManifestFormatException("pending.json: bad channel '" + m.Channel + "'");
            }

            return m;
        }

        public static string PathIn(string releasesRoot) => Path.Combine(releasesRoot, FileName);

        /// <summary>Null when nothing is pending.</summary>
        public static PendingMarker Read(string releasesRoot)
        {
            string p = PathIn(releasesRoot);
            return File.Exists(p) ? Parse(File.ReadAllBytes(p)) : null;
        }

        public static void Write(string releasesRoot, PendingMarker m) => AtomicFile.Write(PathIn(releasesRoot), m.ToJson().ToCanonicalBytes());

        /// <summary>Removes pending.json only when it still names <paramref name="releaseId"/> (a newer pending one stays).</summary>
        public static bool Clear(string releasesRoot, string releaseId)
        {
            var m = Read(releasesRoot);
            if (m == null || !string.Equals(m.ReleaseId, releaseId, StringComparison.Ordinal))
            {
                return false;
            }

            File.Delete(PathIn(releasesRoot));
            return true;
        }
    }

    // ------------------------------------------------------------------------------------------------ PC: review

    public sealed class PendingReviewRequest
    {
        /// <summary>The pending manifest.json exactly as published (the bytes that get signed).</summary>
        public byte[] ManifestBytes { get; set; }

        public string ExpectedReleaseId { get; set; }

        public string ExpectedChannel { get; set; } = "stable";

        /// <summary>The channel's live release (manifest + signature), or null when nothing is live yet.</summary>
        public byte[] CurrentManifestBytes { get; set; }

        public byte[] CurrentSignatureBytes { get; set; }

        /// <summary>Keys the live release must verify with (the team key's public part, + --pub for a rotation).</summary>
        public List<TrustedKey> Trusted { get; } = new List<TrustedKey>();

        /// <summary>Folders holding module folders by name (the team repo clone, the game's Modules folder...).</summary>
        public List<string> ModuleRoots { get; } = new List<string>();

        /// <summary>Explicit module folder name -> local folder (wins over the roots).</summary>
        public Dictionary<string, string> ModuleFolders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>https://domain the release must point players at (null = the live release's own).</summary>
        public string ExpectedBaseUrl { get; set; }

        public int Threads { get; set; } = 4;

        public IProgress<string> Log { get; set; }
    }

    public sealed class ModuleReview
    {
        public string Id { get; set; }

        public string Folder { get; set; }

        public string Version { get; set; }

        public string LiveVersion { get; set; }

        /// <summary>unchanged (byte-identical to the signed live module), verified (every file matches the local copy) or failed.</summary>
        public string Status { get; set; }

        public string LocalFolder { get; set; }

        public int Files { get; set; }

        public long Size { get; set; }

        public int ChangedFromLive { get; set; }

        public List<string> Problems { get; } = new List<string>();

        public int ExtraLocalFiles { get; set; }

        public long BytesHashed { get; set; }
    }

    public sealed class PendingReview
    {
        public ReleaseManifest Manifest { get; set; }

        public string LiveReleaseId { get; set; }

        public List<ModuleReview> Modules { get; } = new List<ModuleReview>();

        public List<string> Errors { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public bool Ok => Errors.Count == 0 && Modules.Count > 0 && Modules.All(m => m.Status == "unchanged" || m.Status == "verified");

        private static JsonObject NotesJson(ReleaseManifest m)
        {
            var o = new JsonObject();
            foreach (var kv in m.Notes ?? new SortedDictionary<string, string>())
            {
                o[kv.Key] = kv.Value;
            }

            return o;
        }

        public JsonObject ToJson() => new JsonObject
        {
            ["ok"] = Ok,
            ["releaseId"] = Manifest?.ReleaseId,
            ["channel"] = Manifest?.Channel,
            ["publishedAt"] = Manifest?.PublishedAt,
            ["publishedBy"] = Manifest?.PublishedBy,
            ["liveReleaseId"] = LiveReleaseId,
            ["base"] = Manifest?.Endpoints?.Base,
            ["notes"] = Manifest == null ? null : NotesJson(Manifest),
            ["modules"] = new JsonArray(Modules.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Id,
                ["folder"] = m.Folder,
                ["version"] = m.Version,
                ["liveVersion"] = m.LiveVersion,
                ["status"] = m.Status,
                ["localFolder"] = m.LocalFolder,
                ["files"] = m.Files,
                ["size"] = m.Size,
                ["changedFromLive"] = m.ChangedFromLive,
                ["extraLocalFiles"] = m.ExtraLocalFiles,
                ["problems"] = new JsonArray(m.Problems.Take(50).Select(p => (JsonNode)p)),
            })),
            ["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e)),
            ["warnings"] = new JsonArray(Warnings.Select(e => (JsonNode)e)),
        };
    }

    /// <summary>
    /// What a team PC checks before signing a pending release. Nothing in the pending manifest is taken on trust:
    /// the live release must carry OUR signature; every module is either byte-identical to the live (signed) one or
    /// matches, file by file (SHA-256), the PC's own copy of that module folder. Anything else is refused.
    /// </summary>
    public static class PendingReviewer
    {
        public static PendingReview Review(PendingReviewRequest req)
        {
            var r = new PendingReview();
            ReleaseManifest m;
            try
            {
                m = ManifestCodec.Parse(req.ManifestBytes);
            }
            catch (Exception e) when (e is FormatException || e is ManifestFormatException || e is InvalidDataException)
            {
                r.Errors.Add("the pending manifest cannot be read: " + e.Message);
                return r;
            }

            r.Manifest = m;
            if (!ManifestCodec.ToCanonicalBytes(m).SequenceEqual(req.ManifestBytes))
            {
                r.Errors.Add("the pending manifest is not in canonical form: refused (the tool always writes it canonical; someone edited it)");
            }

            var v = ManifestValidator.Validate(m);
            r.Errors.AddRange(v.Errors.Select(e => "manifest: " + e));
            r.Warnings.AddRange(v.Warnings.Select(e => "manifest: " + e));
            if (req.ExpectedReleaseId != null && !string.Equals(m.ReleaseId, req.ExpectedReleaseId, StringComparison.Ordinal))
            {
                r.Errors.Add("the manifest's releaseId " + m.ReleaseId + " is not the pending one " + req.ExpectedReleaseId);
            }

            if (!string.Equals(m.Channel, req.ExpectedChannel, StringComparison.Ordinal))
            {
                r.Errors.Add("the release is for channel '" + m.Channel + "', not '" + req.ExpectedChannel + "'");
            }

            if (m.Extra != null && m.Extra.Any())
            {
                r.Errors.Add("the manifest carries unknown top-level fields (" + string.Join(", ", m.Extra.Select(kv => kv.Key)) + "): refused");
            }

            // ---- the live release: must be ours
            ReleaseManifest live = null;
            Dictionary<string, byte[]> liveModules = null;
            if (req.CurrentManifestBytes != null)
            {
                var check = req.CurrentSignatureBytes == null
                    ? new SignatureCheck { Valid = false, Message = "no signature" }
                    : ManifestSignature.Verify(req.CurrentManifestBytes, req.CurrentSignatureBytes, req.Trusted);
                if (!check.Valid)
                {
                    r.Errors.Add("the LIVE release does not verify with the team key (" + check.Message + "): something is wrong on the server; nothing is signed");
                }
                else
                {
                    live = ManifestCodec.Parse(req.CurrentManifestBytes);
                    r.LiveReleaseId = live.ReleaseId;
                    liveModules = ModuleBytes(live);
                    if (string.CompareOrdinal(m.PublishedAt ?? string.Empty, live.PublishedAt ?? string.Empty) <= 0)
                    {
                        r.Errors.Add("the pending release (" + m.PublishedAt + ") is not newer than the live one " + live.ReleaseId + " (" + live.PublishedAt + ")");
                    }
                }
            }

            // ---- endpoints: where the updater sends players. Only the base may differ, and only to the expected domain.
            var e0 = live?.Endpoints ?? new ReleaseEndpoints();
            var e1 = m.Endpoints;
            if (e1 == null)
            {
                if (live?.Endpoints != null)
                {
                    r.Errors.Add("the pending release drops the endpoints of the live one");
                }
            }
            else
            {
                if (e1.Files != e0.Files || e1.Queue != e0.Queue || e1.Support != e0.Support)
                {
                    r.Errors.Add("the pending release changes the download/queue/support endpoints (" + e1.Files + ", " + e1.Queue + ", " + e1.Support + "): refused");
                }

                string wantBase = req.ExpectedBaseUrl?.TrimEnd('/') ?? live?.Endpoints?.Base;
                if (wantBase != null && !string.Equals(e1.Base, wantBase, StringComparison.Ordinal))
                {
                    r.Errors.Add("the pending release points players at " + (e1.Base ?? "(nothing)") + ", expected " + wantBase);
                }
            }

            // ---- modules
            var pendingModules = ModuleBytes(m);
            foreach (var mod in m.Modules)
            {
                var mr = new ModuleReview { Id = mod.Id, Folder = mod.Folder, Version = mod.Version, Files = mod.Files.Count, Size = mod.TotalSize };
                r.Modules.Add(mr);
                var liveMod = live?.FindModule(mod.Id);
                mr.LiveVersion = liveMod?.Version;
                if (liveModules != null && liveModules.TryGetValue(mod.Id, out var lb) && pendingModules.TryGetValue(mod.Id, out var pb) && lb.SequenceEqual(pb))
                {
                    mr.Status = "unchanged";
                    continue;
                }

                if (liveMod != null)
                {
                    mr.ChangedFromLive = mod.Files.Count(f => liveMod.FindFile(f.Path)?.Sha256 != f.Sha256) + liveMod.Files.Count(f => mod.FindFile(f.Path) == null);
                    if (!string.Equals(liveMod.WorkshopItem, mod.WorkshopItem, StringComparison.Ordinal))
                    {
                        r.Warnings.Add(mod.Folder + ": Workshop item " + (liveMod.WorkshopItem ?? "-") + " -> " + (mod.WorkshopItem ?? "-"));
                    }

                    if (!string.Equals(liveMod.Role, mod.Role, StringComparison.Ordinal))
                    {
                        r.Warnings.Add(mod.Folder + ": role " + liveMod.Role + " -> " + mod.Role);
                    }
                }

                var candidates = FindLocal(req, mod.Folder);
                if (candidates.Count == 0)
                {
                    mr.Status = "failed";
                    mr.Problems.Add("no local copy of the module folder '" + mod.Folder + "' to check it against (give its folder: --module " + mod.Folder + "=<dir> or --modules-root <dir>)");
                    continue;
                }

                // Several local copies (team repo clone, game Modules folder...): the first one that matches wins; when
                // none does, the closest one is reported.
                InstallCheck best = null;
                foreach (var local in candidates)
                {
                    req.Log?.Report("checking " + mod.Folder + " " + mod.Version + " against " + local + " (" + mod.Files.Count + " files, " + (mod.TotalSize / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB)...");
                    var check = InstalledModuleVerifier.Verify(local, mod, InstallCheckMode.Full, Math.Max(1, req.Threads));
                    mr.BytesHashed += check.BytesHashed;
                    if (best == null || check.Problems.Count() < best.Problems.Count())
                    {
                        best = check;
                    }

                    if (check.Ok)
                    {
                        break;
                    }
                }

                mr.LocalFolder = best.ModuleFolder;
                mr.ExtraLocalFiles = best.Extra.Count;
                foreach (var p in best.Problems)
                {
                    mr.Problems.Add(p.State + ": " + p.Path + (p.Error != null ? " (" + p.Error + ")" : string.Empty));
                }

                if (!best.Ok && candidates.Count > 1)
                {
                    mr.Problems.Insert(0, "none of the " + candidates.Count + " local copies matches (" + string.Join(", ", candidates) + "); closest: " + best.ModuleFolder);
                }

                mr.Status = best.Ok ? "verified" : "failed";
                if (best.Ok && best.Extra.Count > 0)
                {
                    r.Warnings.Add(mod.Folder + ": " + best.Extra.Count + " file(s) in the local copy are not in the release (not shipped to players): " + string.Join(", ", best.Extra.Take(5)) + (best.Extra.Count > 5 ? ", ..." : string.Empty));
                }
            }

            if (live != null)
            {
                foreach (var gone in live.Modules.Where(x => m.FindModule(x.Id) == null))
                {
                    r.Warnings.Add("module " + gone.Id + " (" + gone.Folder + ") is no longer in the release");
                }
            }

            return r;
        }

        private static List<string> FindLocal(PendingReviewRequest req, string folder)
        {
            var list = new List<string>();
            if (req.ModuleFolders.TryGetValue(folder, out var explicitDir))
            {
                if (Directory.Exists(explicitDir))
                {
                    list.Add(Path.GetFullPath(explicitDir));
                }

                return list;
            }

            foreach (var root in req.ModuleRoots)
            {
                string d = Path.Combine(root, folder);
                if (File.Exists(Path.Combine(d, "SubModule.xml")) && !list.Contains(Path.GetFullPath(d), StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(Path.GetFullPath(d));
                }
            }

            return list;
        }

        /// <summary>Module id -> canonical bytes of its whole manifest entry (files, scenes, dependencies, Workshop item...).</summary>
        private static Dictionary<string, byte[]> ModuleBytes(ReleaseManifest m)
        {
            var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var n in ManifestCodec.ToJson(m)["modules"].AsArray())
            {
                string id = n.AsObject()?["id"]?.AsString();
                if (id != null)
                {
                    d[id] = n.ToCanonicalBytes();
                }
            }

            return d;
        }
    }

    // ------------------------------------------------------------------------------------------------ server: apply

    public sealed class AttachResult
    {
        public string ReleaseId { get; set; }

        public string Channel { get; set; }

        public bool AlreadySigned { get; set; }

        public bool ChannelUpdated { get; set; }

        public string PreviousChannelRelease { get; set; }

        public string SignedBy { get; set; }

        public ReleaseVerification Verification { get; set; }

        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>Installs a team signature on a pending release, verifies the whole release, then moves its channel.</summary>
    public static class ReleaseSignatureAttacher
    {
        public static AttachResult Attach(string releasesRoot, string releaseId, byte[] sigBytes, IList<TrustedKey> trusted, int threads = 4, bool force = false, IProgress<string> log = null)
        {
            if (!ManifestValidator.ReleaseIdPattern.IsMatch(releaseId ?? string.Empty))
            {
                throw new ArgumentException("bad release id '" + releaseId + "'");
            }

            if (trusted == null || trusted.Count == 0)
            {
                throw new InvalidOperationException("no trusted public key to check the signature with");
            }

            string dir = Path.Combine(releasesRoot, releaseId);
            var rel = ReleaseFolder.Open(dir);
            var check = ManifestSignature.Verify(rel.ManifestBytes, sigBytes, trusted);
            if (!check.Valid)
            {
                throw new InvalidOperationException("the signature for " + releaseId + " does not verify with the server's public key (" + check.Message + "): not installed");
            }

            var res = new AttachResult { ReleaseId = releaseId, Channel = rel.Manifest.Channel, SignedBy = check.Message };
            string sigPath = Path.Combine(dir, ReleaseFolder.SignatureFile);
            byte[] previous = rel.SignatureBytes;
            res.AlreadySigned = previous != null && previous.SequenceEqual(sigBytes);
            if (!res.AlreadySigned)
            {
                AtomicFile.Write(sigPath, sigBytes);
            }

            log?.Report("verifying " + releaseId + " (signature + every object decoded and hashed)...");
            res.Verification = ReleaseVerifier.Verify(dir, trusted, threads);
            if (!res.Verification.Ok)
            {
                if (!res.AlreadySigned)
                {
                    if (previous != null)
                    {
                        AtomicFile.Write(sigPath, previous);
                    }
                    else
                    {
                        File.Delete(sigPath);
                    }
                }

                throw new InvalidDataException("release " + releaseId + " failed verification, signature not kept: " + string.Join("; ", res.Verification.Errors.Take(5)));
            }

            var channels = ChannelIndex.Load(Path.Combine(releasesRoot, ReleaseFolder.ChannelsFile));
            string current = channels.Get(rel.Manifest.Channel);
            res.PreviousChannelRelease = current;
            if (string.Equals(current, releaseId, StringComparison.Ordinal))
            {
                // already live
            }
            else
            {
                string currentAt = null;
                if (current != null && File.Exists(Path.Combine(releasesRoot, current, ReleaseFolder.ManifestFile)))
                {
                    currentAt = ReleaseFolder.Open(Path.Combine(releasesRoot, current)).Manifest.PublishedAt;
                }

                if (current == null || force || string.CompareOrdinal(rel.Manifest.PublishedAt ?? string.Empty, currentAt ?? string.Empty) > 0)
                {
                    ReleasePublisher.SetChannel(releasesRoot, rel.Manifest.Channel, releaseId);
                    res.ChannelUpdated = true;
                }
                else
                {
                    res.Warnings.Add(releaseId + " is older than the live " + current + ": signed, channel NOT moved (set-channel " + rel.Manifest.Channel + " " + releaseId + " to roll back on purpose)");
                }
            }

            PendingMarker.Clear(releasesRoot, releaseId);
            return res;
        }
    }

    public sealed class UpdaterApplyResult
    {
        public string LatestFile { get; set; }

        public string Version { get; set; }

        public string PreviousVersion { get; set; }

        /// <summary>The same latest.json (same bytes) was already live: nothing written.</summary>
        public bool AlreadyLive { get; set; }

        public string SignedBy { get; set; }
    }

    /// <summary>
    /// Installs an updater release signed on a team PC (lotr-publish publish-updater --updater-dir &lt;signed&gt;\updater):
    /// latest.json must verify with the trusted key, the exe must match it (name, size, SHA-256), never a downgrade.
    /// Writes exe, .sha256 (recomputed here), latest.json.sig, then latest.json last, each atomically.
    /// </summary>
    public static class UpdaterInstaller
    {
        private static readonly Regex LatestName = new Regex(@"^latest(-[a-z][a-z0-9-]{0,31})?\.json$", RegexOptions.CultureInvariant);

        public static IEnumerable<string> LatestFiles(string srcDir) =>
            Directory.Exists(srcDir) ? Directory.GetFiles(srcDir, "latest*.json").Where(f => LatestName.IsMatch(Path.GetFileName(f))).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>();

        public static UpdaterApplyResult Apply(string srcLatestFile, string updaterDir, IList<TrustedKey> trusted, bool force = false)
        {
            string name = Path.GetFileName(srcLatestFile);
            if (!LatestName.IsMatch(name))
            {
                throw new ArgumentException(name + " is not latest.json / latest-<channel>.json");
            }

            if (trusted == null || trusted.Count == 0)
            {
                throw new InvalidOperationException("no trusted public key to check latest.json with");
            }

            string srcDir = Path.GetDirectoryName(Path.GetFullPath(srcLatestFile));
            byte[] latestBytes = File.ReadAllBytes(srcLatestFile);
            string sigFile = srcLatestFile + ".sig";
            if (!File.Exists(sigFile))
            {
                throw new InvalidOperationException(name + " has no " + name + ".sig: an unsigned updater release is never installed");
            }

            var check = ManifestSignature.Verify(latestBytes, File.ReadAllBytes(sigFile), trusted);
            if (!check.Valid)
            {
                throw new InvalidOperationException(name + ": the signature does not verify with the server's public key (" + check.Message + ")");
            }

            var latest = UpdaterLatest.Parse(latestBytes);
            var val = latest.Validate();
            if (!val.IsValid)
            {
                throw new InvalidDataException(name + " is invalid: " + string.Join("; ", val.Errors));
            }

            string exeName = UpdaterPublisher.ExeFileName(latest.Version);
            string expectedPath = "/files/updater/" + exeName;
            bool urlOk = latest.Url == expectedPath || (latest.Url.StartsWith("https://", StringComparison.Ordinal) && latest.Url.EndsWith(expectedPath, StringComparison.Ordinal) && latest.Url.IndexOf('/', 8) == latest.Url.Length - expectedPath.Length);
            if (!urlOk)
            {
                throw new InvalidDataException(name + ": url " + latest.Url + " is not this server's " + expectedPath);
            }

            string srcExe = Path.Combine(srcDir, exeName);
            if (!File.Exists(srcExe))
            {
                throw new FileNotFoundException(exeName + " is not next to " + name, srcExe);
            }

            byte[] exe = File.ReadAllBytes(srcExe);
            if (exe.LongLength != latest.Size || Sha256Util.Of(exe) != latest.Sha256)
            {
                throw new InvalidDataException(exeName + " does not match the signed " + name + " (size or SHA-256)");
            }

            var res = new UpdaterApplyResult { LatestFile = Path.Combine(updaterDir, name), Version = latest.Version, SignedBy = check.Message };
            if (File.Exists(res.LatestFile))
            {
                byte[] liveBytes = File.ReadAllBytes(res.LatestFile);
                if (liveBytes.SequenceEqual(latestBytes) && File.Exists(res.LatestFile + ".sig") && ManifestSignature.Verify(liveBytes, File.ReadAllBytes(res.LatestFile + ".sig"), trusted).Valid)
                {
                    res.AlreadyLive = true;
                    res.PreviousVersion = latest.Version;
                    return res;
                }

                UpdaterLatest live = null;
                try
                {
                    live = UpdaterLatest.Parse(liveBytes);
                }
                catch (Exception e) when (e is FormatException || e is ManifestFormatException)
                {
                    // unreadable live file: replaced
                }

                res.PreviousVersion = live?.Version;
                int cmp = live == null ? 1 : CompareVersions(latest.Version, live.Version);
                if (cmp < 0 && !force)
                {
                    throw new InvalidOperationException("updater " + latest.Version + " is older than the live " + live.Version + ": refused (no downgrade without --force)");
                }

                if (cmp == 0 && live.Sha256 != latest.Sha256 && !force)
                {
                    throw new InvalidOperationException("updater " + latest.Version + " is already live with OTHER bytes: bump the version (or --force)");
                }
            }

            string exeOut = Path.Combine(updaterDir, exeName);
            if (File.Exists(exeOut) && Sha256Util.OfFile(exeOut) != latest.Sha256 && !force)
            {
                throw new InvalidOperationException(exeName + " is already on the server with other bytes: bump the version (or --force)");
            }

            Directory.CreateDirectory(updaterDir);
            if (!File.Exists(exeOut) || Sha256Util.OfFile(exeOut) != latest.Sha256)
            {
                AtomicFile.Write(exeOut, exe);
            }

            AtomicFile.Write(exeOut + ".sha256", System.Text.Encoding.ASCII.GetBytes(Sha256SumFile.Format(latest.Sha256, exeName)));
            AtomicFile.Write(res.LatestFile + ".sig", File.ReadAllBytes(sigFile));
            AtomicFile.Write(res.LatestFile, latestBytes);
            return res;
        }

        /// <summary>1.2.3[.4][-tag]: numbers first; at equal numbers a version without a tag is newer than one with a tag.</summary>
        public static int CompareVersions(string a, string b)
        {
            string[] Split(string v, out string tag)
            {
                int dash = v.IndexOf('-');
                tag = dash >= 0 ? v.Substring(dash + 1) : null;
                return (dash >= 0 ? v.Substring(0, dash) : v).Split('.');
            }

            var pa = Split(a, out var ta);
            var pb = Split(b, out var tb);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                long x = i < pa.Length ? long.Parse(pa[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
                long y = i < pb.Length ? long.Parse(pb[i], System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (x != y)
                {
                    return x.CompareTo(y);
                }
            }

            if (ta == null && tb == null)
            {
                return 0;
            }

            if (ta == null)
            {
                return 1;
            }

            if (tb == null)
            {
                return -1;
            }

            return string.CompareOrdinal(ta, tb);
        }
    }
}
