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

public sealed class VtOptions
{
    public string BaseUrl = "https://www.virustotal.com/api/v3";
    public string GuiFileUrl = "https://www.virustotal.com/gui/file/";
    /// <summary>Direct POST /files limit (VirusTotal: 32 MB).</summary>
    public long DirectUploadLimit = 32L * 1024 * 1024;
    /// <summary>POST to an upload_url limit (VirusTotal: 650 MB).</summary>
    public long MaxUploadSize = 650L * 1024 * 1024;
    public TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    /// <summary>How long one run waits for analyses; anything still running is resumed by the next run.</summary>
    public TimeSpan MaxWait = TimeSpan.FromMinutes(20);
    /// <summary>Re-analyse files whose last VirusTotal analysis is older than this (null = never).</summary>
    public TimeSpan? Rescan = null;
}

/// <summary>
/// VirusTotal API v3 flow, per distinct file:
///   1. GET /files/{sha256}                      known  -> result (0 upload)
///   2. unknown + ≤ 32 MB : POST /files          -> analysis id
///      unknown + > 32 MB : GET /files/upload_url, POST {url}  (≤ 650 MB)
///      known, never analysed / too old: POST /files/{sha256}/analyse
///   3. GET /analyses/{id} until "completed"     -> result
/// Each step is saved in the job file, so a run stopped by the quota or by a crash resumes where it stopped.
/// </summary>
public sealed class VirusTotalScanner
{
    private readonly ApiTransport _api;
    private readonly string _apiKey;
    private readonly VtOptions _o;
    private readonly IClock _clock;
    private readonly ILog _log;

    internal VirusTotalScanner(string apiKey, ApiTransport api, VtOptions options, IClock clock, ILog log)
    {
        _apiKey = apiKey; _api = api; _o = options; _clock = clock; _log = log;
    }

