using System.IO;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Cli.Tests
{
    /// <summary>
    /// "A new version of lotr-updater is available": status / install read files/updater/linux/latest.json once the server
    /// answered, print where to get the new version, and change neither the exit code nor anything on disk.
    /// </summary>
    public class LauncherNoticeTests
    {
        private const string Notice = "A new version of lotr-updater is available";

        private static void PublishLinuxLatest(CliSandbox s, string version, bool sign = true)
        {
            string dir = Path.Combine(s.ServerFiles, "updater", "linux");
            Directory.CreateDirectory(dir);
            byte[] bytes = new UpdaterLatest
            {
                Version = version,
                Url = LinuxUpdaterArchive.UrlPath(version),
                Sha256 = Sha256Util.Of(new byte[] { 4, 2 }),
                Size = 12_000_000,
                ReleasedAt = "2026-11-14T09:00:00Z",
            }.ToJson().ToCanonicalBytes();
            File.WriteAllBytes(Path.Combine(dir, "latest.json"), bytes);
            if (sign)
            {
                using (var copy = EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(s.Key)))
                using (var signer = new ReleaseSigner(copy))
                {
                    File.WriteAllBytes(Path.Combine(dir, "latest.json.sig"), ManifestSignature.Create(bytes, new[] { signer }));
                }
            }
        }

        [Fact]
        public void Status_and_install_tell_about_a_newer_signed_lotr_updater_without_changing_the_exit_code()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                PublishLinuxLatest(s, "99.0.0");
                var status = s.Run(s.Dev("status"));
                Assert.True(status.Code == ExitCodes.UpdateAvailable, status.ToString());
                Assert.Contains(Notice + ": 99.0.0 (you have " + CliApp.Version + ").", status.Out);
                Assert.Contains("Get it from " + s.Server.BaseUrl + "install/ (Linux / Steam Deck)", status.Out);

                var install = s.Run(s.Dev("install", "--yes"));
                Assert.True(install.Code == ExitCodes.Ok, install.ToString());
                Assert.Contains(Notice + ": 99.0.0", install.Out);
                Assert.Equal(install.Out.IndexOf(Notice), install.Out.LastIndexOf(Notice)); // once per run

                // Nothing was downloaded: the archive is not even on this server.
                Assert.Equal(0, s.Server.Hits("/files/updater/linux/lotr-updater-99.0.0-linux-x64.tar.gz"));
                Assert.Equal(0, s.Server.Hits("/files/updater/latest.json"));
            }
        }

        [Fact]
        public void No_notice_for_the_same_version_an_unsigned_pointer_or_a_newer_windows_updater()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();

                // Only the Windows updater is newer: the Linux updater never reads its latest.json.
                string win = Path.Combine(s.ServerFiles, "updater");
                Directory.CreateDirectory(win);
                string exe = Path.Combine(s.Root, "LotRMPUpdater.exe");
                File.WriteAllBytes(exe, CliSandbox.Random(5_000, 9));
                UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = win, Version = "99.0.0" });
                var r = s.Run(s.Dev("status"));
                Assert.DoesNotContain(Notice, r.Out);
                Assert.Equal(0, s.Server.Hits("/files/updater/latest.json"));
                Assert.Equal(1, s.Server.Hits("/files/updater/linux/latest.json"));

                PublishLinuxLatest(s, CliApp.Version);
                Assert.DoesNotContain(Notice, s.Run(s.Dev("status")).Out);

                PublishLinuxLatest(s, "99.0.0", sign: false);
                var unsigned = s.Run(s.Dev("status"));
                Assert.True(unsigned.Code == ExitCodes.UpdateAvailable, unsigned.ToString());
                Assert.DoesNotContain(Notice, unsigned.Out);
            }
        }

        [Fact]
        public void A_failed_check_does_not_ask_for_the_launcher_pointer()
        {
            using (var s = new CliSandbox())
            {
                s.Publish();
                PublishLinuxLatest(s, "99.0.0");
                s.Server.Faults = (path, hit) => new Fault { Status = 502 };
                var r = s.Run(s.Dev("status"));
                Assert.True(r.Code == ExitCodes.Network, r.ToString());
                Assert.DoesNotContain(Notice, r.Out);
                Assert.Equal(0, s.Server.Hits("/files/updater/linux/latest.json"));
            }
        }
    }
}
