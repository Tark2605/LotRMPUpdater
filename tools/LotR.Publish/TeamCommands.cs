using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;

namespace LotR.Publish
{
    /// <summary>
    /// Team signing commands (docs/TEAM-SIGNING.md): sign-release on a team member's PC, apply-signed on the server.
    /// </summary>
    internal static class TeamCommands
    {
        // ---------------------------------------------------------------- PC: sign-release

        public static int SignRelease(Args a)
        {
            string from = a.One("from") ?? throw new UsageException("--from <https://domain | files\\releases folder>");
            string outDir = a.One("out") ?? throw new UsageException("--out <signed folder>");
            bool json = a.Flag("json");
            var src = ReleaseSource.Create(from);
            PendingMarker pending = null;
            byte[] pendingBytes = src.TryGet(PendingMarker.FileName);
            if (pendingBytes != null)
            {
                pending = PendingMarker.Parse(pendingBytes);
            }

            string id = a.One("release") ?? pending?.ReleaseId;
            if (id == null)
            {
                Report(json, new JsonObject { ["pending"] = null, ["signed"] = false }, "nothing pending on " + src.Describe + ": no mod release to sign.");
                return 0;
            }

            if (!ManifestValidator.ReleaseIdPattern.IsMatch(id))
            {
                throw new UsageException("bad release id '" + id + "'");
            }

            string channel = a.One("channel") ?? (pending != null && pending.ReleaseId == id ? pending.Channel : "stable");
            string keyPath = a.One("key") ?? KeyStore.DefaultPcKeyPath;
            if (!File.Exists(keyPath))
            {
                throw new InvalidOperationException("no release key at " + keyPath + " (docs/TEAM-SIGNING.md, \"Move the key to your PC\")");
            }

            using (var signer = new ReleaseSigner(KeyStore.Load(keyPath)))
            {
                var trusted = a.Many("pub").Select(TrustedKey.FromPemFile).ToList();
                trusted.Add(new TrustedKey(signer.Spki, "team key"));
                byte[] manifest = src.Get(id + "/" + ReleaseFolder.ManifestFile);
                byte[] existing = src.TryGet(id + "/" + ReleaseFolder.SignatureFile);
                if (existing != null && ManifestSignature.Verify(manifest, existing, trusted).Valid)
                {
                    Report(json, new JsonObject { ["pending"] = id, ["signed"] = false, ["alreadySigned"] = true }, id + " already carries a valid team signature: nothing to do.");
                    return 0;
                }

                var req = new PendingReviewRequest
                {
                    ManifestBytes = manifest,
                    ExpectedReleaseId = id,
                    ExpectedChannel = channel,
                    ExpectedBaseUrl = a.One("base-url"),
                    Threads = a.Int("threads", Math.Min(Environment.ProcessorCount, 8)),
                    Log = json ? null : new Progress<string>(s => Console.Error.WriteLine(s)),
                };
                req.Trusted.AddRange(trusted);
                req.ModuleRoots.AddRange(a.Many("modules-root"));
                foreach (var m in a.Many("module"))
                {
                    var p = m.Split(new[] { '=' }, 2);
                    if (p.Length != 2 || p[0].Length == 0)
                    {
                        throw new UsageException("--module <Folder>=<dir>");
                    }

                    req.ModuleFolders[p[0]] = p[1];
                }

                byte[] channelsBytes = src.TryGet(ReleaseFolder.ChannelsFile);
                string liveId = channelsBytes == null ? null : ChannelIndex.Parse(channelsBytes).Get(channel);
                if (liveId != null && liveId != id)
                {
                    req.CurrentManifestBytes = src.Get(liveId + "/" + ReleaseFolder.ManifestFile);
                    req.CurrentSignatureBytes = src.TryGet(liveId + "/" + ReleaseFolder.SignatureFile);
                }

                // Progress lines go to stderr (as the review runs); the review itself to stdout.
                var review = PendingReviewer.Review(req);
                var o = review.ToJson();
                o["pending"] = id;
                o["signed"] = false;
                o["keyId"] = EcKeys.KeyId(signer.Spki);
                if (!json)
                {
                    PrintReview(review, src, signer.Spki);
                }

                if (!review.Ok)
                {
                    if (json)
                    {
                        Console.Write(o.ToPrettyString());
                    }
                    else
                    {
                        Console.WriteLine("RESULT: REFUSED - nothing was signed.");
                    }

                    return 1;
                }

                if (!a.Flag("yes"))
                {
                    if (Console.IsInputRedirected)
                    {
                        Console.Error.WriteLine("no console to ask in: run again with --yes to sign");
                        return 3;
                    }

                    Console.Write("Sign release " + id + " with the team key " + EcKeys.KeyId(signer.Spki) + "? (y/N) ");
                    string answer = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
                    if (answer != "y" && answer != "yes" && answer != "o" && answer != "oui")
                    {
                        Console.WriteLine("Not signed.");
                        return 3;
                    }
                }

                byte[] sig = ManifestSignature.Create(manifest, new[] { signer });
                var self = ManifestSignature.Verify(manifest, sig, new[] { new TrustedKey(signer.Spki) });
                if (!self.Valid)
                {
                    throw new InvalidOperationException("self-check of the new signature failed: " + self.Message);
                }

                string sigOut = Path.Combine(outDir, "releases", id, ReleaseFolder.SignatureFile);
                AtomicFile.Write(sigOut, sig);
                o["signed"] = true;
                o["signatureFile"] = Path.GetFullPath(sigOut);
                if (json)
                {
                    Console.Write(o.ToPrettyString());
                }
                else
                {
                    Console.WriteLine("signed      : " + Path.GetFullPath(sigOut));
                    Console.WriteLine("RESULT: SIGNED - the server installs it with the next site package (apply-signed).");
                }

                return 0;
            }
        }

