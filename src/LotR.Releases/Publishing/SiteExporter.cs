using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;

namespace LotR.Releases.Publishing
{
    /// <summary>
    /// Writes the website's build-time data file (CONTRACTS §5), site/src/data/releases.json:
    /// <code>
    /// { "current": null | { releaseId, channel, publishedAt, notes, game, signature: { keyId, fingerprint, verified },
    ///                       modules: [ { id, name, version, role, folder, size, files, download } ],
    ///                       size, download, security: { virustotal: { files, detections, scanned }, hybridAnalysis: { files, verdicts } } },
    ///   "updater": null | latest.json,
    ///   "history": [ { releaseId, publishedAt, versions: { moduleId: version } } ]   (newest first, at most 10) }
    /// </code>
    /// "download" is the stored bytes a fresh install fetches (distinct objects, chunk indexes included). "security" summarises the unsigned sidecar
    /// &lt;release&gt;/security.json (CONTRACTS §8; only entries with status "done" count; null when absent).
    /// Pretty-printed (it is read by people too).
    /// </summary>
    public static class SiteExporter
    {
        public static JsonObject Build(string releasesRoot, string channel, IEnumerable<TrustedKey> trusted, string updaterLatestPath, List<string> warnings = null)
        {
            var trustedList = trusted?.ToList() ?? new List<TrustedKey>();
            var channels = ChannelIndex.Load(Path.Combine(releasesRoot, ReleaseFolder.ChannelsFile));
            string id = channels.Get(channel);
            JsonNode current = JsonNull.Instance;
            if (id != null)
            {
                var rel = ReleaseFolder.Open(Path.Combine(releasesRoot, id));
                current = Current(rel, trustedList, warnings);
            }

            JsonNode updater = JsonNull.Instance;
            if (updaterLatestPath != null && File.Exists(updaterLatestPath))
            {
                var latest = UpdaterLatest.Parse(File.ReadAllBytes(updaterLatestPath));
                var v = latest.Validate();
                if (!v.IsValid)
                {
                    warnings?.Add("updater latest.json: " + string.Join("; ", v.Errors));
                }

                updater = latest.ToJson();
            }

            var history = new JsonArray();
            if (Directory.Exists(releasesRoot))
            {
                var all = new List<ReleaseManifest>();
                foreach (var dir in Directory.GetDirectories(releasesRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (!ManifestValidator.ReleaseIdPattern.IsMatch(name) || !File.Exists(Path.Combine(dir, ReleaseFolder.ManifestFile)))
                    {
                        continue;
                    }

                    try
                    {
                        all.Add(ReleaseFolder.Open(dir).Manifest);
                    }
                    catch (Exception e) when (e is IOException || e is FormatException)
                    {
                        warnings?.Add("history: skipped " + name + ": " + e.Message);
                    }
                }

                foreach (var m in all.Where(m => m.Channel == channel).OrderByDescending(m => m.PublishedAt, StringComparer.Ordinal).ThenByDescending(m => m.ReleaseId, StringComparer.Ordinal).Take(10))
                {
                    var versions = new JsonObject();
                    foreach (var mod in m.Modules)
                    {
                        versions[mod.Id] = mod.Version;
                    }

                    history.Add(new JsonObject { ["releaseId"] = m.ReleaseId, ["publishedAt"] = m.PublishedAt, ["versions"] = versions });
                }
            }

            return new JsonObject { ["current"] = current, ["updater"] = updater, ["history"] = history };
        }

        public static void Write(string outPath, JsonObject data) => AtomicFile.Write(outPath, JsonWriter.ToPrettyBytes(data));

        private static JsonObject Current(ReleaseFolder rel, List<TrustedKey> trusted, List<string> warnings)
        {
            var m = rel.Manifest;
            JsonObject signature;
            if (rel.SignatureBytes == null)
            {
                signature = new JsonObject { ["keyId"] = null, ["fingerprint"] = null, ["verified"] = false };
                warnings?.Add("current release has no signature file");
            }
            else
            {
                var check = trusted.Count > 0 ? ManifestSignature.Verify(rel.ManifestBytes, rel.SignatureBytes, trusted) : null;
                string keyId = check?.KeyId ?? ManifestSignature.Parse(rel.SignatureBytes).Select(s => s.KeyId).FirstOrDefault();
                if (check != null && !check.Valid)
                {
                    warnings?.Add("current release signature: " + check.Message);
                }

                signature = new JsonObject
                {
                    ["keyId"] = keyId,
                    ["fingerprint"] = keyId == null ? null : EcKeys.FormatFingerprint(keyId),
                    ["verified"] = check != null && check.Valid,
                };
            }

            var notes = new JsonObject();
            foreach (var kv in m.Notes)
            {
                notes[kv.Key] = kv.Value;
            }

            return new JsonObject
            {
                ["releaseId"] = m.ReleaseId,
                ["channel"] = m.Channel,
                ["publishedAt"] = m.PublishedAt,
                ["notes"] = notes,
                ["game"] = new JsonObject { ["minVersion"] = m.Game?.MinVersion, ["maxVersion"] = m.Game?.MaxVersion },
                ["signature"] = signature,
                ["size"] = m.TotalSize,
                ["download"] = rel.CollectObjects().Values.Sum(o => o.ObjectSize),
                ["modules"] = new JsonArray(m.Modules.Select(mod => (JsonNode)new JsonObject
                {
                    ["id"] = mod.Id,
                    ["name"] = mod.Name,
                    ["version"] = mod.Version,
                    ["role"] = mod.Role,
                    ["folder"] = mod.Folder,
                    ["size"] = mod.TotalSize,
                    ["files"] = mod.Files.Count,
                    ["download"] = rel.CollectObjects(mod).Values.Sum(o => o.ObjectSize),
                    ["workshopItem"] = mod.WorkshopItem,
                })),
                ["security"] = SecuritySummary(SecuritySidecar.Load(rel.Path, warnings)),
            };
        }

        // Entries without a status (manifest-block output) count as done.
        private static List<JsonObject> Done(JsonArray entries) =>
            entries.OfType<JsonObject>().Where(e => (e["status"]?.AsString() ?? "done") == "done").ToList();

        private static JsonNode SecuritySummary(JsonNode security)
        {
            var s = security as JsonObject;
            if (s == null)
            {
                return JsonNull.Instance;
            }

            var result = new JsonObject();
            result["updatedAt"] = s["updatedAt"]?.AsString();
            if (s["virustotal"] is JsonArray vtAll)
            {
                var vt = Done(vtAll);
                long detections = 0;
                long suspicious = 0;
                foreach (var e in vt)
                {
                    detections += e["detections"]?.AsLong() ?? 0;
                    suspicious += e["suspicious"]?.AsLong() ?? 0;
                }

                result["virustotal"] = new JsonObject
                {
                    ["files"] = vt.Count,
                    ["pending"] = vtAll.Count - vt.Count,
                    ["detections"] = detections,
                    ["suspicious"] = suspicious,
                    ["reports"] = new JsonArray(vt.Select(e => (JsonNode)new JsonObject
                    {
                        ["sha256"] = e["sha256"]?.AsString(),
                        ["url"] = e["url"]?.AsString(),
                        ["detections"] = e["detections"]?.AsLong() ?? 0,
                        ["engines"] = e["engines"]?.AsLong() ?? 0,
                        ["scannedAt"] = e["scannedAt"]?.AsString(),
                    })),
                };
            }

            if (s["hybridAnalysis"] is JsonArray haAll)
            {
                var ha = Done(haAll);
                var verdicts = new JsonObject();
                foreach (var g in ha.GroupBy(e => e["verdict"]?.AsString() ?? "unknown"))
                {
                    verdicts[g.Key] = g.Count();
                }

                result["hybridAnalysis"] = new JsonObject
                {
                    ["files"] = ha.Count,
                    ["pending"] = haAll.Count - ha.Count,
                    ["verdicts"] = verdicts,
                    ["reports"] = new JsonArray(ha.Select(e => (JsonNode)new JsonObject
                    {
                        ["sha256"] = e["sha256"]?.AsString(),
                        ["url"] = e["url"]?.AsString(),
                        ["verdict"] = e["verdict"]?.AsString(),
                    })),
                };
            }

            return result;
        }
    }

    /// <summary>
    /// &lt;release&gt;/security.json: scan results written by tools/security (lotr-security), NOT signed, updated
    /// asynchronously (CONTRACTS §8). Absence or a broken file never blocks anything.
    /// </summary>
    public static class SecuritySidecar
    {
        public const string FileName = "security.json";

        public static JsonObject Load(string releaseDir, List<string> warnings = null)
        {
            string path = Path.Combine(releaseDir, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonParser.Parse(File.ReadAllBytes(path)) as JsonObject;
            }
            catch (Exception e) when (e is IOException || e is FormatException)
            {
                warnings?.Add("security.json unreadable: " + e.Message);
                return null;
            }
        }
    }
}
