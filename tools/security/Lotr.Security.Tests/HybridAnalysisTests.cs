using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Xunit;

namespace Lotr.Security.Tests;

public class HybridAnalysisTests
{
    private const string Base = @"https://www\.hybrid-analysis\.com/api/v2/";

    private sealed class Ctx : IDisposable
    {
        public readonly TempDir Dir = new TempDir();
        public readonly string Exe;
        public readonly string Sha;
        public readonly FakeHttp Http = new FakeHttp();
        public readonly FakeClock Clock = new FakeClock();
        public readonly MemoryLog Log = new MemoryLog();
        public string Out => Dir.Combine("files", "updater", "security.json");

        public Ctx()
        {
            Exe = Dir.WriteFile(@"files\updater\LotRMPUpdater-0.1.0.exe", 5000, 77);
            Sha = Hashing.Sha256File(Exe);
            Http.Vars["SHA256"] = Sha;
            Http.Clock = Clock;
        }

        public int Run(Action<ScanRequest> tweak = null, Secrets secrets = null)
        {
            var req = new ScanRequest { Out = Out, StateDir = Dir.Combine("state") };
            req.Files.Add(Exe);
            tweak?.Invoke(req);
            return new Runner(secrets ?? TestRelease.Keys(), Http, Clock, Log).HybridAnalysisAsync(req, CancellationToken.None).GetAwaiter().GetResult();
        }

        public HaEntry Entry => SecuritySidecar.Load(Out).HybridAnalysis.Single();
        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void NoKey_Skips()
    {
        using (var c = new Ctx())
        {
            Assert.Equal(ExitCodes.Ok, c.Run(secrets: TestRelease.Keys(ha: null)));
            Assert.Empty(c.Http.Requests);
            Assert.Contains("Hybrid Analysis: skipped - no API key", c.Log.ToString());
            Assert.Contains(Secrets.HaEnv, c.Log.ToString());
        }
    }

    [Fact]
    public void ExistingReport_IsReused_NoUpload_AndRequiredHeadersSent()
    {
        using (var c = new Ctx())
        {
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(200, "ha/search-found.json"));
            Assert.Equal(ExitCodes.Ok, c.Run());
            RecordedRequest r = Assert.Single(c.Http.Requests);
            Assert.Equal("ha-test-key-0123456789", r.Headers["api-key"]);
            Assert.Equal("Falcon Sandbox", r.Headers["User-Agent"]);
            Assert.Contains("hash=" + c.Sha, r.Uri.Query);
            HaEntry e = c.Entry;
            Assert.Equal("no specific threat", e.Verdict);
            Assert.Equal("existing-report", e.Source);
            Assert.Equal(0, e.ThreatScore);
            Assert.Equal("https://www.hybrid-analysis.com/sample/" + c.Sha, e.Url);
        }
    }

