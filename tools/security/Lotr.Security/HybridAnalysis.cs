using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security;

public sealed class HaOptions
{
    // Endpoint paths follow the public Falcon Sandbox API v2 docs (https://www.hybrid-analysis.com/docs/api/v2).
    // Verify them once with the first real key (docs/review-after/G-security.md, "ha-api").
    public string BaseUrl = "https://www.hybrid-analysis.com/api/v2";
    public string SampleUrl = "https://www.hybrid-analysis.com/sample/";
    /// <summary>Also run a full sandbox detonation (slow, counts against the submission quota).</summary>
    public bool Sandbox = false;
    /// <summary>160 = Windows 10 64-bit (Hybrid Analysis environment id; GET /system/environments lists them).</summary>
    public int EnvironmentId = 160;
    public string QuickScanType = "all";
    public long MaxUploadSize = 100L * 1024 * 1024;
    public TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    public TimeSpan MaxWait = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Hybrid Analysis (Falcon Sandbox API v2) for the updater exe:
///   1. GET  /search/hash?hash=  -> an existing finished report with a verdict is reused
///   2. POST /quick-scan/file    -> multi-engine quick scan, GET /quick-scan/{id} until finished
///   3. (--sandbox) POST /submit/file -> GET /report/{job}/state until SUCCESS -> GET /report/{job}/summary
/// </summary>
public sealed class HybridAnalysisScanner
{
    private readonly ApiTransport _api;
    private readonly string _apiKey;
    private readonly HaOptions _o;
    private readonly IClock _clock;
    private readonly ILog _log;

