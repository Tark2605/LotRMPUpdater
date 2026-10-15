using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Hashing;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.SelfUpdate;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>Authenticode answers per file name (the real check needs signed exes; see Authenticode_of_unsigned_files).</summary>
    public sealed class FakeAuthenticode : IAuthenticodeInspector
    {
        public Dictionary<string, AuthenticodeInfo> ByFileName { get; } = new Dictionary<string, AuthenticodeInfo>(StringComparer.OrdinalIgnoreCase);
        public AuthenticodeInfo Inspect(string path) =>
            ByFileName.TryGetValue(Path.GetFileName(path), out var i) ? i : AuthenticodeInfo.Unsigned();

        public static AuthenticodeInfo SignedBy(string subject) => new AuthenticodeInfo { Signed = true, Valid = true, Subject = subject, Status = "valid signature" };
    }

    /// <summary>Self-update of LotRMPUpdater.exe against the local test server: latest.json, download, checks, staging, swap, rollback.</summary>
    public class SelfUpdateTests
    {
        private static readonly byte[] OldExe = EngineTests.Random(200_000, 41);
        private static readonly byte[] NewExe = EngineTests.Random(260_000, 42);

        private sealed class Ctx : IDisposable
        {
            public Ctx()
            {
                H = new Harness();
                Paths = new UpdaterPaths(H.Home);
                Directory.CreateDirectory(Paths.AppDir);
                File.WriteAllBytes(Paths.InstalledExe, OldExe);
                UpdaterDir = Path.Combine(H.ServerFiles, "updater");
                NewExePath = Path.Combine(H.Root, "build", "LotRMPUpdater.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(NewExePath));
                File.WriteAllBytes(NewExePath, NewExe);
                Service = H.Service();
            }

            public Harness H { get; }
            public UpdaterPaths Paths { get; }
            public string UpdaterDir { get; }
            public string NewExePath { get; }
            public FakeAuthenticode Auth { get; } = new FakeAuthenticode();
            public Lotr.Updater.Core.UpdaterService Service { get; }

            public UpdaterPublishResult Publish(string version, bool sign = true, System.Security.Cryptography.ECDsa key = null)
            {
                var req = new UpdaterPublishRequest { ExePath = NewExePath, UpdaterDir = UpdaterDir, Version = version, Force = true };
                if (sign) req.Signers.Add(new ReleaseSigner(key ?? H.Key)); // not disposed: the harness owns the key
                return UpdaterPublisher.Publish(req);
            }

            public SelfUpdater Updater(string currentVersion = "0.1.0", bool trustKey = true) =>
                new SelfUpdater(new ServerEndpoints(H.Server.BaseUrl), Service.Fetcher, Paths,
                    trustKey ? new[] { ReleaseBuilder.PublicPem(H.Key) } : new string[0], Auth, Service.Log, currentVersion, Paths.InstalledExe);

            public void Dispose() => H.Dispose();
        }

        [Fact]
        public async Task Newer_updater_is_downloaded_verified_staged_swapped_and_can_be_rolled_back()
        {
            using (var c = new Ctx())
            {
                var pub = c.Publish("0.2.0");
                Assert.True(File.Exists(pub.SignatureFile));
                var su = c.Updater();
                var offer = await su.CheckAsync("stable", null, CancellationToken.None);
                Assert.NotNull(offer);
                Assert.Equal("0.2.0", offer.Version);
                Assert.True(offer.ReleaseKeySigned);
                Assert.Equal(c.H.Server.BaseUrl + "files/updater/LotRMPUpdater-0.2.0.exe", offer.DownloadUri.ToString());

                var staged = await su.DownloadAsync(offer, null, CancellationToken.None);
                Assert.Equal(Sha256Util.Of(NewExe), staged.Sha256);
                Assert.Equal("0.2.0", su.Pending.Version);
                Assert.True(File.Exists(staged.FullPath(c.Paths)));
                Assert.Equal(OldExe, File.ReadAllBytes(c.Paths.InstalledExe)); // nothing replaced before the next start

                // Not the installed copy (e.g. run from Downloads): the update stays pending.
                Assert.False(SelfUpdateSwap.ApplyPending(c.Paths, Path.Combine(c.H.Root, "elsewhere.exe"), "0.1.0", null).Swapped);

                var swap = SelfUpdateSwap.ApplyPending(c.Paths, c.Paths.InstalledExe, "0.1.0", c.Service.Log);
                Assert.True(swap.Swapped);
                Assert.Equal(c.Paths.InstalledExe, swap.RelaunchExe);
                Assert.Equal(NewExe, File.ReadAllBytes(c.Paths.InstalledExe));
                Assert.Equal(OldExe, File.ReadAllBytes(c.Paths.PreviousExe));
                Assert.False(File.Exists(c.Paths.PendingUpdateFile));
                Assert.Equal("0.1.0", SelfUpdateSwap.PreviousVersion(c.Paths));

                // The new exe starts as 0.2.0: nothing more to do.
                Assert.False(SelfUpdateSwap.ApplyPending(c.Paths, c.Paths.InstalledExe, "0.2.0", null).Swapped);
                Assert.Null(await c.Updater("0.2.0").CheckAsync("stable", null, CancellationToken.None));

                var back = SelfUpdateSwap.Rollback(c.Paths, null);
                Assert.True(back.Swapped);
                Assert.Equal(OldExe, File.ReadAllBytes(c.Paths.InstalledExe));
                Assert.Equal(NewExe, File.ReadAllBytes(Path.Combine(c.Paths.AppDir, "LotRMPUpdater.rolledback.exe")));
                // The player rolled back from 0.2.0: it is not offered again.
                Assert.Null(await c.Updater("0.1.0").CheckAsync("stable", "0.2.0", CancellationToken.None));
            }
        }

        [Fact]
        public async Task Same_or_older_version_is_not_offered_and_no_latest_json_is_fine()
        {
            using (var c = new Ctx())
            {
                Assert.Null(await c.Updater().CheckAsync("stable", null, CancellationToken.None)); // nothing published yet
                c.Publish("0.1.0");
                Assert.Null(await c.Updater("0.1.0").CheckAsync("stable", null, CancellationToken.None));
                Assert.Null(await c.Updater("0.3.0").CheckAsync("stable", null, CancellationToken.None));
                Assert.NotNull(await c.Updater("0.1.0-beta.2").CheckAsync("stable", null, CancellationToken.None));
                // beta channel falls back to latest.json when latest-beta.json does not exist
                Assert.NotNull(await c.Updater("0.0.9").CheckAsync("beta", null, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Altered_exe_on_the_server_is_refused_and_nothing_is_staged()
        {
            using (var c = new Ctx())
            {
                var pub = c.Publish("0.2.0");
                var bad = (byte[])NewExe.Clone();
                bad[1000] ^= 0x01;
                File.WriteAllBytes(pub.ExeFile, bad);
                var su = c.Updater();
                var offer = await su.CheckAsync("stable", null, CancellationToken.None);
                var ex = await Assert.ThrowsAsync<SelfUpdateException>(() => su.DownloadAsync(offer, null, CancellationToken.None));
                Assert.Contains("SHA-256", ex.Message);
                Assert.Null(su.Pending);
                Assert.Empty(Directory.GetFiles(c.Paths.UpdateDir, "*.exe"));
            }
        }

        [Fact]
        public async Task Latest_json_signed_by_an_unknown_key_is_refused()
        {
            using (var c = new Ctx())
            using (var other = ReleaseBuilder.NewTestKey())
            {
                c.Publish("0.2.0", key: other);
                await Assert.ThrowsAsync<SelfUpdateException>(() => c.Updater().CheckAsync("stable", null, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Without_any_checkable_signature_the_new_exe_is_refused()
        {
            using (var c = new Ctx())
            {
                c.Publish("0.2.0", sign: false);
                var su = c.Updater();
                var offer = await su.CheckAsync("stable", null, CancellationToken.None);
                Assert.False(offer.ReleaseKeySigned);
                var ex = await Assert.ThrowsAsync<SelfUpdateException>(() => su.DownloadAsync(offer, null, CancellationToken.None));
                Assert.Contains("neither", ex.Message);
                Assert.Null(su.Pending);
            }
        }

        [Fact]
        public async Task Authenticode_signer_must_match_the_running_exe()
        {
            using (var c = new Ctx())
            {
                c.Publish("0.2.0");
                // Running exe signed by the team, new one signed by someone else: refused even with a valid latest.json signature.
                c.Auth.ByFileName["LotRMPUpdater.exe"] = FakeAuthenticode.SignedBy("CN=LotR MP Event team");
                c.Auth.ByFileName["LotRMPUpdater-0.2.0.exe"] = FakeAuthenticode.SignedBy("CN=Someone else");
                var su = c.Updater();
                var offer = await su.CheckAsync("stable", null, CancellationToken.None);
                var ex = await Assert.ThrowsAsync<SelfUpdateException>(() => su.DownloadAsync(offer, null, CancellationToken.None));
                Assert.Contains("Someone else", ex.Message);

                // Signed running exe, unsigned new one: refused (no downgrade to unsigned).
                c.Auth.ByFileName.Remove("LotRMPUpdater-0.2.0.exe");
                ex = await Assert.ThrowsAsync<SelfUpdateException>(() => su.DownloadAsync(offer, null, CancellationToken.None));
                Assert.Contains("not signed", ex.Message);

                // Same subject (a renewed certificate has another thumbprint): accepted, even without the release key.
                c.Auth.ByFileName["LotRMPUpdater-0.2.0.exe"] = new AuthenticodeInfo { Signed = true, Valid = true, Subject = "CN=LotR MP Event team", Thumbprint = "NEW" };
                c.Publish("0.2.0", sign: false);
                File.Delete(Path.Combine(c.UpdaterDir, "latest.json.sig"));
                var su2 = c.Updater(trustKey: false);
                var offer2 = await su2.CheckAsync("stable", null, CancellationToken.None);
                Assert.False(offer2.ReleaseKeySigned);
                Assert.Equal("0.2.0", (await su2.DownloadAsync(offer2, null, CancellationToken.None)).Version);
            }
        }

        [Fact]
        public void Download_url_must_stay_on_the_lotr_server()
        {
            using (var c = new Ctx())
            {
                var su = c.Updater();
                Assert.Equal(c.H.Server.BaseUrl + "files/updater/LotRMPUpdater-1.0.0.exe", su.ResolveUrl("/files/updater/LotRMPUpdater-1.0.0.exe").ToString());
                Assert.Throws<SelfUpdateException>(() => su.ResolveUrl("https://evil.example/files/updater/LotRMPUpdater-1.0.0.exe"));
                Assert.Throws<SelfUpdateException>(() => su.ResolveUrl("http://localhost/files/updater/LotRMPUpdater-1.0.0.exe")); // absolute must be https
                Assert.Throws<SelfUpdateException>(() => su.ResolveUrl("/files/updater/setup.exe"));
            }
        }

        [Fact]
        public void A_staged_file_that_no_longer_matches_is_discarded_at_start()
        {
            using (var c = new Ctx())
            {
                Directory.CreateDirectory(c.Paths.UpdateDir);
                File.WriteAllBytes(Path.Combine(c.Paths.UpdateDir, "LotRMPUpdater-0.2.0.exe"), NewExe);
                new PendingSelfUpdate { Version = "0.2.0", Sha256 = new string('0', 64), File = "LotRMPUpdater-0.2.0.exe", StagedAtUtc = "x" }.Save(c.Paths);
                var r = SelfUpdateSwap.ApplyPending(c.Paths, c.Paths.InstalledExe, "0.1.0", null);
                Assert.False(r.Swapped);
                Assert.Equal(OldExe, File.ReadAllBytes(c.Paths.InstalledExe));
                Assert.False(File.Exists(c.Paths.PendingUpdateFile));
                // A pending.json pointing outside update\ is ignored.
                new PendingSelfUpdate { Version = "0.2.0", Sha256 = Sha256Util.Of(NewExe), File = @"..\..\evil.exe", StagedAtUtc = "x" }.Save(c.Paths);
                Assert.Null(PendingSelfUpdate.Load(c.Paths));
            }
        }

        [Fact]
        public void Authenticode_of_unsigned_files_and_the_signer_rule()
        {
            var f = Path.Combine(Path.GetTempPath(), "lotr-authenticode-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllBytes(f, OldExe);
            try
            {
                var i = Authenticode.Inspect(f);
                Assert.False(i.Signed);
                Assert.False(i.Valid);
            }
            finally { File.Delete(f); }
            var team = FakeAuthenticode.SignedBy("CN=Team");
            Assert.Null(AuthenticodeInfo.CheckSameSigner(AuthenticodeInfo.Unsigned(), AuthenticodeInfo.Unsigned()));
            Assert.Null(AuthenticodeInfo.CheckSameSigner(AuthenticodeInfo.Unsigned(), team));
            Assert.Null(AuthenticodeInfo.CheckSameSigner(team, FakeAuthenticode.SignedBy("CN=Team")));
            Assert.NotNull(AuthenticodeInfo.CheckSameSigner(team, AuthenticodeInfo.Unsigned()));
            Assert.NotNull(AuthenticodeInfo.CheckSameSigner(team, new AuthenticodeInfo { Signed = true, Valid = false, Subject = "CN=Team", Status = "bad" }));
            Assert.NotNull(AuthenticodeInfo.CheckSameSigner(AuthenticodeInfo.Unsigned(), new AuthenticodeInfo { Signed = true, Valid = false, Status = "bad" }));
        }

        [Theory]
        [InlineData("0.1.0", "0.1.1", -1)]
        [InlineData("0.1.10", "0.1.9", 1)]
        [InlineData("1.0.0", "1.0.0.0", 0)]
        [InlineData("1.0.0-beta.1", "1.0.0", -1)]
        [InlineData("1.0.0-beta.2", "1.0.0-beta.10", 1)] // ordinal pre-release order, documented
        [InlineData("v1.2.3", "1.2.3", 0)]
        [InlineData("garbage", "0.0.1", -1)]
        public void Version_order(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(AppVersions.Compare(a, b)));

        private sealed class FileShortcutWriter : IShortcutWriter
        {
            public void Create(string lnkPath, string targetExe, string arguments, string description) =>
                File.WriteAllText(lnkPath, targetExe + "|" + arguments + "|" + description);
        }

        [Fact]
        public void Start_with_windows_is_one_shortcut_in_the_given_startup_folder_and_off_by_default()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lotr-startup-" + Guid.NewGuid().ToString("N"));
            try
            {
                var s = new StartupRegistration(dir, @"C:\Users\x\AppData\Local\LotRMPUpdater\app\LotRMPUpdater.exe", new FileShortcutWriter());
                Assert.False(s.IsEnabled);
                s.Enable();
                Assert.True(s.IsEnabled);
                Assert.Equal(Path.Combine(dir, "LotR MP Event updater.lnk"), s.ShortcutPath);
                Assert.StartsWith(@"C:\Users\x\AppData\Local\LotRMPUpdater\app\LotRMPUpdater.exe|--minimized|", File.ReadAllText(s.ShortcutPath));
                Assert.Single(Directory.GetFiles(dir));
                s.Disable();
                Assert.False(s.IsEnabled);
                s.Disable(); // idempotent
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void New_state_defaults_and_skipped_updater_version_round_trip()
        {
            var f = Path.Combine(Path.GetTempPath(), "lotr-state-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var s = new UpdaterState { SkippedUpdaterVersion = "0.2.0" };
                s.Save(f);
                Assert.Equal("0.2.0", UpdaterState.Load(f).SkippedUpdaterVersion);
            }
            finally { File.Delete(f); }
        }
    }
}
