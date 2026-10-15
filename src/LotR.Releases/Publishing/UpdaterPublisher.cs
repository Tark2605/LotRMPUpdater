using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.Releases.Hashing;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;

namespace LotR.Releases.Publishing
{
    public sealed class UpdaterPublishRequest
    {
        /// <summary>The LotRMPUpdater.exe to publish (signed with Authenticode first when a certificate exists).</summary>
        public string ExePath { get; set; }

        /// <summary>The files\updater folder (default &lt;root&gt;\files\updater).</summary>
        public string UpdaterDir { get; set; }

        /// <summary>stable → latest.json; any other channel → latest-&lt;channel&gt;.json.</summary>
        public string Channel { get; set; } = "stable";

        /// <summary>Overrides the version read from the exe's file version.</summary>
        public string Version { get; set; }

        /// <summary>When set, latest.json's url is https://domain/files/updater/...; else site-relative.</summary>
        public string BaseUrl { get; set; }

        /// <summary>Release keys: latest.json.sig is written with them (same format as manifest.json.sig).</summary>
        public List<ReleaseSigner> Signers { get; } = new List<ReleaseSigner>();

        public DateTime? NowUtc { get; set; }

        /// <summary>Replace an already published exe of the same version with different bytes.</summary>
        public bool Force { get; set; }
    }

    public sealed class UpdaterPublishResult
    {
        public UpdaterLatest Latest { get; set; }

        public string ExeFile { get; set; }

        public string Sha256File { get; set; }

        public string LatestFile { get; set; }

        public string SignatureFile { get; set; }

        public AuthenticodeInfo Authenticode { get; set; }

        public bool AlreadyPublished { get; set; }
    }

    /// <summary>
    /// Publishes the updater exe (CONTRACTS §1 files\updater): LotRMPUpdater-&lt;version&gt;.exe, its .sha256
    /// ("&lt;hex&gt;  &lt;name&gt;" LF) and latest.json (+ latest.json.sig when a release key is given). Each file is written
    /// to a temporary name and renamed; latest.json is written LAST, so a client never sees a latest.json pointing at
    /// an exe that is not fully there.
    /// </summary>
    public static class UpdaterPublisher
    {
        public const string ExeBaseName = "LotRMPUpdater";
        private static readonly Regex VersionPattern = new Regex(@"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

        public static string LatestFileName(string channel) =>
            string.IsNullOrEmpty(channel) || channel == "stable" ? "latest.json" : "latest-" + channel + ".json";

        public static string ExeFileName(string version) => ExeBaseName + "-" + version + ".exe";

        /// <summary>"0.1.0.0" → "0.1.0"; keeps a 4th part only when it is not 0.</summary>
        public static string VersionOf(string exePath)
        {
            var fv = FileVersionInfo.GetVersionInfo(exePath);
            string v = fv.ProductVersion;
            if (!string.IsNullOrEmpty(v))
            {
                int plus = v.IndexOf('+');
                if (plus > 0)
                {
                    v = v.Substring(0, plus);
                }
            }

            if (string.IsNullOrEmpty(v) || !VersionPattern.IsMatch(v))
            {
                v = fv.FileVersion;
            }

            if (string.IsNullOrEmpty(v))
            {
                throw new InvalidOperationException(exePath + " has no version resource; pass --version");
            }

            var parts = v.Split('.');
            if (parts.Length == 4 && parts[3] == "0")
            {
                v = string.Join(".", parts.Take(3));
            }

            return v;
        }

        public static UpdaterPublishResult Publish(UpdaterPublishRequest req)
        {
            if (req.ExePath == null || !File.Exists(req.ExePath))
            {
                throw new FileNotFoundException("updater exe not found", req.ExePath);
            }

            if (string.IsNullOrEmpty(req.UpdaterDir))
            {
                throw new ArgumentException("UpdaterDir is required");
            }

            if (!Regex.IsMatch(req.Channel ?? "stable", "^[a-z][a-z0-9-]{0,31}$"))
            {
                throw new ArgumentException("channel must be lowercase letters, digits or '-'");
            }

            string version = req.Version ?? VersionOf(req.ExePath);
            if (!VersionPattern.IsMatch(version))
            {
                throw new ArgumentException("version '" + version + "' is not like 1.2.3");
            }

            Directory.CreateDirectory(req.UpdaterDir);
            string name = ExeFileName(version);
            string exeOut = Path.Combine(req.UpdaterDir, name);
            byte[] exe = File.ReadAllBytes(req.ExePath);
            string sha = Sha256Util.Of(exe);
            var result = new UpdaterPublishResult { ExeFile = exeOut, Sha256File = exeOut + ".sha256", LatestFile = Path.Combine(req.UpdaterDir, LatestFileName(req.Channel)) };

            AuthenticodeInfo auth;
            try
            {
                auth = Authenticode.Inspect(req.ExePath);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                auth = AuthenticodeInfo.Unsigned("could not check: " + ex.Message);
            }

            if (auth.Signed && !auth.Valid)
            {
                throw new InvalidOperationException("the exe carries an INVALID Authenticode signature (" + auth.Status + "); refusing to publish it");
            }

            result.Authenticode = auth;

            if (File.Exists(exeOut))
            {
                string existing = Sha256Util.OfFile(exeOut);
                if (existing == sha)
                {
                    result.AlreadyPublished = true;
                }
                else if (!req.Force)
                {
                    throw new InvalidOperationException(name + " is already published with different bytes; bump the version (or --force)");
                }
            }

            if (!result.AlreadyPublished || req.Force)
            {
                AtomicFile.Write(exeOut, exe);
            }

            AtomicFile.Write(result.Sha256File, System.Text.Encoding.ASCII.GetBytes(Sha256SumFile.Format(sha, name)));

            string url = "/files/updater/" + name;
            if (!string.IsNullOrEmpty(req.BaseUrl))
            {
                if (!req.BaseUrl.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new ArgumentException("--base-url must be https://");
                }

                url = req.BaseUrl.TrimEnd('/') + url;
            }

            var latest = new UpdaterLatest
            {
                Version = version,
                Url = url,
                Sha256 = sha,
                Size = exe.LongLength,
                ReleasedAt = (req.NowUtc ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Signed = auth.Valid,
            };
            var val = latest.Validate();
            if (!val.IsValid)
            {
                throw new InvalidOperationException("latest.json would be invalid: " + string.Join("; ", val.Errors));
            }

            byte[] latestBytes = latest.ToJson().ToCanonicalBytes();
            if (req.Signers.Count > 0)
            {
                // Signature first, then latest.json: an updater that sees the new latest.json also finds its signature.
                result.SignatureFile = result.LatestFile + ".sig";
                AtomicFile.Write(result.SignatureFile, ManifestSignature.Create(latestBytes, req.Signers));
            }
            else if (File.Exists(result.LatestFile + ".sig"))
            {
                // A signature of the PREVIOUS latest.json would make every updater refuse the new one.
                File.Delete(result.LatestFile + ".sig");
            }

            AtomicFile.Write(result.LatestFile, latestBytes);
            result.Latest = latest;
            return result;
        }
    }
}