    internal HybridAnalysisScanner(string apiKey, ApiTransport api, HaOptions options, IClock clock, ILog log)
    {
        _apiKey = apiKey; _api = api; _o = options; _clock = clock; _log = log;
    }

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("api-key", _apiKey);
        r.Headers.TryAddWithoutValidation("User-Agent", "Falcon Sandbox"); // required by the API
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    private HttpRequestMessage Upload(string url, ScanItem item, Dictionary<string, string> fields)
    {
        HttpRequestMessage req = Req(HttpMethod.Post, url);
        var form = new MultipartFormDataContent();
        foreach (var kv in fields) form.Add(new StringContent(kv.Value), kv.Key);
        var file = new StreamContent(new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", item.UploadName);
        req.Content = form;
        return req;
    }

    public async Task<int> RunAsync(JobState job, Action save, CancellationToken ct)
    {
        try
        {
            foreach (ScanItem item in job.Items.Where(i => i.Phase == ScanPhase.Pending).ToList())
            {
                await StartAsync(item, ct).ConfigureAwait(false);
                save();
            }
            DateTimeOffset deadline = _clock.UtcNow + _o.MaxWait;
            while (job.Items.Any(i => i.Phase == ScanPhase.Submitted))
            {
                foreach (ScanItem item in job.Items.Where(i => i.Phase == ScanPhase.Submitted).ToList())
                {
                    await PollAsync(item, ct).ConfigureAwait(false);
                    save();
                }
                if (!job.Items.Any(i => i.Phase == ScanPhase.Submitted)) break;
                if (_clock.UtcNow + _o.PollInterval > deadline)
                {
                    _log.Warn("Hybrid Analysis: report still running; run the same command again later to collect it.");
                    return ExitCodes.Incomplete;
                }
                await _clock.DelayAsync(_o.PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (QuotaExhaustedException ex) { save(); _log.Warn(ex.Message); return ExitCodes.Incomplete; }
        catch (ApiAuthException ex) { save(); _log.Error(ex.Message); return ExitCodes.AuthOrConfig; }
        return job.Items.Any(i => i.Phase == ScanPhase.Error) ? ExitCodes.Failed : ExitCodes.Ok;
    }

    internal async Task StartAsync(ScanItem item, CancellationToken ct)
    {
        // 1. Existing public report?
        ApiResponse s = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/search/hash?hash={item.Sha256}"), ct).ConfigureAwait(false);
        if (s.IsSuccess)
        {
            JsonElement best = BestReport(s.Root);
            if (best.ValueKind == JsonValueKind.Object)
            {
                Complete(item, best.Str("verdict"), (int?)best.Long("threat_score"), "existing-report",
                    best.Str("environment_description"), best.Str("analysis_start_time"), best.Str("job_id") ?? best.Str("id"));
                return;
            }
        }
        else if (s.Status != 404)
        {
            _log.Warn("Hybrid Analysis: hash search failed (" + s.Describe() + "), submitting instead");
        }

        if (item.LocalPath == null || !File.Exists(item.LocalPath)) { Skip(item, "no local copy to submit"); return; }
        long size = new FileInfo(item.LocalPath).Length;
        if (size > _o.MaxUploadSize) { Skip(item, $"{size / (1024 * 1024)} MB is above the Hybrid Analysis upload limit"); return; }
        if (!string.Equals(Hashing.Sha256File(item.LocalPath), item.Sha256, StringComparison.OrdinalIgnoreCase))
        { Fail(item, "local copy does not match the expected sha256; not submitted"); return; }

        if (_o.Sandbox)
        {
            ApiResponse sub = await _api.SendAsync(() => Upload($"{_o.BaseUrl}/submit/file", item,
                new Dictionary<string, string> { ["environment_id"] = _o.EnvironmentId.ToString() }), ct).ConfigureAwait(false);
            string job = sub.IsSuccess ? sub.Root.Str("job_id") : null;
            if (job == null) { Fail(item, "submit: " + sub.Describe()); return; }
            item.JobId = job;
            Submitted(item, "sandbox job " + job);
            return;
        }

        ApiResponse q = await _api.SendAsync(() => Upload($"{_o.BaseUrl}/quick-scan/file", item,
            new Dictionary<string, string> { ["scan_type"] = _o.QuickScanType }), ct).ConfigureAwait(false);
        string id = q.IsSuccess ? q.Root.Str("id") : null;
        if (id == null) { Fail(item, "quick-scan: " + q.Describe()); return; }
        item.QuickScanId = id;
        if (q.Root.TryGet("finished", out JsonElement fin) && fin.ValueKind == JsonValueKind.True)
        {
            CompleteQuickScan(item, q.Root);
            return;
        }
        Submitted(item, "quick scan " + id);
    }

    /// <summary>The search answer is either an array of reports (older API) or { "reports": [...] } (newer API).</summary>
    private static JsonElement BestReport(JsonElement root)
    {
        IEnumerable<JsonElement> reports = Enumerable.Empty<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array) reports = root.EnumerateArray().ToList();
        else if (root.TryGet("reports", out JsonElement r) && r.ValueKind == JsonValueKind.Array) reports = r.EnumerateArray().ToList();
        return reports
            .Where(x => x.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(x.Str("verdict")) &&
                        (x.Str("state") == null || x.Str("state") == "SUCCESS"))
            .OrderByDescending(x => x.Str("analysis_start_time") ?? "")
            .FirstOrDefault();
    }

    internal async Task PollAsync(ScanItem item, CancellationToken ct)
    {
        if (item.JobId != null)
        {
            ApiResponse st = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/report/{Uri.EscapeDataString(item.JobId)}/state"), ct).ConfigureAwait(false);
            if (!st.IsSuccess) { Fail(item, "report state: " + st.Describe()); return; }
            string state = st.Root.Str("state");
            if (state == "ERROR") { Fail(item, "sandbox error: " + (st.Root.Str("error") ?? "unknown")); return; }
            if (state != "SUCCESS") { _log.Info($"Hybrid Analysis: sandbox {state}"); return; }
            ApiResponse sum = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/report/{Uri.EscapeDataString(item.JobId)}/summary"), ct).ConfigureAwait(false);
            if (!sum.IsSuccess) { Fail(item, "report summary: " + sum.Describe()); return; }
            Complete(item, sum.Root.Str("verdict"), (int?)sum.Root.Long("threat_score"), "sandbox",
                sum.Root.Str("environment_description"), sum.Root.Str("analysis_start_time"), item.JobId);
            return;
        }
        ApiResponse q = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/quick-scan/{Uri.EscapeDataString(item.QuickScanId)}"), ct).ConfigureAwait(false);
        if (!q.IsSuccess) { Fail(item, "quick-scan state: " + q.Describe()); return; }
        if (q.Root.TryGet("finished", out JsonElement fin) && fin.ValueKind == JsonValueKind.True) CompleteQuickScan(item, q.Root);
        else _log.Info("Hybrid Analysis: quick scan running");
    }

    private void CompleteQuickScan(ScanItem item, JsonElement root)
    {
        // Quick scan has no sandbox verdict; derive one from the scanners so the site shows a single word.
        var statuses = new List<string>();
        if (root.TryGet("scanners", out JsonElement sc) && sc.ValueKind == JsonValueKind.Array)
            foreach (JsonElement s in sc.EnumerateArray()) statuses.Add(s.Str("status") ?? "");
        string verdict = statuses.Contains("malicious") ? "malicious"
                       : statuses.Contains("suspicious") ? "suspicious"
                       : "no specific threat";
        Complete(item, verdict, null, "quick-scan", null, _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), item.QuickScanId);
    }

    private void Complete(ScanItem item, string verdict, int? score, string source, string env, string when, string reportId)
    {
        item.Phase = ScanPhase.Done;
        item.Note = source;
        item.Ha = new HaEntry
        {
            Sha256 = item.Sha256,
            Url = _o.SampleUrl + item.Sha256 + (source == "sandbox" && reportId != null && !reportId.Contains(":") ? "/" + reportId : ""),
            Verdict = verdict ?? "no verdict",
            ThreatScore = score,
            Source = source,
            Environment = env,
            ScannedAt = when,
            Files = item.Files.ToList(),
            Status = "done",
        };
        _log.Info($"Hybrid Analysis: {item.Files[0]}: {item.Ha.Verdict} ({source}{(score.HasValue ? ", threat score " + score : "")})");
        if (verdict == "malicious" || verdict == "suspicious")
            _log.Warn("Hybrid Analysis flags the file - see docs/SECURITY-AUTOMATION.md section 4 (false positives).");
    }

    private void Submitted(ScanItem item, string what)
    {
        item.Phase = ScanPhase.Submitted; item.SubmittedAt = _clock.UtcNow; item.Note = what;
        item.Ha = new HaEntry { Sha256 = item.Sha256, Url = _o.SampleUrl + item.Sha256, Files = item.Files.ToList(), Status = "pending", Note = what };
        _log.Info($"Hybrid Analysis: {item.Files[0]} submitted ({what})");
    }

    private void Fail(ScanItem item, string why)
    {
        item.Phase = ScanPhase.Error; item.Note = why;
        item.Ha = new HaEntry { Sha256 = item.Sha256, Url = _o.SampleUrl + item.Sha256, Files = item.Files.ToList(), Status = "error", Note = why };
        _log.Error($"Hybrid Analysis: {item.Files[0]}: {why}");
    }

    private void Skip(ScanItem item, string why)
    {
        item.Phase = ScanPhase.Skipped; item.Note = why;
        item.Ha = new HaEntry { Sha256 = item.Sha256, Url = _o.SampleUrl + item.Sha256, Files = item.Files.ToList(), Status = "skipped", Note = why };
        _log.Warn($"Hybrid Analysis: {item.Files[0]} skipped: {why}");
    }
}
