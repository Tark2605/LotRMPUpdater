using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lotr.Security;

public enum ScanPhase { Pending, Submitted, Done, Skipped, Error }

/// <summary>One distinct file (by SHA-256) to scan. Several manifest entries can share the same hash.</summary>
public sealed class ScanItem
{
    public string Sha256 { get; set; }
    /// <summary>Human-readable origins, e.g. "LotRMPEvent/bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll".</summary>
    public List<string> Files { get; set; } = new List<string>();
    /// <summary>Local copy used for uploads (object file or source module file); null = lookup only.</summary>
    public string LocalPath { get; set; }
    public long? Size { get; set; }
    public ScanPhase Phase { get; set; }
    public string Note { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    /// <summary>Name sent with an upload: the player-facing file name, never the "&lt;sha&gt;.raw" object name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UploadName =>
        Files.Count > 0 ? Path.GetFileName(Files[0].Replace('/', Path.DirectorySeparatorChar)) : Path.GetFileName(LocalPath);

    // VirusTotal
    public string AnalysisId { get; set; }
    public VtEntry Vt { get; set; }

    // Hybrid Analysis
    public string QuickScanId { get; set; }
    public string JobId { get; set; }
    public HaEntry Ha { get; set; }
}

/// <summary>Resumable job, saved under &lt;state-dir&gt;\jobs\ after every step (never in the public release folder).</summary>
public sealed class JobState
{
    public int Schema { get; set; } = 1;
    public string Service { get; set; }
    public string ReleaseId { get; set; }
    public string OutPath { get; set; }
    public List<ScanItem> Items { get; set; } = new List<ScanItem>();
}

public sealed class FlaggedBy
{
    public string Engine { get; set; }
    public string Category { get; set; }
    public string Result { get; set; }
}

/// <summary>
/// VirusTotal entry of security.json. The first five properties are exactly the manifest contract
/// (docs/CONTRACTS.md §2 "security.virustotal"); the others are extra detail for the site and for humans.
/// </summary>
public sealed class VtEntry
{
    public string Sha256 { get; set; }
    public string Url { get; set; }
    public int? Detections { get; set; }
    public int? Engines { get; set; }
    public string ScannedAt { get; set; }
    public int? Suspicious { get; set; }
    public List<FlaggedBy> FlaggedBy { get; set; }
    public List<string> Files { get; set; }
    /// <summary>done | pending | skipped | error</summary>
    public string Status { get; set; }
    public string Note { get; set; }
}

/// <summary>Hybrid Analysis entry; sha256/url/verdict are the manifest contract fields.</summary>
public sealed class HaEntry
{
    public string Sha256 { get; set; }
    public string Url { get; set; }
    public string Verdict { get; set; }
    public int? ThreatScore { get; set; }
    /// <summary>existing-report | quick-scan | sandbox</summary>
    public string Source { get; set; }
    public string ScannedAt { get; set; }
    public string Environment { get; set; }
    public List<string> Files { get; set; }
    public string Status { get; set; }
    public string Note { get; set; }
}

/// <summary>
/// security.json — the sidecar written next to a release's manifest (or next to the updater exe).
/// The publish tool merges its "done" entries into manifest.security (REVIEW-AFTER(publish-cli)); the site reads it.
/// </summary>
public sealed class SecuritySidecar
{
    public int Schema { get; set; } = 1;
    public string ReleaseId { get; set; }
    public string UpdatedAt { get; set; }
    public List<VtEntry> Virustotal { get; set; } = new List<VtEntry>();
    public List<HaEntry> HybridAnalysis { get; set; } = new List<HaEntry>();

    public static SecuritySidecar Load(string path) => Json.Read<SecuritySidecar>(path) ?? new SecuritySidecar();

    /// <summary>Upserts by sha256: entries of other files (e.g. the updater scanned separately) are kept.</summary>
    public void Upsert(IEnumerable<VtEntry> entries)
    {
        foreach (VtEntry e in entries)
        {
            Virustotal.RemoveAll(x => string.Equals(x.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase));
            Virustotal.Add(e);
        }
        Virustotal = Virustotal.OrderBy(x => x.Sha256, StringComparer.Ordinal).ToList();
    }

    public void Upsert(IEnumerable<HaEntry> entries)
    {
        foreach (HaEntry e in entries)
        {
            HybridAnalysis.RemoveAll(x => string.Equals(x.Sha256, e.Sha256, StringComparison.OrdinalIgnoreCase));
            HybridAnalysis.Add(e);
        }
        HybridAnalysis = HybridAnalysis.OrderBy(x => x.Sha256, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The exact "security" block of docs/CONTRACTS.md §2: only finished results, only contract fields.
    /// The publish tool should call this (or reproduce it) when merging. REVIEW-AFTER(publish-cli)
    /// </summary>
    public object ToManifestBlock() => new
    {
        virustotal = Virustotal.Where(v => v.Status == "done").Select(v => new
        {
            sha256 = v.Sha256, url = v.Url, detections = v.Detections ?? 0, engines = v.Engines ?? 0, scannedAt = v.ScannedAt,
        }).ToList(),
        hybridAnalysis = HybridAnalysis.Where(h => h.Status == "done").Select(h => new
        {
            sha256 = h.Sha256, url = h.Url, verdict = h.Verdict,
        }).ToList(),
    };
}

public static class Paths
{
    /// <summary>Default per-machine state folder (quota counters + resumable jobs). Not served by Caddy.</summary>
    public static string DefaultStateDir =>
        Environment.GetEnvironmentVariable("LOTR_SECURITY_STATE_DIR") is string s && s.Length > 0
            ? s
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LotRMP", "security");

    public static string JobPath(string stateDir, string service, string outPath) =>
        Path.Combine(stateDir, "jobs", service + "-" + Hashing.Sha256Text(Path.GetFullPath(outPath).ToLowerInvariant()).Substring(0, 16) + ".json");

    public static string QuotaPath(string stateDir, string service) => Path.Combine(stateDir, service + "-quota.json");
}
