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
    ///   "updaterLinux": null | linux/latest.json   (same shape as "updater"; null = no Linux updater published yet),
    ///   "history": [ { releaseId, publishedAt, versions: { moduleId: version } } ]   (newest first, at most 10) }
    /// </code>
    /// "download" is the stored bytes a fresh install fetches (distinct objects, chunk indexes included). "security" summarises the unsigned sidecar
    /// &lt;release&gt;/security.json (CONTRACTS §8; only entries with status "done" count; null when absent).
    /// "updaterLinux" is shown only when files\updater\linux\latest.json is valid, points at this server's
    /// lotr-updater-&lt;version&gt;-linux-x64.tar.gz and (when a trusted key is given) carries a valid release-key signature;
    /// anything else is a warning and null, so the Install page never offers a Linux download the team did not sign.
    /// Pretty-printed (it is read by people too).
    /// </summary>
    public static class SiteExporter
    {
        /// <param name="updaterLinuxLatestPath">files\updater\linux\latest.json; default: linux\latest.json next to <paramref name="updaterLatestPath"/>.</param>
        public static JsonObject Build(string releasesRoot, string channel, IEnumerable<TrustedKey> trusted, string updaterLatestPath, List<string> warnings = null, string updaterLinuxLatestPath = null)
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

            if (updaterLinuxLatestPath == null && updaterLatestPath != null)
            {
                updaterLinuxLatestPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(updaterLatestPath)), LinuxUpdaterArchive.Folder, UpdaterPublisher.LatestFileName("stable"));
            }

            JsonNode updaterLinux = LinuxUpdater(updaterLinuxLatestPath, trustedList, warnings);

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

            return new JsonObject { ["current"] = current, ["updater"] = updater, ["updaterLinux"] = updaterLinux, ["history"] = history };
        }

        public static void Write(string outPath, JsonObject data) => AtomicFile.Write(outPath, JsonWriter.ToPrettyBytes(data));

        /// <summary>linux\latest.json as the site shows it, or JSON null (absent, broken, foreign url, bad signature: warned).</summary>
        private static JsonNode LinuxUpdater(string path, List<TrustedKey> trusted, List<string> warnings)
        {
            if (path == null || !File.Exists(path))
            {
                return JsonNull.Instance;
            }

            const string What = "linux updater latest.json";
            byte[] bytes;
            UpdaterLatest latest;
            try
            {
                bytes = File.ReadAllBytes(path);
                latest = UpdaterLatest.Parse(bytes);
            }
            catch (Exception e) when (e is IOException || e is FormatException)
            {
                warnings?.Add(What + " unreadable, Linux download not shown: " + e.Message);
                return JsonNull.Instance;
            }

            var v = latest.Validate();
            if (!v.IsValid)
            {
                warnings?.Add(What + ", Linux download not shown: " + string.Join("; ", v.Errors));
                return JsonNull.Instance;
            }

            if (!LinuxUpdaterArchive.IsOwnUrl(latest.Url, latest.Version))
            {
                warnings?.Add(What + ": url " + latest.Url + " is not this server's " + LinuxUpdaterArchive.UrlPath(latest.Version) + ", Linux download not shown");
                return JsonNull.Instance;
            }

            if (trusted.Count == 0)
            {
                warnings?.Add(What + ": no trusted public key to check its signature with (shown unverified)");
                return latest.ToJson();
            }

            string sigPath = path + ".sig";
            var check = File.Exists(sigPath)
                ? ManifestSignature.Verify(bytes, File.ReadAllBytes(sigPath), trusted)
                : new SignatureCheck { Valid = false, Message = "no latest.json.sig" };
            if (!check.Valid)
            {
                warnings?.Add(What + " signature: " + check.Message + ", Linux download not shown");
                return JsonNull.Instance;
            }

            return latest.ToJson();
        }

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
                ["security"] = SecuritySummary(SecuritySidecar.Load(rel.Path, warnings), SecuritySidecar.Load(rel.Path, warnings, SecuritySidecar.DefenderFileName)),
            };
        }

        // Entries without a status (manifest-block output) count as done.
        private static List<JsonObject> Done(JsonArray entries) =>
            entries.OfType<JsonObject>().Where(e => (e["status"]?.AsString() ?? "done") == "done").ToList();

        private static JsonNode SecuritySummary(JsonNode security, JsonObject defender = null)
        {
            var s = security as JsonObject;
            if (s == null && defender == null)
            {
                return JsonNull.Instance;
            }

            s = s ?? new JsonObject();
            var result = new JsonObject();
            result["updatedAt"] = s["updatedAt"]?.AsString();
            if (defender != null)
            {
                // Local Microsoft Defender scan of the exact published files (tools/security/Invoke-DefenderScan.ps1 -> defender.json).
                var engine = defender["engine"] as JsonObject;
                var u = defender["updater"] as JsonObject;
                result["defender"] = new JsonObject
                {
                    ["status"] = defender["status"]?.AsString(),
                    ["result"] = defender["result"]?.AsString(),
                    ["scannedAt"] = defender["scannedAt"]?.AsString(),
                    ["engineVersion"] = engine?["engineVersion"]?.AsString(),
                    ["signatureVersion"] = engine?["signatureVersion"]?.AsString(),
                    ["modules"] = new JsonArray((defender["modules"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(m => (JsonNode)new JsonObject
                    {
                        ["folder"] = m["folder"]?.AsString(),
                        ["version"] = m["version"]?.AsString(),
                        ["files"] = m["files"]?.AsLong() ?? 0,
                        ["result"] = m["result"]?.AsString(),
                        ["threats"] = new JsonArray((m["threats"] as JsonArray ?? new JsonArray()).Select(x => (JsonNode)new JsonString(x.AsString() ?? "?"))),
                    })),
                    ["updater"] = u == null ? (JsonNode)JsonNull.Instance : new JsonObject { ["file"] = u["file"]?.AsString(), ["result"] = u["result"]?.AsString() },
                };
            }
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

        /// <summary>&lt;release&gt;/defender.json: local Microsoft Defender scan (tools/security/Invoke-DefenderScan.ps1), same rules.</summary>
        public const string DefenderFileName = "defender.json";

        public static JsonObject Load(string releaseDir, List<string> warnings = null, string fileName = FileName)
        {
            string path = Path.Combine(releaseDir, fileName);
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
                warnings?.Add(fileName + " unreadable: " + e.Message);
                return null;
            }
        }
    }
}
