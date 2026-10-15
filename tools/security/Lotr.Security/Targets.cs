using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Lotr.Security;

/// <summary>Builds the list of files to scan from a release manifest and/or explicit files (the updater exe).</summary>
public static class Targets
{
    public sealed class ManifestInfo
    {
        public string ReleaseId;
        public List<ScanItem> Items = new List<ScanItem>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Reads every <c>exec: true</c> file of docs/CONTRACTS.md §2. The local copy used for an upload is looked up in
    /// <paramref name="objectsDir"/> (objects\&lt;sha[0:2]&gt;\&lt;object&gt;, only for raw objects) then in
    /// <paramref name="sourceModulesDir"/> (&lt;folder&gt;\&lt;path&gt;). The hash is always re-checked before use.
    /// </summary>
    public static ManifestInfo FromManifest(string manifestPath, string objectsDir, string sourceModulesDir)
    {
        var info = new ManifestInfo();
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8)))
        {
            JsonElement root = doc.RootElement;
            info.ReleaseId = root.Str("releaseId");
            if (!root.TryGet("modules", out JsonElement modules) || modules.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("manifest has no \"modules\" array: " + manifestPath);

            var byHash = new Dictionary<string, ScanItem>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement m in modules.EnumerateArray())
            {
                string folder = m.Str("folder") ?? m.Str("name") ?? m.Str("id") ?? "?";
                if (!m.TryGet("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement f in files.EnumerateArray())
                {
                    if (!(f.TryGet("exec", out JsonElement ex) && ex.ValueKind == JsonValueKind.True)) continue;
                    string sha = f.Str("sha256")?.ToLowerInvariant();
                    string path = f.Str("path");
                    if (!Hashing.IsSha256(sha))
                    {
                        info.Warnings.Add($"{folder}/{path}: invalid sha256, ignored");
                        continue;
                    }
                    if (!byHash.TryGetValue(sha, out ScanItem item))
                    {
                        item = new ScanItem { Sha256 = sha, Size = f.Long("size") };
                        byHash[sha] = item;
                        info.Items.Add(item);
                    }
                    item.Files.Add(folder + "/" + path);
                    if (item.LocalPath == null)
                        item.LocalPath = FindLocal(sha, f.Str("object"), folder, path, objectsDir, sourceModulesDir, info.Warnings);
                }
            }
        }
        return info;
    }

    private static string FindLocal(string sha, string obj, string folder, string path, string objectsDir, string sourceModulesDir, List<string> warnings)
    {
        if (objectsDir != null && obj != null)
        {
            // Objects are .zst or .raw (CONTRACTS §1, decided 15/10); only .raw is byte-identical to the installed file.
            // A compressed object is NOT the file players run, so it must not be uploaded as-is.
            if (obj.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            {
                string p = Path.Combine(objectsDir, sha.Substring(0, 2), obj);
                if (File.Exists(p)) return p;
            }
            else
            {
                warnings.Add($"{folder}/{path}: object '{obj}' is not raw; use --source-modules to upload the original file");
            }
        }
        if (sourceModulesDir != null && path != null)
        {
            string p = Path.Combine(sourceModulesDir, folder, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static ScanItem FromFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("file to scan not found", path);
        return new ScanItem
        {
            Sha256 = Hashing.Sha256File(fi.FullName),
            Size = fi.Length,
            LocalPath = fi.FullName,
            Files = new List<string> { fi.Name },
        };
    }

    /// <summary>Merges a fresh target list into a saved job, keeping progress for hashes already known.</summary>
    public static void MergeInto(JobState job, IEnumerable<ScanItem> fresh)
    {
        foreach (ScanItem f in fresh)
        {
            ScanItem existing = job.Items.FirstOrDefault(i => string.Equals(i.Sha256, f.Sha256, StringComparison.OrdinalIgnoreCase));
            if (existing == null) { job.Items.Add(f); continue; }
            foreach (string name in f.Files) if (!existing.Files.Contains(name)) existing.Files.Add(name);
            if (f.LocalPath != null) existing.LocalPath = f.LocalPath;
            if (f.Size.HasValue) existing.Size = f.Size;
            // An item that previously failed or was skipped for lack of a local copy gets another chance.
            if (existing.Phase == ScanPhase.Error || (existing.Phase == ScanPhase.Skipped && f.LocalPath != null && existing.Note != null && existing.Note.StartsWith("no local copy")))
                existing.Phase = ScanPhase.Pending;
        }
    }
}
