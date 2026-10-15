using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LotR.Releases.Json;

namespace LotR.Releases.Model
{
    /// <summary>
    /// releases/channels.json: { "stable": "&lt;releaseId&gt;", "beta": "&lt;releaseId&gt;" }. Unsigned convenience index:
    /// the updater must still verify the manifest it points to, and check the manifest's own (signed) channel.
    /// </summary>
    public sealed class ChannelIndex
    {
        public SortedDictionary<string, string> Channels { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public string Get(string channel) => Channels.TryGetValue(channel, out var id) ? id : null;

        public byte[] ToBytes()
        {
            var o = new JsonObject();
            foreach (var kv in Channels)
            {
                o[kv.Key] = kv.Value;
            }

            return o.ToCanonicalBytes();
        }

        public static ChannelIndex Parse(byte[] utf8)
        {
            var o = JsonParser.Parse(utf8) as JsonObject ?? throw new ManifestFormatException("channels.json: not an object");
            var c = new ChannelIndex();
            foreach (var kv in o)
            {
                if (!ManifestValidator.ChannelPattern.IsMatch(kv.Key))
                {
                    throw new ManifestFormatException("channels.json: bad channel name '" + kv.Key + "'");
                }

                if (kv.Value.IsNull)
                {
                    continue;
                }

                string id = kv.Value.AsString();
                if (id == null || !ManifestValidator.ReleaseIdPattern.IsMatch(id))
                {
                    throw new ManifestFormatException("channels.json: '" + kv.Key + "' does not hold a release id");
                }

                c.Channels[kv.Key] = id;
            }

            return c;
        }

        public static ChannelIndex Load(string path) => File.Exists(path) ? Parse(File.ReadAllBytes(path)) : new ChannelIndex();
    }

    /// <summary>updater/latest.json: { "version", "url", "sha256", "size", "releasedAt", "signed": bool }.</summary>
    public sealed class UpdaterLatest
    {
        private static readonly Regex VersionPattern = new Regex(@"^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

        public string Version { get; set; }

        public string Url { get; set; }

        public string Sha256 { get; set; }

        public long Size { get; set; }

        public string ReleasedAt { get; set; }

        /// <summary>Authenticode-signed exe (not the manifest signature).</summary>
        public bool Signed { get; set; }

        public JsonObject ToJson() => new JsonObject
        {
            ["version"] = Version,
            ["url"] = Url,
            ["sha256"] = Sha256,
            ["size"] = Size,
            ["releasedAt"] = ReleasedAt,
            ["signed"] = Signed,
        };

        public static UpdaterLatest FromJson(JsonNode node)
        {
            var o = node as JsonObject ?? throw new ManifestFormatException("latest.json: not an object");
            return new UpdaterLatest
            {
                Version = o["version"]?.AsString(),
                Url = o["url"]?.AsString(),
                Sha256 = o["sha256"]?.AsString(),
                Size = o["size"]?.AsLong() ?? -1,
                ReleasedAt = o["releasedAt"]?.AsString(),
                Signed = o["signed"]?.AsBool() ?? false,
            };
        }

        public static UpdaterLatest Parse(byte[] utf8) => FromJson(JsonParser.Parse(utf8));

        public ValidationResult Validate()
        {
            var r = new ValidationResult();
            if (Version == null || !VersionPattern.IsMatch(Version))
            {
                r.Errors.Add("version '" + Version + "' is not like 1.2.3");
            }

            if (Url == null || !(Url.StartsWith("/", StringComparison.Ordinal) || Url.StartsWith("https://", StringComparison.Ordinal)))
            {
                r.Errors.Add("url must be site-relative (/files/updater/...) or https");
            }

            if (Sha256 == null || !ManifestValidator.Sha256Pattern.IsMatch(Sha256))
            {
                r.Errors.Add("sha256 must be 64 lowercase hex characters");
            }

            if (Size <= 0)
            {
                r.Errors.Add("size must be positive");
            }

            if (string.IsNullOrEmpty(ReleasedAt))
            {
                r.Warnings.Add("releasedAt is empty");
            }

            return r;
        }
    }

    /// <summary>
    /// "&lt;file&gt;.sha256" companion files (updater exe, deploy package): one line "&lt;hex&gt;  &lt;filename&gt;" (two
    /// spaces, sha256sum text mode), LF (CONTRACTS §8, 15/10).
    /// </summary>
    public static class Sha256SumFile
    {
        public static string Format(string sha256, string fileName) => sha256.ToLowerInvariant() + "  " + fileName + "\n";

        /// <summary>Returns (sha256, fileName) or throws FormatException. Accepts CRLF and the binary-mode '*' marker.</summary>
        public static (string Sha256, string FileName) Parse(string text)
        {
            string line = (text ?? string.Empty).Split('\n')[0].TrimEnd('\r');
            int sep = line.IndexOf(' ');
            if (sep != 64 || line.Length < 67)
            {
                throw new FormatException(".sha256 file is not '<64 hex>  <file name>'");
            }

            string sha = line.Substring(0, 64).ToLowerInvariant();
            string rest = line.Substring(65);
            if (rest.StartsWith(" ", StringComparison.Ordinal) || rest.StartsWith("*", StringComparison.Ordinal))
            {
                rest = rest.Substring(1);
            }

            if (!ManifestValidator.Sha256Pattern.IsMatch(sha) || rest.Length == 0)
            {
                throw new FormatException(".sha256 file is not '<64 hex>  <file name>'");
            }

            return (sha, rest);
        }
    }
}