        private static void PrintReview(PendingReview r, ReleaseSource src, byte[] spki)
        {
            var m = r.Manifest;
            Console.WriteLine();
            Console.WriteLine("source      : " + src.Describe);
            Console.WriteLine("team key    : " + EcKeys.KeyId(spki) + "  " + EcKeys.Fingerprint(spki));
            if (m != null)
            {
                Console.WriteLine("release     : " + m.ReleaseId + " (channel " + m.Channel + ") published " + m.PublishedAt + " by " + m.PublishedBy);
                Console.WriteLine("live now    : " + (r.LiveReleaseId ?? "(nothing live on this channel)"));
                Console.WriteLine("players get : " + (m.Endpoints?.Base ?? "(relative endpoints)"));
                foreach (var n in m.Notes ?? new SortedDictionary<string, string>())
                {
                    Console.WriteLine("notes [" + n.Key + "]  : " + n.Value);
                }
            }

            foreach (var mod in r.Modules)
            {
                string what = mod.Status == "unchanged"
                    ? "unchanged (identical to the signed live module)"
                    : mod.Status == "verified"
                        ? "CHANGED, every file matches " + mod.LocalFolder + " (" + Program.Mb(mod.BytesHashed) + " hashed)"
                        : "REFUSED";
                Console.WriteLine("module      : " + mod.Folder + " " + (mod.LiveVersion ?? "-") + " -> " + mod.Version + ", " + mod.Files + " files, " + Program.Mb(mod.Size)
                    + (mod.ChangedFromLive > 0 ? ", " + mod.ChangedFromLive + " file(s) differ from live" : string.Empty) + ": " + what);
                foreach (var p in mod.Problems.Take(15))
                {
                    Console.WriteLine("    " + p);
                }

                if (mod.Problems.Count > 15)
                {
                    Console.WriteLine("    ... " + (mod.Problems.Count - 15) + " more");
                }
            }

            foreach (var e in r.Errors)
            {
                Console.WriteLine("ERROR: " + e);
            }

            foreach (var w in r.Warnings.Take(30))
            {
                Console.WriteLine("warning: " + w);
            }
        }