    [Fact]
    public void LegacyArraySearchAnswer_IsUnderstood()
    {
        using (var c = new Ctx())
        {
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(200, "ha/search-found-legacy-array.json"));
            Assert.Equal(ExitCodes.Ok, c.Run());
            Assert.Equal("whitelisted", c.Entry.Verdict);
        }
    }

    [Fact]
    public void UnknownFile_QuickScan_SubmittedThenPolled()
    {
        using (var c = new Ctx())
        {
            c.Http.Vars["ANALYSIS_ID"] = "qs-1";
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(200, "ha/search-empty.json"))
                 .On("POST", Base + "quick-scan/file$", c.Http.Fixture(200, "ha/quickscan-submitted.json"))
                 .On("GET", Base + "quick-scan/qs-1$", c.Http.Fixture(200, "ha/quickscan-submitted.json"), c.Http.Fixture(200, "ha/quickscan-finished.json"));
            Assert.Equal(ExitCodes.Ok, c.Run());
            RecordedRequest up = c.Http.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Equal("multipart/form-data", up.ContentType);
            Assert.Contains("scan_type", up.ContentText);
            Assert.Contains("LotRMPUpdater-0.1.0.exe", up.ContentText);
            Assert.Equal("no specific threat", c.Entry.Verdict);
            Assert.Equal("quick-scan", c.Entry.Source);
        }
    }

    [Fact]
    public void QuickScan_MaliciousScanner_GivesMaliciousVerdict()
    {
        using (var c = new Ctx())
        {
            c.Http.Vars["ANALYSIS_ID"] = "qs-2";
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(404, "ha/search-empty.json"))
                 .On("POST", Base + "quick-scan/file$", c.Http.Fixture(200, "ha/quickscan-finished-malicious.json"));
            Assert.Equal(ExitCodes.Ok, c.Run());
            Assert.Equal("malicious", c.Entry.Verdict);
            Assert.Contains("false positives", c.Log.ToString());
        }
    }

    [Fact]
    public void Sandbox_Submit_PollState_ThenSummary()
    {
        using (var c = new Ctx())
        {
            c.Http.Vars["ANALYSIS_ID"] = "6712aaaaaaaaaaaaaaaaaaaa";
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(200, "ha/search-empty.json"))
                 .On("POST", Base + "submit/file$", c.Http.Fixture(201, "ha/submit.json"))
                 .On("GET", Base + "report/6712a+/state$", c.Http.Fixture(200, "ha/state-running.json"), c.Http.Fixture(200, "ha/state-success.json"))
                 .On("GET", Base + "report/6712a+/summary$", c.Http.Fixture(200, "ha/summary.json"));
            Assert.Equal(ExitCodes.Ok, c.Run(r => r.Ha.Sandbox = true));
            RecordedRequest up = c.Http.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Contains("environment_id", up.ContentText);
            Assert.Contains("160", up.ContentText);
            HaEntry e = c.Entry;
            Assert.Equal("sandbox", e.Source);
            Assert.Equal(3, e.ThreatScore);
            Assert.Equal("Windows 10 64 bit", e.Environment);
            Assert.EndsWith("/6712aaaaaaaaaaaaaaaaaaaa", e.Url);
            Assert.True(c.Clock.TotalWaited >= TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void InvalidKey_Aborts()
    {
        using (var c = new Ctx())
        {
            c.Http.On("GET", Base, c.Http.Fixture(403, "ha/forbidden.json"));
            Assert.Equal(ExitCodes.AuthOrConfig, c.Run());
            Assert.Contains("Api key is invalid", c.Log.ToString());
        }
    }

    [Fact]
    public void RateLimited_ThenOk()
    {
        using (var c = new Ctx())
        {
            c.Http.On("GET", Base + "search/hash", FakeHttp.Text(429, "{\"message\":\"Too many requests\"}"), c.Http.Fixture(200, "ha/search-found.json"));
            Assert.Equal(ExitCodes.Ok, c.Run());
            Assert.Equal(2, c.Http.Requests.Count);
            Assert.True(c.Http.Requests[1].At - c.Http.Requests[0].At >= TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void SubmitError_IsRecorded()
    {
        using (var c = new Ctx())
        {
            c.Http.On("GET", Base + "search/hash", c.Http.Fixture(200, "ha/search-empty.json"))
                 .On("POST", Base + "quick-scan/file$", FakeHttp.Text(400, "{\"validation_errors\":[{\"field\":\"scan_type\",\"errors\":[\"invalid\"]}],\"message\":\"Invalid scan type\"}"));
            Assert.Equal(ExitCodes.Failed, c.Run());
            Assert.Equal("error", c.Entry.Status);
            Assert.Contains("Invalid scan type", c.Entry.Note);
        }
    }
}

public class SecretsTests
{
    [Fact]
    public void EnvironmentWins_OverFile_AndFileIsUsedOtherwise()
    {
        using (var d = new TempDir())
        {
            string f = d.Combine("security-api.json");
            File.WriteAllText(f, "{ \"virustotal\": { \"apiKey\": \"file-vt-key-123456\" }, \"hybridAnalysis\": { \"apiKey\": \"file-ha-key-123456\" } }");
            Secrets s = Secrets.Load(f, n => n == Secrets.VtEnv ? "env-vt-key-123456" : null);
            Assert.Equal("env-vt-key-123456", s.VirusTotalKey);
            Assert.StartsWith("env", s.VirusTotalSource);
            Assert.Equal("file-ha-key-123456", s.HybridAnalysisKey);
            Assert.StartsWith("file", s.HybridAnalysisSource);
        }
    }

    [Fact]
    public void SecretsFileEnvVariable_IsHonoured()
    {
        using (var d = new TempDir())
        {
            string f = d.Combine("k.json");
            File.WriteAllText(f, "{ \"virustotal\": { \"apiKey\": \"abc-def-ghi-jkl\" } }");
            Secrets s = Secrets.Load(null, n => n == Secrets.FileEnv ? f : null);
            Assert.Equal(f, s.FilePath);
            Assert.Equal("abc-def-ghi-jkl", s.VirusTotalKey);
            Assert.Null(s.HybridAnalysisKey);
        }
    }

    [Fact]
    public void ExampleFile_HasNoUsableKey()
    {
        string example = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "templates", "security-api.example.json");
        Assert.True(File.Exists(example), example);
        Secrets s = Secrets.Load(example, n => null);
        Assert.Null(s.VirusTotalKey);
        Assert.Null(s.HybridAnalysisKey);
    }

    [Fact]
    public void FileInsideGitTree_IsWarned()
    {
        using (var d = new TempDir())
        {
            Directory.CreateDirectory(d.Combine(".git"));
            string f = d.Combine("sub", "security-api.json");
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, "{}");
            Secrets s = Secrets.Load(f, n => null);
            Assert.Contains(s.Warnings, w => w.Contains("git working tree"));
        }
    }

    [Fact]
    public void Mask_NeverShowsTheWholeKey()
    {
        Assert.Equal("abcd********wxyz", Secrets.Mask("abcdefghijklmnopqrstuvwxyz"));
        Assert.Equal("****", Secrets.Mask("short"));
        Assert.Equal("(none)", Secrets.Mask(null));
    }
}
