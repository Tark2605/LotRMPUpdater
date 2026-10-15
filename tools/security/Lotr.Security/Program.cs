using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace Lotr.Security;

public static class Program
{
    private const string Usage = @"lotr-security - VirusTotal / Hybrid Analysis scans for LotR MP Event releases

  lotr-security vt-scan  [--manifest <release>\manifest.json] [--file <path>]... [--out <security.json>]
                         [--objects-dir <dir>] [--source-modules <Modules dir>] [--rescan-days N]
                         [--max-wait-minutes 20] [--per-minute 4] [--per-day 500]
  lotr-security ha-scan  --file <LotRMPUpdater.exe> --out <security.json> [--sandbox] [--environment-id 160]
                         [--max-wait-minutes 30]
  lotr-security check-config            where the keys come from (masked) and today's quota use
  lotr-security manifest-block --in <security.json>   prints the manifest ""security"" block (docs/CONTRACTS.md §2)

Common: [--secrets <file>] [--state-dir <dir>] [--require-keys] [--dry-run]
Keys:   env LOTR_VT_APIKEY / LOTR_HA_APIKEY, or the secrets file (default %ProgramData%\LotRMP\secrets\security-api.json)
Exit:   0 ok or skipped (no key), 1 some file failed, 2 usage, 3 incomplete (quota/pending: run again later), 4 key rejected
Docs:   docs/SECURITY-AUTOMATION.md";

    public static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var log = new ConsoleLog();
        if (args.Length == 0 || args[0] == "-h" || args[0] == "--help" || args[0] == "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        string cmd = args[0];
        Dictionary<string, List<string>> opt;
        try { opt = Parse(args, 1); }
        catch (ArgumentException ex) { log.Error(ex.Message); Console.WriteLine(Usage); return ExitCodes.Usage; }

        using (var cts = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); log.Warn("cancelling; progress is saved"); };
            try
            {
                Secrets secrets = Secrets.Load(One(opt, "secrets"));
                var runner = new Runner(secrets, new HttpClientHandler(), SystemClock.Instance, log);
                switch (cmd)
                {
                    case "vt-scan":
                    {
                        ScanRequest req = Common(opt);
                        req.ObjectsDir = One(opt, "objects-dir");
                        req.SourceModulesDir = One(opt, "source-modules");
                        if (One(opt, "rescan-days") is string rd) req.Vt.Rescan = TimeSpan.FromDays(int.Parse(rd));
                        if (One(opt, "max-wait-minutes") is string mw) req.Vt.MaxWait = TimeSpan.FromMinutes(int.Parse(mw));
                        if (req.Manifest == null && req.Files.Count == 0) { log.Error("give --manifest and/or --file"); return ExitCodes.Usage; }
                        return runner.VirusTotalAsync(req, cts.Token).GetAwaiter().GetResult();
                    }
                    case "ha-scan":
                    {
                        ScanRequest req = Common(opt);
                        req.Ha.Sandbox = opt.ContainsKey("sandbox");
                        if (One(opt, "environment-id") is string env) req.Ha.EnvironmentId = int.Parse(env);
                        if (One(opt, "max-wait-minutes") is string mw) req.Ha.MaxWait = TimeSpan.FromMinutes(int.Parse(mw));
                        if (req.Files.Count == 0) { log.Error("ha-scan needs --file (the updater exe)"); return ExitCodes.Usage; }
                        if (req.Manifest != null) { log.Error("ha-scan is for the updater exe only; use --file"); return ExitCodes.Usage; }
                        return runner.HybridAnalysisAsync(req, cts.Token).GetAwaiter().GetResult();
                    }
                    case "check-config":
                    {
                        string stateDir = One(opt, "state-dir") ?? Paths.DefaultStateDir;
                        Console.WriteLine($"secrets file : {secrets.FilePath} ({(File.Exists(secrets.FilePath) ? "present" : "absent")})");
                        Console.WriteLine($"VirusTotal   : {Secrets.Mask(secrets.VirusTotalKey)} from {secrets.VirusTotalSource}");
                        Console.WriteLine($"HybridAnalys.: {Secrets.Mask(secrets.HybridAnalysisKey)} from {secrets.HybridAnalysisSource}");
                        Console.WriteLine($"state dir    : {stateDir}");
                        foreach (string svc in new[] { "virustotal", "hybrid-analysis" })
                        {
                            var q = Json.Read<RateLimitState>(Paths.QuotaPath(stateDir, svc));
                            Console.WriteLine($"quota {svc,-16}: " + (q == null ? "never used" : $"{q.DayCount} request(s) on {q.Day} UTC{(q.DayExhausted ? " (exhausted)" : "")}"));
                        }
                        foreach (string w in secrets.Warnings) log.Warn(w);
                        bool needAll = opt.ContainsKey("require-keys");
                        return needAll && (secrets.VirusTotalKey == null || secrets.HybridAnalysisKey == null) ? ExitCodes.Usage : ExitCodes.Ok;
                    }
                    case "manifest-block":
                    {
                        string input = One(opt, "in");
                        if (input == null || !File.Exists(input)) { log.Error("--in <security.json> required"); return ExitCodes.Usage; }
                        Console.WriteLine(JsonSerializer.Serialize(SecuritySidecar.Load(input).ToManifestBlock(), new JsonSerializerOptions { WriteIndented = true }));
                        return ExitCodes.Ok;
                    }
                    default:
                        log.Error("unknown command " + cmd);
                        Console.WriteLine(Usage);
                        return ExitCodes.Usage;
                }
            }
            catch (OperationCanceledException) { return ExitCodes.Incomplete; }
            catch (Exception ex) when (ex is FileNotFoundException || ex is InvalidDataException || ex is JsonException || ex is FormatException)
            {
                log.Error(ex.Message);
                return ExitCodes.AuthOrConfig;
            }
        }
    }

    private static ScanRequest Common(Dictionary<string, List<string>> opt)
    {
        var r = new ScanRequest
        {
            Manifest = One(opt, "manifest"),
            Out = One(opt, "out"),
            StateDir = One(opt, "state-dir"),
            RequireKeys = opt.ContainsKey("require-keys"),
            DryRun = opt.ContainsKey("dry-run"),
        };
        if (opt.TryGetValue("file", out var files)) r.Files.AddRange(files);
        if (One(opt, "per-minute") is string pm) r.PerMinute = int.Parse(pm);
        if (One(opt, "per-day") is string pd) r.PerDay = int.Parse(pd);
        return r;
    }

    private static readonly HashSet<string> Flags = new HashSet<string> { "require-keys", "dry-run", "sandbox" };

    internal static Dictionary<string, List<string>> Parse(string[] args, int start)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException("unexpected argument " + args[i]);
            string name = args[i].Substring(2);
            if (!d.TryGetValue(name, out var list)) d[name] = list = new List<string>();
            if (Flags.Contains(name)) continue;
            if (i + 1 >= args.Length) throw new ArgumentException("--" + name + " needs a value");
            list.Add(args[++i]);
        }
        return d;
    }

    private static string One(Dictionary<string, List<string>> d, string name) =>
        d.TryGetValue(name, out var l) && l.Count > 0 ? l[l.Count - 1] : null;
}