        private static void Report(bool json, JsonObject o, string text)
        {
            if (json)
            {
                Console.Write(o.ToPrettyString());
            }
            else
            {
                Console.WriteLine(text);
            }
        }

        // ---------------------------------------------------------------- server: apply-signed

        public static int ApplySigned(Args a)
        {
            string from = a.One("from") ?? throw new UsageException("--from <signed folder>");
            if (!Directory.Exists(from))
            {
                throw new DirectoryNotFoundException("no folder " + from);
            }

            bool json = a.Flag("json");
            var trusted = Program.Trusted(a);
            if (trusted.Count == 0)
            {
                throw new UsageException("apply-signed needs the trusted public key: --pub <pem> (or --root, data\\secrets\\release-signing.pub.pem)");
            }

            string updaterDir = a.One("updater-dir") ?? (a.Root != null ? Path.Combine(a.Root, "files", "updater") : null);
            bool force = a.Flag("force");
            int threads = a.Int("threads", Math.Min(Environment.ProcessorCount, 8));
            var errors = new List<string>();
            var upd = new JsonArray();
            var rels = new JsonArray();
            var log = json ? null : new Progress<string>(s => Console.Error.WriteLine(s));

            string srcUpdater = Path.Combine(from, "updater");
            foreach (var latest in UpdaterInstaller.LatestFiles(srcUpdater))
            {
                try
                {
                    if (updaterDir == null)
                    {
                        throw new UsageException("give --root or --updater-dir to install " + Path.GetFileName(latest));
                    }

                    var r = UpdaterInstaller.Apply(latest, updaterDir, trusted, force);
                    upd.Add(new JsonObject { ["file"] = Path.GetFileName(latest), ["version"] = r.Version, ["previous"] = r.PreviousVersion, ["alreadyLive"] = r.AlreadyLive });
                    if (!json)
                    {
                        Console.WriteLine("updater     : " + (r.AlreadyLive ? r.Version + " already live (same signed latest.json)" : (r.PreviousVersion ?? "-") + " -> " + r.Version + " (" + r.SignedBy + ")"));
                    }
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    string name = Path.GetFileName(latest);
                    errors.Add(e.Message.StartsWith(name, StringComparison.Ordinal) ? "updater " + e.Message : "updater " + name + ": " + e.Message);
                }
            }

            string srcReleases = Path.Combine(from, "releases");
            if (Directory.Exists(srcReleases))
            {
                foreach (var dir in Directory.GetDirectories(srcReleases).OrderBy(d => d, StringComparer.Ordinal))
                {
                    string id = Path.GetFileName(dir);
                    try
                    {
                        if (!ManifestValidator.ReleaseIdPattern.IsMatch(id))
                        {
                            throw new InvalidDataException("not a release id");
                        }

                        string sigFile = Path.Combine(dir, ReleaseFolder.SignatureFile);
                        if (!File.Exists(sigFile))
                        {
                            throw new FileNotFoundException("no " + ReleaseFolder.SignatureFile + " in " + dir);
                        }

                        var r = ReleaseSignatureAttacher.Attach(a.Releases, id, File.ReadAllBytes(sigFile), trusted, threads, force, log);
                        rels.Add(new JsonObject
                        {
                            ["releaseId"] = id,
                            ["channel"] = r.Channel,
                            ["channelUpdated"] = r.ChannelUpdated,
                            ["previous"] = r.PreviousChannelRelease,
                            ["alreadySigned"] = r.AlreadySigned,
                            ["objects"] = r.Verification.ObjectsChecked,
                            ["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode)w)),
                        });
                        if (!json)
                        {
                            Console.WriteLine("release     : " + id + " signed (" + r.SignedBy + "), " + r.Verification.ObjectsChecked + " objects verified, channel " + r.Channel + (r.ChannelUpdated ? ": " + (r.PreviousChannelRelease ?? "-") + " -> " + id : " unchanged"));
                            foreach (var w in r.Warnings)
                            {
                                Console.WriteLine("warning: " + w);
                            }
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        errors.Add(id + ": " + e.Message);
                    }
                }
            }

            if (json)
            {
                Console.Write(new JsonObject
                {
                    ["ok"] = errors.Count == 0,
                    ["updater"] = upd,
                    ["releases"] = rels,
                    ["errors"] = new JsonArray(errors.Select(e => (JsonNode)e)),
                }.ToPrettyString());
            }
            else
            {
                if (upd.Count() == 0 && rels.Count() == 0 && errors.Count == 0)
                {
                    Console.WriteLine("nothing to apply in " + Path.GetFullPath(from));
                }

                foreach (var e in errors)
                {
                    Console.WriteLine("ERROR: " + e);
                }

                Console.WriteLine(errors.Count == 0 ? "RESULT: OK" : "RESULT: FAILED (" + errors.Count + "); what verified was installed, the rest was not touched");
            }

            return errors.Count == 0 ? 0 : 1;
        }
    }

    /// <summary>Where sign-release reads the releases from: the public site (https) or a local files\releases folder.</summary>
    internal abstract class ReleaseSource
    {
        public const int MaxBytes = 64 * 1024 * 1024;

        public abstract string Describe { get; }

        public static ReleaseSource Create(string from)
        {
            if (from.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || from.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpSource(from);
            }

            return new DirSource(from);
        }

        public byte[] Get(string rel) => TryGet(rel) ?? throw new FileNotFoundException(Describe + ": " + rel + " not found");

        /// <summary>Null when the file does not exist.</summary>
        public abstract byte[] TryGet(string rel);

        protected static void CheckRel(string rel)
        {
            if (rel.Contains("..") || rel.Contains("\\") || rel.StartsWith("/", StringComparison.Ordinal) || rel.Contains(":"))
            {
                throw new ArgumentException("bad relative path " + rel);
            }
        }

        private sealed class DirSource : ReleaseSource
        {
            private readonly string root;

            public DirSource(string root)
            {
                this.root = Path.GetFullPath(root);
                if (!Directory.Exists(this.root))
                {
                    throw new DirectoryNotFoundException("no folder " + this.root);
                }
            }

            public override string Describe => this.root;

            public override byte[] TryGet(string rel)
            {
                CheckRel(rel);
                string p = Path.Combine(root, rel.Replace('/', '\\'));
                return File.Exists(p) ? File.ReadAllBytes(p) : null;
            }
        }

        private sealed class HttpSource : ReleaseSource
        {
            private readonly Uri baseUri;

            public HttpSource(string site)
            {
                var u = new Uri(site.TrimEnd('/') + "/files/releases/");
                bool loopback = u.IsLoopback || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
                if (u.Scheme != Uri.UriSchemeHttps && !loopback)
                {
                    throw new UsageException("--from must be https:// (http only for localhost tests)");
                }

                baseUri = u;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }

            public override string Describe => baseUri.ToString();

            public override byte[] TryGet(string rel)
            {
                CheckRel(rel);
                var req = (HttpWebRequest)WebRequest.Create(new Uri(baseUri, rel));
                req.Method = "GET";
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                req.AllowAutoRedirect = false;
                req.UserAgent = "lotr-publish (team signing)";
                req.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        var buf = new byte[81920];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (ms.Length + n > MaxBytes)
                            {
                                throw new InvalidDataException(rel + " is larger than " + (MaxBytes >> 20) + " MB");
                            }

                            ms.Write(buf, 0, n);
                        }

                        return ms.ToArray();
                    }
                }
                catch (WebException e) when (e.Response is HttpWebResponse r)
                {
                    int code = (int)r.StatusCode;
                    r.Dispose();
                    if (code == 404)
                    {
                        return null;
                    }

                    if (code == 423)
                    {
                        throw new InvalidOperationException("the site is in battle-night mode (kill switch ON): sign after the battle");
                    }

                    throw new InvalidOperationException(new Uri(baseUri, rel) + ": HTTP " + code.ToString(CultureInfo.InvariantCulture));
                }
            }
        }
    }
}
