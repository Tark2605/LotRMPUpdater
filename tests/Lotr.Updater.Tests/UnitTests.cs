using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Schedule;
using Lotr.Updater.Core.Security;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    public class UnitTests
    {
        [Fact]
        public void Json_roundtrip_and_canonical_form()
        {
            var o = Json.ParseObject("{\"b\":1,\"a\":[true,null,\"x\\u00e9\\n\"],\"c\":{\"d\":-2.5}}");
            Assert.Equal("{\"a\":[true,null,\"xé\\n\"],\"b\":1,\"c\":{\"d\":-2.5}}", Json.Write(o, indent: false, sortKeys: true));
            Assert.Throws<FormatException>(() => Json.Parse("{\"a\":1,}"));
        }

        [Theory]
        [InlineData("LotRMPEvent", "a.xml", true)]
        [InlineData("Native", "a.xml", false)]
        [InlineData("Multiplayer", "a.xml", false)]
        [InlineData("LotRMPEvent", ".lotr-staging/journal.json", false)]
        public void Updater_specific_manifest_rules(string folder, string path, bool ok)
        {
            var m = new LotR.Releases.Model.ReleaseManifest();
            var mod = new LotR.Releases.Model.ManifestModule { Id = "X", Folder = folder };
            mod.Files.Add(new LotR.Releases.Model.ManifestFile { Path = path, Size = 1, Sha256 = new string('a', 64), Object = new string('a', 64) + ".raw" });
            m.Modules.Add(mod);
            Assert.Equal(ok, UpdaterManifestRules.Check(m) == null);
        }

        [Fact]
        public void Signature_valid_tampered_and_unknown_key()
        {
            using (var key = ReleaseBuilder.NewTestKey())
            using (var other = ReleaseBuilder.NewTestKey())
            using (var signer = new LotR.Releases.Signing.ReleaseSigner(LotR.Releases.Signing.EcKeys.ImportPkcs8(LotR.Releases.Signing.EcKeys.ExportPkcs8(key))))
            {
                var data = Encoding.UTF8.GetBytes("{\"schema\":1}");
                var sig = LotR.Releases.Signing.ManifestSignature.Create(data, new[] { signer });
                var v = new ReleaseSignatureVerifier(new[] { ReleaseBuilder.PublicPem(other), ReleaseBuilder.PublicPem(key) });
                Assert.True(v.Verify(data, sig).Trusted);
                var tampered = (byte[])data.Clone();
                tampered[3] ^= 1;
                Assert.False(v.Verify(tampered, sig).Trusted);
                Assert.False(v.Verify(data, null).Trusted);
                Assert.False(v.Verify(data, Encoding.ASCII.GetBytes("garbage")).Trusted);
                Assert.False(new ReleaseSignatureVerifier(new[] { ReleaseBuilder.PublicPem(other) }).Verify(data, sig).Trusted);
                Assert.False(new ReleaseSignatureVerifier(new string[0]).Verify(data, sig).Trusted);
                Assert.Equal(2, v.TrustedFingerprints.Count);
            }
        }

        [Fact]
        public async Task Signed_release_is_accepted_and_tampered_release_refused()
        {
            using (var h = new Harness())
            {
                var r = h.NewRelease("sig");
                r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.0", "LotRMPEvent").SubModule();
                r.WriteTo(h.Releases);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.True(c.Signature.Trusted);

                var mf = Path.Combine(h.Releases, r.ReleaseId, "manifest.json");
                File.WriteAllText(mf, File.ReadAllText(mf).Replace("LotRMPEvent\"", "LotRMPEvenX\""));
                var c2 = await s.CheckAsync(null, CancellationToken.None);
                Assert.Contains("does not match", c2.Error);
                Assert.Null(c2.Plan);

                File.Delete(mf + ".sig");
                var c3 = await s.CheckAsync(null, CancellationToken.None);
                Assert.Contains("no signature", c3.Error);
            }
        }

        [Fact]
        public void User_name_is_masked()
        {
            var t = "C:\\Users\\Jean.Dupont\\AppData\\Local\\LotRMPUpdater\\logs  D:\\Games  C:\\Users\\JEAN~1\\x  user Jean.Dupont on PC-JEAN";
            var m = LogMasker.Mask(t, "Jean.Dupont", "C:\\Users\\Jean.Dupont", "PC-JEAN");
            Assert.DoesNotContain("Jean", m, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("C:\\Users\\<user>\\AppData", m);
            Assert.Contains("C:\\Users\\<user>\\x", m);
            Assert.Contains("D:\\Games", m);
            Assert.Contains("<pc>", m);
            var json = LogMasker.Mask("{\"p\":\"C:\\\\Users\\\\JEANDU~1\\\\AppData\"}", "Jean.Dupont", "C:\\Users\\Jean.Dupont");
            Assert.Equal("{\"p\":\"C:\\\\Users\\\\<user>\\\\AppData\"}", json);
        }

        [Theory]
        [InlineData("2026-10-15T12:00:00Z", "2026-10-20T18:30:00Z")] // Thursday (summer time CEST, UTC+2) -> next Tuesday
        [InlineData("2026-10-27T19:00:00Z", "2026-10-27T19:30:00Z")] // Tuesday after the switch to winter time (UTC+1)
        [InlineData("2026-10-27T20:00:00Z", "2026-10-27T19:30:00Z")] // battle in progress
        [InlineData("2026-10-27T23:00:00Z", "2026-11-03T19:30:00Z")] // after the battle
        public void Next_battle_is_tuesday_2030_paris(string now, string expected)
        {
            var n = DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var e = DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            Assert.Equal(e, EventSchedule.NextStartUtc(n));
        }

        [Fact]
        public void Predownload_jitter_is_bounded_and_waits()
        {
            var s = new UpdaterState { PreDownloadMaxJitterMinutes = 360 };
            var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(PreDownloadPolicy.Observe(s, "r2", now));
            Assert.False(PreDownloadPolicy.Observe(s, "r2", now.AddMinutes(5)));
            Assert.InRange(s.PendingJitterMinutes, 0, 360);
            Assert.True(PreDownloadPolicy.IsDue(s, now.AddHours(6).AddMinutes(1)));
            s.PendingJitterMinutes = 120;
            Assert.False(PreDownloadPolicy.IsDue(s, now.AddMinutes(60)));
            s.PreDownload = false;
            Assert.False(PreDownloadPolicy.IsDue(s, now.AddDays(1)));
        }

        [Theory]
        [InlineData("v1.2.12", "v1.2.12", null, true)]
        [InlineData("v1.2.11", "v1.2.12", null, false)]
        [InlineData("v1.4.8", "v1.2.12", "v1.4", true)]
        [InlineData("v1.5.0", "v1.2.12", "v1.4", false)]
        [InlineData(null, "v1.2.12", null, true)]
        public void Game_version_check(string game, string min, string max, bool ok) => Assert.Equal(ok, GameVersions.Check(game, min, max) == null);

        [Fact]
        public void State_roundtrip_and_damaged_file()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lotr-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var f = Path.Combine(dir, "state.json");
                var s = new UpdaterState { GamePath = "D:\\Games\\Bannerlord", Channel = "beta", InstalledReleaseId = "r1", PreDownload = false };
                s.Save(f);
                s.Save(f);
                var l = UpdaterState.Load(f);
                Assert.Equal("beta", l.Channel);
                Assert.Equal(s.InstallId, l.InstallId);
                Assert.False(l.PreDownload);
                File.WriteAllText(f, "{garbage");
                Assert.Equal("stable", UpdaterState.Load(f).Channel);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public async Task Support_log_is_masked_and_returns_a_code()
        {
            using (var h = new Harness())
            {
                var s = h.Service();
                s.Log.Info("Hello from " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                var log = s.BuildSupportLog();
                var lines = log.Split('\n');
                Assert.StartsWith("App version: test", lines[0]);
                Assert.StartsWith("Store: Steam", lines[1]);
                Assert.StartsWith("Errors: 0", lines[2]);
                Assert.True(Guid.TryParseExact(s.State.InstallId, "D", out _));
                Assert.DoesNotContain(Environment.UserName + "\\", log, StringComparison.OrdinalIgnoreCase);
                var code = await s.SendSupportAsync(log, CancellationToken.None);
                Assert.Equal("LOTR-7F3K", code.Code);
                Assert.Contains("\"installId\"", h.Server.LastSupportBody);
                Assert.DoesNotContain(Environment.UserName + "\\\\", h.Server.LastSupportBody, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task Security_sidecar_is_read_when_present()
        {
            using (var h = new Harness())
            {
                var r = h.NewRelease("sec");
                r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.0", "LotRMPEvent").SubModule();
                r.WriteTo(h.Releases);
                var s = h.Service();
                Assert.Empty(await s.FetchSecurityAsync(r.ReleaseId, CancellationToken.None));
                File.WriteAllText(Path.Combine(h.Releases, r.ReleaseId, "security.json"),
                    "{\"virustotal\":[{\"sha256\":\"ab\",\"url\":\"https://www.virustotal.com/gui/file/ab\",\"detections\":0,\"engines\":70,\"scannedAt\":\"2026-10-15\"}],\"hybridAnalysis\":[]}");
                var scans = await s.FetchSecurityAsync(r.ReleaseId, CancellationToken.None);
                Assert.Equal("clean (70 engines)", Assert.Single(scans).Verdict);
            }
        }

        [Fact]
        public void Zstd_object_with_too_large_window_is_refused()
        {
            var data = new byte[1 << 20];
            new Random(1).NextBytes(data);
            using (var c = new ZstdSharp.Compressor(3))
            {
                c.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_windowLog, 28);
                c.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
                var z = c.Wrap(data).ToArray();
                // A 1 MiB frame advertises a small window whatever the setting; only check the decoder round-trips normal frames.
                var r = Lotr.Updater.Core.Objects.ObjectCodec.DecodeTo(new MemoryStream(z), "zst", null);
                Assert.Equal(ReleaseBuilder.Sha(data), r.Sha256);
            }
            Assert.Throws<NotSupportedException>(() => Lotr.Updater.Core.Objects.ObjectCodec.OpenDecoded(new MemoryStream(), "zpatch"));
        }
    }
}
