using System;
using System.IO;
using System.Linq;
using System.Threading;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Lotr.Updater.Core.SelfUpdate;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>
    /// lotr-updater (Linux) only TELLS the player about a newer version: it reads files/updater/linux/latest.json (never
    /// the Windows updater/latest.json), believes it only when signed with the release key, and downloads nothing.
    /// </summary>
    public class LinuxLauncherCheckTests
    {
        /// <summary>Writes &lt;files&gt;/updater/linux/latest.json (+ .sig with <paramref name="key"/>, none when null).</summary>
        internal static void PublishLinuxLatest(string serverFiles, string version, System.Security.Cryptography.ECDsa key, string url = null)
        {
            string dir = Path.Combine(serverFiles, "updater", "linux");
            Directory.CreateDirectory(dir);
            var latest = new UpdaterLatest
            {
                Version = version,
                Url = url ?? LinuxUpdaterArchive.UrlPath(version),
                Sha256 = Sha256Util.Of(new byte[] { 1, 2, 3 }),
                Size = 12_000_000,
                ReleasedAt = "2026-11-14T09:00:00Z",
            };
            byte[] bytes = latest.ToJson().ToCanonicalBytes();
            File.WriteAllBytes(Path.Combine(dir, "latest.json"), bytes);
            string sig = Path.Combine(dir, "latest.json.sig");
            if (key == null)
            {
                File.Delete(sig);
                return;
            }

            using (var copy = EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key)))
            using (var signer = new ReleaseSigner(copy))
            {
                File.WriteAllBytes(sig, ManifestSignature.Create(bytes, new[] { signer }));
            }
        }

        private static LinuxLauncherCheck Check(Harness h, Lotr.Updater.Core.UpdaterService svc, string current, bool withKey = true) =>
            new LinuxLauncherCheck(svc.Endpoints, svc.Fetcher, withKey ? new[] { ReleaseBuilder.PublicPem(h.Key) } : new string[0], svc.Log, current);

        [Fact]
        public void A_newer_signed_linux_release_gives_a_notice_with_the_install_page()
        {
            using (var h = new Harness())
            {
                var svc = h.Service();
                PublishLinuxLatest(h.ServerFiles, "0.2.0", h.Key);
                var n = Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                Assert.NotNull(n);
                Assert.Equal("0.2.0", n.Version);
                Assert.Equal("0.1.0", n.CurrentVersion);
                Assert.Equal(h.Server.BaseUrl + "install/", n.DownloadPage);
                Assert.Equal(1, h.Server.Hits("/files/updater/linux/latest.json"));
                Assert.Equal(0, h.Server.Hits("/files/updater/latest.json"));

                // Same or older version: nothing to say. A pre-release is older than its release.
                Assert.Null(Check(h, svc, "0.2.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                Assert.Null(Check(h, svc, "0.3.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                Assert.NotNull(Check(h, svc, "0.2.0-beta.1").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());

                // Nothing downloaded, nothing staged.
                Assert.False(Directory.Exists(svc.Paths.UpdateDir) && Directory.EnumerateFileSystemEntries(svc.Paths.UpdateDir).Any());
            }
        }

        [Fact]
        public void Never_reads_the_windows_pointer()
        {
            using (var h = new Harness())
            {
                var svc = h.Service();
                string win = Path.Combine(h.ServerFiles, "updater");
                Directory.CreateDirectory(win);
                var exe = Path.Combine(h.Root, "LotRMPUpdater.exe");
                File.WriteAllBytes(exe, EngineTests.Random(10_000, 3));
                var req = new UpdaterPublishRequest { ExePath = exe, UpdaterDir = win, Version = "9.9.9" };
                req.Signers.Add(new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(h.Key))));
                UpdaterPublisher.Publish(req);
                req.Signers[0].Dispose();

                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                Assert.Equal(0, h.Server.Hits("/files/updater/latest.json"));
                Assert.Equal(1, h.Server.Hits("/files/updater/linux/latest.json"));
            }
        }

        [Fact]
        public void Unsigned_foreign_signed_or_foreign_url_pointers_are_ignored_quietly()
        {
            using (var h = new Harness())
            using (var other = ReleaseBuilder.NewTestKey())
            {
                var svc = h.Service();
                PublishLinuxLatest(h.ServerFiles, "0.2.0", null);
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                PublishLinuxLatest(h.ServerFiles, "0.2.0", other);
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                PublishLinuxLatest(h.ServerFiles, "0.2.0", h.Key, "https://evil.example/files/updater/linux/lotr-updater-0.2.0-linux-x64.tar.gz");
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                PublishLinuxLatest(h.ServerFiles, "0.2.0", h.Key, "/files/updater/LotRMPUpdater-0.2.0.exe");
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());

                // A build without any release key (dev) takes an unsigned pointer, like the Windows self-update does.
                PublishLinuxLatest(h.ServerFiles, "0.2.0", null);
                Assert.NotNull(Check(h, svc, "0.1.0", withKey: false).CheckAsync(CancellationToken.None).GetAwaiter().GetResult());

                var log = string.Concat(Directory.GetFiles(svc.Paths.LogDir).Select(File.ReadAllText));
                Assert.Contains("linux/latest.json ignored: not signed with the LotR team key", log);
                Assert.Contains("does not point at a lotr-updater archive on this server", log);
            }
        }

        [Fact]
        public void Nothing_published_or_a_broken_server_gives_no_notice_and_no_error()
        {
            using (var h = new Harness())
            {
                var svc = h.Service();
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                string dir = Path.Combine(h.ServerFiles, "updater", "linux");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "latest.json"), "{ not json");
                Assert.Null(Check(h, svc, "0.1.0", withKey: false).CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                h.Server.Faults = (path, n) => path.EndsWith("/linux/latest.json", StringComparison.Ordinal) ? new Fault { Status = 500 } : null;
                Assert.Null(Check(h, svc, "0.1.0").CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
            }
        }

        [Fact]
        public void Cancellation_is_not_swallowed()
        {
            using (var h = new Harness())
            using (var cts = new CancellationTokenSource())
            {
                var svc = h.Service();
                PublishLinuxLatest(h.ServerFiles, "0.2.0", h.Key);
                cts.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => Check(h, svc, "0.1.0").CheckAsync(cts.Token).GetAwaiter().GetResult());
            }
        }
    }
}
