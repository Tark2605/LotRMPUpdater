using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Xunit;

namespace Lotr.Security.Tests;

public class VirusTotalTests
{
    private const string Files = @"https://www\.virustotal\.com/api/v3/files/";
    private const string Analyses = @"https://www\.virustotal\.com/api/v3/analyses/";

    [Fact]
    public void NoKey_SkipsWithClearMessage_AndSendsNothing()
    {
        using (var rel = new TestRelease())
        {
            rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            var http = new FakeHttp(); var log = new MemoryLog();
            int code = rel.RunVt(http, new FakeClock(), log, rel.Request(), TestRelease.Keys(vt: null, ha: null));
            Assert.Equal(ExitCodes.Ok, code);
            Assert.Empty(http.Requests);
            Assert.Contains("skipped - no API key", log.ToString());
            Assert.Contains(Secrets.VtEnv, log.ToString());
            Assert.False(File.Exists(rel.SecurityJson));

            int strict = rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request(r => r.RequireKeys = true), TestRelease.Keys(vt: null, ha: null));
            Assert.Equal(ExitCodes.Usage, strict);
        }
    }

    [Fact]
    public void PlaceholderKey_CountsAsNoKey()
    {
        Secrets s = TestRelease.Keys(vt: "<PUT-YOUR-VIRUSTOTAL-KEY-HERE>");
        Assert.Null(s.VirusTotalKey);
    }

    [Fact]
    public void KnownHashes_AreLookedUpOnly_DuplicatesOnce_NonExecIgnored()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            rel.Add("LotRMPCore", "bin/Win64_Shipping_Client/A.dll", 2048, 1); // same bytes, other module
            rel.Add("LotRMPEvent", "SubModule.xml", 100, 7, exec: false);
            var http = new FakeHttp();
            http.Vars["SHA256"] = a;
            http.On("GET", Files + a + "$", http.Fixture(200, "vt/file-known-clean.json"));

            var log = new MemoryLog();
            int code = rel.RunVt(http, new FakeClock(), log, rel.Request());

            Assert.Equal(ExitCodes.Ok, code);
            Assert.Single(http.Requests);
            Assert.Equal("vt-test-key-0123456789", http.Requests[0].Headers["x-apikey"]);
            Assert.DoesNotContain("vt-test-key-0123456789", log.ToString()); // key never logged in clear

            var sc = rel.Sidecar();
            Assert.Equal("20261015-140000-a1b2c3d4", sc.ReleaseId);
            VtEntry e = Assert.Single(sc.Virustotal);
            Assert.Equal("done", e.Status);
            Assert.Equal(0, e.Detections);
            Assert.Equal(72, e.Engines);
            Assert.Equal("https://www.virustotal.com/gui/file/" + a, e.Url);
            Assert.Equal("2026-10-14T15:00:00Z", e.ScannedAt);
            Assert.Equal(2, e.Files.Count);
        }
    }

    [Fact]
    public void FlaggedFile_ReportsEnginesAndWarns()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(200, "vt/file-known-flagged.json"));
            var log = new MemoryLog();
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, new FakeClock(), log, rel.Request()));
            VtEntry e = rel.Sidecar().Virustotal.Single();
            Assert.Equal(2, e.Detections);
            Assert.Equal(1, e.Suspicious);
            Assert.Equal(71, e.Engines);
            Assert.Equal(new[] { "OtherAV", "SomeAV", "ThirdAV" }, e.FlaggedBy.Select(f => f.Engine).ToArray());
            Assert.Contains("false positives", log.ToString());
        }
    }

    [Fact]
    public void UnknownSmallFile_IsUploadedDirectly_ThenPolledUntilCompleted()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 4096, 1);
            var http = new FakeHttp();
            http.Vars["SHA256"] = a; http.Vars["ANALYSIS_ID"] = "NjY0MjRlOTVjMDM0YmE2OA==";
            http.On("GET", Files + a, http.Fixture(404, "vt/not-found.json"))
                .On("POST", @"/api/v3/files$", http.Fixture(200, "vt/analysis-created.json"))
                .On("GET", Analyses, http.Fixture(200, "vt/analysis-queued.json"), http.Fixture(200, "vt/analysis-queued.json"), http.Fixture(200, "vt/analysis-completed.json"));
            var clock = new FakeClock();
            int code = rel.RunVt(http, clock, new MemoryLog(), rel.Request());

            Assert.Equal(ExitCodes.Ok, code);
            RecordedRequest up = http.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Equal("multipart/form-data", up.ContentType);
            Assert.True(up.ContentLength > 4096);
            string head = up.ContentText.Substring(0, 400);
            Assert.True(head.Contains("name=file") && head.Contains("filename=A.dll"), head);
            Assert.Equal(3, http.Requests.Count(r => r.Uri.AbsolutePath.Contains("/analyses/")));
            Assert.Equal("NjY0MjRlOTVjMDM0YmE2OA%3D%3D", http.Requests.Last().Uri.AbsolutePath.Split('/').Last());
            VtEntry e = rel.Sidecar().Virustotal.Single();
            Assert.Equal("done", e.Status);
            Assert.Equal(71, e.Engines);
            Assert.True(clock.TotalWaited >= TimeSpan.FromSeconds(60)); // two poll intervals
        }
    }

    [Fact]
    public void FileAbove32MB_UsesUploadUrl_WithDefaultThreshold()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPCore", "bin/Win64_Shipping_Client/Big.dll", 33 * 1024 * 1024, 3);
            var http = new FakeHttp();
            http.Vars["SHA256"] = a; http.Vars["ANALYSIS_ID"] = "big-1";
            http.On("GET", Files + "upload_url$", http.Fixture(200, "vt/upload-url.json"))
                .On("GET", Files + a, http.Fixture(404, "vt/not-found.json"))
                .On("POST", @"/_ah/upload/", http.Fixture(200, "vt/analysis-created.json"))
                .On("GET", Analyses, http.Fixture(200, "vt/analysis-completed.json"));
            int code = rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request());

            Assert.Equal(ExitCodes.Ok, code);
            Assert.Contains(http.Requests, r => r.Uri.AbsolutePath.EndsWith("/files/upload_url"));
            Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v3/files");
            RecordedRequest up = http.Requests.Single(r => r.Method == HttpMethod.Post);
            Assert.Equal("www.virustotal.com", up.Uri.Host);
            Assert.True(up.ContentLength > 33L * 1024 * 1024);
            Assert.Equal("vt-test-key-0123456789", up.Headers["x-apikey"]);
            Assert.Equal("done", rel.Sidecar().Virustotal.Single().Status);
        }
    }

    [Fact]
    public void FileAboveUploadLimit_IsSkipped_NotUploaded()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPCore", "bin/Win64_Shipping_Client/Huge.dll", 8192, 3);
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(404, "vt/not-found.json"));
            var log = new MemoryLog();
            int code = rel.RunVt(http, new FakeClock(), log, rel.Request(r => { r.Vt.DirectUploadLimit = 1024; r.Vt.MaxUploadSize = 4096; }));
            Assert.Equal(ExitCodes.Ok, code);
            Assert.Single(http.Requests);
            VtEntry e = rel.Sidecar().Virustotal.Single();
            Assert.Equal("skipped", e.Status);
            Assert.Contains("upload limit", e.Note);
        }
    }

    [Fact]
    public void UnknownFile_WithoutLocalCopy_IsSkipped_ThenUploadedWhenSourceGiven()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1, asObject: false);
            var http = new FakeHttp();
            http.Vars["ANALYSIS_ID"] = "x1";
            http.On("GET", Files + a, http.Fixture(404, "vt/not-found.json"))
                .On("POST", @"/api/v3/files$", http.Fixture(200, "vt/analysis-created.json"))
                .On("GET", Analyses, http.Fixture(200, "vt/analysis-completed.json"));
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request()));
            Assert.Equal("skipped", rel.Sidecar().Virustotal.Single().Status);

            int code = rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request(r => r.SourceModulesDir = rel.Dir.Combine("Modules")));
            Assert.Equal(ExitCodes.Ok, code);
            Assert.Equal("done", rel.Sidecar().Virustotal.Single().Status);
            Assert.Single(http.Requests, r => r.Method == HttpMethod.Post);
        }
    }

    [Fact]
    public void LocalCopyWithWrongHash_IsNeverUploaded()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            File.WriteAllText(Path.Combine(rel.ReleaseDir, "objects", a.Substring(0, 2), a + ".raw"), "tampered");
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(404, "vt/not-found.json"));
            int code = rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request());
            Assert.Equal(ExitCodes.Failed, code);
            Assert.DoesNotContain(http.Requests, r => r.Method == HttpMethod.Post);
            Assert.Contains("not uploaded", rel.Sidecar().Virustotal.Single().Note);
        }
    }

    [Fact]
    public void KnownButNeverAnalysed_RequestsReanalysis_WithoutUpload()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            var http = new FakeHttp();
            http.Vars["ANALYSIS_ID"] = "re-1";
            http.On("GET", Files + a + "$", http.Fixture(200, "vt/file-known-no-analysis.json"))
                .On("POST", Files + a + "/analyse$", http.Fixture(200, "vt/analysis-created.json"))
                .On("GET", Analyses, http.Fixture(200, "vt/analysis-completed.json"));
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request()));
            Assert.Single(http.Requests, r => r.Method == HttpMethod.Post);
            Assert.Null(http.Requests.Single(r => r.Method == HttpMethod.Post).ContentType);
        }
    }

    [Fact]
    public void FreeTier_NeverMoreThanFourRequestsPerMinute()
    {
        using (var rel = new TestRelease())
        {
            for (int i = 0; i < 9; i++) rel.Add("LotRMPEvent", $"bin/Win64_Shipping_Client/F{i}.dll", 1024, 100 + i);
            var http = new FakeHttp();
            http.On("GET", Files, http.Fixture(200, "vt/file-known-clean.json"));
            var clock = new FakeClock();
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, clock, new MemoryLog(), rel.Request()));
            Assert.Equal(9, http.Requests.Count);
            for (int i = 0; i + 4 < http.Requests.Count; i++)
                Assert.True(http.Requests[i + 4].At - http.Requests[i].At >= TimeSpan.FromMinutes(1),
                    $"requests {i} and {i + 4} are less than a minute apart");
            Assert.True(clock.TotalWaited >= TimeSpan.FromMinutes(2));
        }
    }

    [Fact]
    public void QuotaIsShared_AcrossRuns_ViaPersistedState()
    {
        using (var rel = new TestRelease())
        {
            for (int i = 0; i < 4; i++) rel.Add("LotRMPEvent", $"bin/Win64_Shipping_Client/F{i}.dll", 1024, 100 + i);
            var http = new FakeHttp();
            http.On("GET", Files, http.Fixture(200, "vt/file-known-clean.json"));
            var clock = new FakeClock();
            rel.RunVt(http, clock, new MemoryLog(), rel.Request());
            // A second release scanned right after must wait: the 4 slots of this minute are already used.
            rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/New.dll", 1024, 999);
            var before = clock.TotalWaited;
            rel.RunVt(http, clock, new MemoryLog(), rel.Request());
            Assert.True(clock.TotalWaited - before >= TimeSpan.FromSeconds(55));
        }
    }

    [Fact]
    public void DailyQuota_StopsWithResumableExitCode_AndResumesNextUtcDay()
    {
        using (var rel = new TestRelease())
        {
            for (int i = 0; i < 5; i++) rel.Add("LotRMPEvent", $"bin/Win64_Shipping_Client/F{i}.dll", 1024, 100 + i);
            var http = new FakeHttp();
            http.On("GET", Files, http.Fixture(200, "vt/file-known-clean.json"));
            var clock = new FakeClock();
            var log = new MemoryLog();
            int first = rel.RunVt(http, clock, log, rel.Request(r => r.PerDay = 3));
            Assert.Equal(ExitCodes.Incomplete, first);
            Assert.Equal(3, http.Requests.Count);
            Assert.Contains("daily quota used", log.ToString());
            Assert.Equal(3, rel.Sidecar().Virustotal.Count(v => v.Status == "done"));

            Assert.Equal(ExitCodes.Incomplete, rel.RunVt(http, clock, new MemoryLog(), rel.Request(r => r.PerDay = 3)));
            Assert.Equal(3, http.Requests.Count); // same day: nothing sent

            clock.UtcNow = new DateTimeOffset(2026, 10, 16, 0, 5, 0, TimeSpan.Zero);
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, clock, new MemoryLog(), rel.Request(r => r.PerDay = 3)));
            Assert.Equal(5, http.Requests.Count); // only the two remaining files
            Assert.Equal(5, rel.Sidecar().Virustotal.Count(v => v.Status == "done"));
        }
    }

    [Fact]
    public void Http429_BacksOffOneMinute_ThenSucceeds()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(429, "vt/quota-exceeded.json"), http.Fixture(200, "vt/file-known-clean.json"));
            var clock = new FakeClock();
            var log = new MemoryLog();
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, clock, log, rel.Request()));
            Assert.Equal(2, http.Requests.Count);
            Assert.True(http.Requests[1].At - http.Requests[0].At >= TimeSpan.FromSeconds(60));
            Assert.Contains("QuotaExceededError", log.ToString());
        }
    }

    [Fact]
    public void Persistent429_IsTreatedAsDailyQuotaSpent()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(429, "vt/quota-exceeded.json"));
            Assert.Equal(ExitCodes.Incomplete, rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request()));
            Assert.Equal(3, http.Requests.Count);
            var q = Json.Read<RateLimitState>(Paths.QuotaPath(rel.StateDir, "virustotal"));
            Assert.True(q.DayExhausted);
            Assert.Empty(rel.Sidecar().Virustotal); // nothing finished, nothing published
        }
    }

    [Fact]
    public void WrongKey_AbortsWholeRun()
    {
        using (var rel = new TestRelease())
        {
            rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/B.dll", 1024, 2);
            var http = new FakeHttp();
            http.On("GET", Files, http.Fixture(401, "vt/wrong-key.json"));
            var log = new MemoryLog();
            Assert.Equal(ExitCodes.AuthOrConfig, rel.RunVt(http, new FakeClock(), log, rel.Request()));
            Assert.Single(http.Requests);
            Assert.Contains("WrongCredentialsError", log.ToString());
        }
    }

    [Fact]
    public void ServerErrors_AreRetried_ThenOnlyThatFileFails()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            string b = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/B.dll", 1024, 2);
            var http = new FakeHttp();
            http.On("GET", Files + a, http.Fixture(503, "vt/server-error.json"))
                .On("GET", Files + b, http.Fixture(200, "vt/file-known-clean.json"));
            var clock = new FakeClock();
            Assert.Equal(ExitCodes.Failed, rel.RunVt(http, clock, new MemoryLog(), rel.Request()));
            Assert.Equal(4, http.Requests.Count(r => r.Uri.AbsolutePath.EndsWith(a)));
            var sc = rel.Sidecar();
            Assert.Equal("error", sc.Virustotal.Single(v => v.Sha256 == a).Status);
            Assert.Equal("done", sc.Virustotal.Single(v => v.Sha256 == b).Status);
            Assert.Contains(TimeSpan.FromSeconds(5), clock.Waits);
            Assert.Contains(TimeSpan.FromSeconds(15), clock.Waits);
            Assert.Contains(TimeSpan.FromSeconds(45), clock.Waits);

            // Next run retries the failed file only.
            var http2 = new FakeHttp();
            http2.On("GET", Files + a, http2.Fixture(200, "vt/file-known-clean.json"));
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http2, clock, new MemoryLog(), rel.Request()));
            Assert.Single(http2.Requests);
        }
    }

    [Fact]
    public void NetworkFailure_IsRetried()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            var http = new FakeHttp();
            http.On("GET", Files + a, FakeHttp.Throw(new HttpRequestException("connection reset")), http.Fixture(200, "vt/file-known-clean.json"));
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request()));
            Assert.Equal(2, http.Requests.Count);
        }
    }

    [Fact]
    public void LongAnalysis_IsLeftPending_AndCollectedByTheNextRunWithoutReupload()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 2048, 1);
            var http = new FakeHttp();
            http.Vars["ANALYSIS_ID"] = "slow-1";
            http.On("GET", Files + a, http.Fixture(404, "vt/not-found.json"))
                .On("POST", @"/api/v3/files$", http.Fixture(200, "vt/analysis-created.json"))
                .On("GET", Analyses, http.Fixture(200, "vt/analysis-queued.json"));
            int code = rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request(r => r.Vt.MaxWait = TimeSpan.FromMinutes(2)));
            Assert.Equal(ExitCodes.Incomplete, code);
            Assert.Equal("pending", rel.Sidecar().Virustotal.Single().Status);

            var http2 = new FakeHttp();
            http2.Vars["ANALYSIS_ID"] = "slow-1";
            http2.On("GET", Analyses + "slow-1$", http2.Fixture(200, "vt/analysis-completed.json"));
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http2, new FakeClock(), new MemoryLog(), rel.Request()));
            Assert.Single(http2.Requests);
            Assert.Equal("done", rel.Sidecar().Virustotal.Single().Status);
        }
    }

    [Fact]
    public void UpdaterExe_ScannedSeparately_SharesTheSidecarWithoutErasingReleaseEntries()
    {
        using (var rel = new TestRelease())
        {
            string a = rel.Add("LotRMPEvent", "bin/Win64_Shipping_Client/A.dll", 1024, 1);
            string exe = rel.Dir.WriteFile(@"files\updater\LotRMPUpdater-0.1.0.exe", 3000, 42);
            var http = new FakeHttp();
            http.On("GET", Files, http.Fixture(200, "vt/file-known-clean.json"));
            rel.RunVt(http, new FakeClock(), new MemoryLog(), rel.Request());
            var req = new ScanRequest { StateDir = rel.StateDir, Out = rel.SecurityJson };
            req.Files.Add(exe);
            Assert.Equal(ExitCodes.Ok, rel.RunVt(http, new FakeClock(), new MemoryLog(), req));
            var sc = rel.Sidecar();
            Assert.Equal(2, sc.Virustotal.Count);
            Assert.Contains(sc.Virustotal, v => v.Files.Contains("LotRMPUpdater-0.1.0.exe"));
            Assert.Contains(sc.Virustotal, v => v.Sha256 == a);
        }
    }

    [Fact]
    public void ManifestBlock_HasOnlyContractFields_AndOnlyFinishedResults()
    {
        var sc = new SecuritySidecar();
        sc.Upsert(new[]
        {
            new VtEntry { Sha256 = "aa", Url = "u1", Detections = 0, Engines = 70, ScannedAt = "2026-10-15T00:00:00Z", Status = "done", Suspicious = 0, Files = new System.Collections.Generic.List<string> { "x" } },
            new VtEntry { Sha256 = "bb", Url = "u2", Status = "pending" },
        });
        sc.Upsert(new[] { new HaEntry { Sha256 = "cc", Url = "u3", Verdict = "no specific threat", Status = "done", Source = "quick-scan" } });
        string json = JsonSerializer.Serialize(sc.ToManifestBlock());
        Assert.Equal("{\"virustotal\":[{\"sha256\":\"aa\",\"url\":\"u1\",\"detections\":0,\"engines\":70,\"scannedAt\":\"2026-10-15T00:00:00Z\"}],\"hybridAnalysis\":[{\"sha256\":\"cc\",\"url\":\"u3\",\"verdict\":\"no specific threat\"}]}", json);
    }
}