    /// <summary>Runs the job until everything is done, the quota is spent, or MaxWait elapses. Calls <paramref name="save"/> after every state change.</summary>
    public async Task<int> RunAsync(JobState job, Action save, CancellationToken ct)
    {
        try
        {
            foreach (ScanItem item in job.Items.Where(i => i.Phase == ScanPhase.Pending).ToList())
            {
                await LookupOrSubmitAsync(item, ct).ConfigureAwait(false);
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
                    _log.Warn($"VirusTotal: {job.Items.Count(i => i.Phase == ScanPhase.Submitted)} analysis(es) still running after {_o.MaxWait.TotalMinutes:0} min; run the same command again later to collect them.");
                    return ExitCodes.Incomplete;
                }
                await _clock.DelayAsync(_o.PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (QuotaExhaustedException ex)
        {
            save();
            _log.Warn(ex.Message);
            return ExitCodes.Incomplete;
        }
        catch (ApiAuthException ex)
        {
            save();
            _log.Error(ex.Message);
            return ExitCodes.AuthOrConfig;
        }
        return job.Items.Any(i => i.Phase == ScanPhase.Error) ? ExitCodes.Failed : ExitCodes.Ok;
    }

    private HttpRequestMessage Req(HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("x-apikey", _apiKey);
        r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return r;
    }

    internal async Task LookupOrSubmitAsync(ScanItem item, CancellationToken ct)
    {
        ApiResponse r = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/files/{item.Sha256}"), ct).ConfigureAwait(false);
        if (r.Status == 200 && r.Root.TryGet("data", out JsonElement data) && data.TryGet("attributes", out JsonElement attrs))
        {
            long? date = attrs.Long("last_analysis_date");
            bool hasResults = attrs.TryGet("last_analysis_stats", out _) && date.HasValue;
            bool tooOld = hasResults && _o.Rescan.HasValue && DateTimeOffset.FromUnixTimeSeconds(date.Value) < _clock.UtcNow - _o.Rescan.Value;
            if (hasResults && !tooOld)
            {
                Complete(item, attrs.GetProperty("last_analysis_stats"), attrs.TryGet("last_analysis_results", out JsonElement res) ? res : default, date.Value, "known to VirusTotal");
                return;
            }
            // Known but never analysed (or stale): ask for a (re)analysis, no upload needed.
            ApiResponse a = await _api.SendAsync(() => Req(HttpMethod.Post, $"{_o.BaseUrl}/files/{item.Sha256}/analyse"), ct).ConfigureAwait(false);
            if (!AcceptAnalysis(item, a, "reanalysis requested")) Fail(item, "reanalyse: " + a.Describe());
            return;
        }
        if (r.Status == 404)
        {
            await UploadAsync(item, ct).ConfigureAwait(false);
            return;
        }
        Fail(item, "lookup: " + r.Describe());
    }

    private async Task UploadAsync(ScanItem item, CancellationToken ct)
    {
        if (item.LocalPath == null || !File.Exists(item.LocalPath))
        {
            Skip(item, "no local copy to upload (file unknown to VirusTotal); pass --objects-dir or --source-modules");
            return;
        }
        var fi = new FileInfo(item.LocalPath);
        if (fi.Length > _o.MaxUploadSize)
        {
            Skip(item, $"{fi.Length / (1024 * 1024)} MB is above the VirusTotal API upload limit ({_o.MaxUploadSize / (1024 * 1024)} MB); upload it by hand on virustotal.com if needed");
            return;
        }
        string actual = Hashing.Sha256File(fi.FullName);
        if (!string.Equals(actual, item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Fail(item, $"local copy {fi.FullName} has sha256 {actual}, manifest says {item.Sha256}; not uploaded");
            return;
        }

        string target = $"{_o.BaseUrl}/files";
        if (fi.Length > _o.DirectUploadLimit)
        {
            ApiResponse u = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/files/upload_url"), ct).ConfigureAwait(false);
            string url = u.IsSuccess && u.Root.TryGet("data", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (url == null) { Fail(item, "upload_url: " + u.Describe()); return; }
            target = url;
            _log.Info($"VirusTotal: {item.Files[0]} is {fi.Length / (1024 * 1024)} MB, using a large-file upload URL");
        }

        _log.Info($"VirusTotal: uploading {item.Files[0]} ({fi.Length:N0} bytes)");
        ApiResponse up = await _api.SendAsync(() =>
        {
            HttpRequestMessage req = Req(HttpMethod.Post, target);
            var form = new MultipartFormDataContent();
            var file = new StreamContent(new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", item.UploadName); // real file name, not the "<sha>.raw" object name
            req.Content = form;
            return req;
        }, ct).ConfigureAwait(false);
        if (!AcceptAnalysis(item, up, "uploaded")) Fail(item, "upload: " + up.Describe());
    }

    private bool AcceptAnalysis(ScanItem item, ApiResponse r, string what)
    {
        if (!r.IsSuccess || !r.Root.TryGet("data", out JsonElement d)) return false;
        string id = d.Str("id");
        if (string.IsNullOrEmpty(id)) return false;
        item.AnalysisId = id;
        item.Phase = ScanPhase.Submitted;
        item.SubmittedAt = _clock.UtcNow;
        item.Note = what;
        item.Vt = Entry(item, "pending", what);
        _log.Info($"VirusTotal: {item.Files[0]} {what}, analysis {id}");
        return true;
    }

    internal async Task PollAsync(ScanItem item, CancellationToken ct)
    {
        ApiResponse r = await _api.SendAsync(() => Req(HttpMethod.Get, $"{_o.BaseUrl}/analyses/{Uri.EscapeDataString(item.AnalysisId)}"), ct).ConfigureAwait(false);
        if (r.Status == 404)
        {
            // Analysis ids expire; start over from the hash lookup.
            item.Phase = ScanPhase.Pending; item.AnalysisId = null;
            await LookupOrSubmitAsync(item, ct).ConfigureAwait(false);
            return;
        }
        if (!r.IsSuccess || !r.Root.TryGet("data", out JsonElement d) || !d.TryGet("attributes", out JsonElement a))
        {
            Fail(item, "analysis: " + r.Describe());
            return;
        }
        string status = a.Str("status");
        if (status != "completed") { _log.Info($"VirusTotal: {item.Files[0]} analysis {status ?? "?"}"); return; }
        long date = a.Long("date") ?? _clock.UtcNow.ToUnixTimeSeconds();
        Complete(item, a.TryGet("stats", out JsonElement st) ? st : default, a.TryGet("results", out JsonElement res) ? res : default, date, "analysed");
    }

    private void Complete(ScanItem item, JsonElement stats, JsonElement results, long unixDate, string how)
    {
        int Stat(string n) => (int)(stats.Long(n) ?? 0);
        int malicious = Stat("malicious"), suspicious = Stat("suspicious");
        int engines = malicious + suspicious + Stat("undetected") + Stat("harmless");
        var flagged = new List<FlaggedBy>();
        if (results.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in results.EnumerateObject())
            {
                string cat = p.Value.Str("category");
                if (cat == "malicious" || cat == "suspicious")
                    flagged.Add(new FlaggedBy { Engine = p.Value.Str("engine_name") ?? p.Name, Category = cat, Result = p.Value.Str("result") });
            }
        }
        item.Phase = ScanPhase.Done;
        item.Note = how;
        item.Vt = Entry(item, "done", null);
        item.Vt.Detections = malicious;
        item.Vt.Suspicious = suspicious;
        item.Vt.Engines = engines;
        item.Vt.ScannedAt = DateTimeOffset.FromUnixTimeSeconds(unixDate).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        item.Vt.FlaggedBy = flagged.OrderBy(f => f.Engine, StringComparer.Ordinal).ToList();
        string verdict = malicious == 0 && suspicious == 0 ? "clean" : $"{malicious} malicious, {suspicious} suspicious ({string.Join(", ", flagged.Select(f => f.Engine))})";
        _log.Info($"VirusTotal: {item.Files[0]} {how}: {malicious}/{engines} - {verdict}");
        if (malicious > 0) _log.Warn($"VirusTotal flags {item.Files[0]} - see docs/SECURITY-AUTOMATION.md section 4 (false positives) before publishing the link.");
    }

    private VtEntry Entry(ScanItem item, string status, string note) => new VtEntry
    {
        Sha256 = item.Sha256,
        Url = _o.GuiFileUrl + item.Sha256,
        Files = item.Files.ToList(),
        Status = status,
        Note = note,
    };

    private void Fail(ScanItem item, string why)
    {
        item.Phase = ScanPhase.Error; item.Note = why;
        item.Vt = Entry(item, "error", why);
        _log.Error($"VirusTotal: {item.Files[0]}: {why}");
    }

    private void Skip(ScanItem item, string why)
    {
        item.Phase = ScanPhase.Skipped; item.Note = why;
        item.Vt = Entry(item, "skipped", why);
        _log.Warn($"VirusTotal: {item.Files[0]} skipped: {why}");
    }
}
