using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security;

public sealed class ScanRequest
{
    public string Manifest;
    public List<string> Files = new List<string>();
    public string Out;
    public string ObjectsDir;
    public string SourceModulesDir;
    public string StateDir;
    public bool RequireKeys;
    public bool DryRun;
    public int PerMinute;
    public int PerDay;
    public VtOptions Vt = new VtOptions();
    public HaOptions Ha = new HaOptions();
}

/// <summary>Wires targets + state + quota + API client + sidecar. Everything injectable for tests.</summary>
public sealed class Runner
{
    private readonly Secrets _secrets;
    private readonly HttpMessageHandler _handler;
    private readonly IClock _clock;
    private readonly ILog _log;

    public Runner(Secrets secrets, HttpMessageHandler handler, IClock clock, ILog log)
    {
        _secrets = secrets; _handler = handler; _clock = clock; _log = log;
    }

    public Task<int> VirusTotalAsync(ScanRequest req, CancellationToken ct) =>
        RunAsync("virustotal", "VirusTotal", _secrets.VirusTotalKey, Secrets.VtEnv, req,
            req.PerMinute > 0 ? req.PerMinute : 4, req.PerDay > 0 ? req.PerDay : 500,
            (key, api) => (job, save, c) => new VirusTotalScanner(key, api, req.Vt, _clock, _log).RunAsync(job, save, c),
            (sidecar, items) => sidecar.Upsert(items.Where(i => i.Vt != null).Select(i => i.Vt)), ct);

    public Task<int> HybridAnalysisAsync(ScanRequest req, CancellationToken ct) =>
        RunAsync("hybrid-analysis", "Hybrid Analysis", _secrets.HybridAnalysisKey, Secrets.HaEnv, req,
            req.PerMinute > 0 ? req.PerMinute : 5, req.PerDay > 0 ? req.PerDay : 200,
            (key, api) => (job, save, c) => new HybridAnalysisScanner(key, api, req.Ha, _clock, _log).RunAsync(job, save, c),
            (sidecar, items) => sidecar.Upsert(items.Where(i => i.Ha != null).Select(i => i.Ha)), ct);

    private async Task<int> RunAsync(string service, string display, string key, string envName, ScanRequest req,
        int perMinute, int perDay,
        Func<string, ApiTransport, Func<JobState, Action, CancellationToken, Task<int>>> makeScanner,
        Action<SecuritySidecar, List<ScanItem>> upsert, CancellationToken ct)
    {
        foreach (string w in _secrets.Warnings) _log.Warn(w);
        if (string.IsNullOrEmpty(key))
        {
            string msg = $"{display}: skipped - no API key. Set {envName} or put it in {_secrets.FilePath} (docs/SECURITY-AUTOMATION.md, section 2).";
            if (req.RequireKeys) { _log.Error(msg); return ExitCodes.Usage; }
            _log.Warn(msg);
            return ExitCodes.Ok;
        }

        // Targets
        string releaseId = null;
        var fresh = new List<ScanItem>();
        if (req.Manifest != null)
        {
            string objects = req.ObjectsDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(req.Manifest)), "objects");
            Targets.ManifestInfo mi = Targets.FromManifest(req.Manifest, objects, req.SourceModulesDir);
            foreach (string w in mi.Warnings) _log.Warn(w);
            releaseId = mi.ReleaseId;
            fresh.AddRange(mi.Items);
        }
        foreach (string f in req.Files) fresh.Add(Targets.FromFile(f));
        if (fresh.Count == 0) { _log.Warn($"{display}: nothing to scan (no exec:true file and no --file)."); return ExitCodes.Ok; }

        string outPath = req.Out ?? (req.Manifest != null ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(req.Manifest)), "security.json") : null);
        if (outPath == null) { _log.Error("--out is required when no --manifest is given"); return ExitCodes.Usage; }

        string stateDir = req.StateDir ?? Paths.DefaultStateDir;
        string jobPath = Paths.JobPath(stateDir, service, outPath);
        JobState job = Json.Read<JobState>(jobPath) ?? new JobState { Service = service, OutPath = Path.GetFullPath(outPath) };
        job.ReleaseId = releaseId ?? job.ReleaseId;
        Targets.MergeInto(job, fresh);

        _log.Info($"{display}: {job.Items.Count} file(s), key from {(service == "virustotal" ? _secrets.VirusTotalSource : _secrets.HybridAnalysisSource)} ({Secrets.Mask(key)}), state {jobPath}");
        if (req.DryRun)
        {
            foreach (ScanItem i in job.Items)
                _log.Info($"  {i.Phase,-9} {i.Sha256} {string.Join(", ", i.Files)} {(i.LocalPath != null ? "[local copy]" : "[lookup only]")}");
            return ExitCodes.Ok;
        }

        void Save()
        {
            Json.Write(jobPath, job);
            SecuritySidecar sc = SecuritySidecar.Load(outPath);
            sc.ReleaseId = job.ReleaseId ?? sc.ReleaseId;
            sc.UpdatedAt = _clock.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            upsert(sc, job.Items);
            Json.Write(outPath, sc);
        }

        var limiter = new RateLimiter(display, perMinute, perDay, Paths.QuotaPath(stateDir, service), _clock, _log);
        using (var http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(30) })
        {
            var api = new ApiTransport(display, http, limiter, _clock, _log);
            Save();
            int code = await makeScanner(key, api)(job, Save, ct).ConfigureAwait(false);
            Save();
            var groups = job.Items.GroupBy(i => i.Phase).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}");
            _log.Info($"{display}: {string.Join(", ", groups)}; quota today {limiter.UsedToday}/{limiter.PerDay}; results in {outPath}");
            return code;
        }
    }
}
