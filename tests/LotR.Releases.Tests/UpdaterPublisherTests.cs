using System;
using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Xunit;

namespace LotR.Releases.Tests
{
    /// <summary>publish-updater: LotRMPUpdater-&lt;version&gt;.exe + .sha256 + latest.json (+ .sig), atomic, latest.json last.</summary>
    public class UpdaterPublisherTests
    {
        private static byte[] Bytes(int n, int seed)
        {
            var b = new byte[n];
            new Random(seed).NextBytes(b);
            return b;
        }

        [Fact]
        public void Publishes_exe_sha256_and_signed_latest_json()
        {
            using (var t = new TempDir())
            using (var key = EcKeys.Generate())
            {
                var exe = t.Sub("build", "LotRMPUpdater.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exe));
                var bytes = Bytes(150_000, 1);
                File.WriteAllBytes(exe, bytes);
                var dir = t.Sub("files", "updater");
                var req = new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "0.2.0", NowUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc) };
                req.Signers.Add(new ReleaseSigner(key));
                var r = UpdaterPublisher.Publish(req);

                Assert.Equal(Path.Combine(dir, "LotRMPUpdater-0.2.0.exe"), r.ExeFile);
                Assert.Equal(bytes, File.ReadAllBytes(r.ExeFile));
                var sum = Sha256SumFile.Parse(File.ReadAllText(r.Sha256File));
                Assert.Equal(Sha256Util.Of(bytes), sum.Sha256);
                Assert.Equal("LotRMPUpdater-0.2.0.exe", sum.FileName);
                Assert.EndsWith("\n", File.ReadAllText(r.Sha256File));

                var latestBytes = File.ReadAllBytes(Path.Combine(dir, "latest.json"));
                var latest = UpdaterLatest.Parse(latestBytes);
                Assert.True(latest.Validate().IsValid);
                Assert.Equal("0.2.0", latest.Version);
                Assert.Equal("/files/updater/LotRMPUpdater-0.2.0.exe", latest.Url);
                Assert.Equal(bytes.LongLength, latest.Size);
                Assert.Equal("2026-10-15T12:00:00Z", latest.ReleasedAt);
                Assert.False(latest.Signed); // random bytes: no Authenticode
                var check = ManifestSignature.Verify(latestBytes, File.ReadAllBytes(r.SignatureFile), new[] { new TrustedKey(EcKeys.ExportSpki(key)) });
                Assert.True(check.Valid, check.Message);
                // No temporary files left behind (atomic writes rename them).
                Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
                Assert.Equal(4, Directory.GetFiles(dir).Length);
            }
        }

        [Fact]
        public void Same_bytes_again_is_idempotent_and_different_bytes_need_a_new_version()
        {
            using (var t = new TempDir())
            {
                var exe = t.Sub("LotRMPUpdater.exe");
                File.WriteAllBytes(exe, Bytes(10_000, 2));
                var dir = t.Sub("updater");
                UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0" });
                Assert.True(UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0" }).AlreadyPublished);
                File.WriteAllBytes(exe, Bytes(10_000, 3));
                Assert.Throws<InvalidOperationException>(() => UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0" }));
                Assert.False(UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0", Force = true }).AlreadyPublished);
                Assert.Equal(Sha256Util.OfFile(exe), UpdaterLatest.Parse(File.ReadAllBytes(Path.Combine(dir, "latest.json"))).Sha256);
            }
        }

        [Fact]
        public void Beta_channel_base_url_version_from_the_exe_and_stale_signature_removal()
        {
            using (var t = new TempDir())
            using (var key = EcKeys.Generate())
            {
                // A real PE with a version resource: this test assembly (its version comes from Directory.Build.props).
                var exe = t.Sub("LotRMPUpdater.exe");
                File.Copy(typeof(UpdaterPublisherTests).Assembly.Location, exe);
                var dir = t.Sub("updater");
                var req = new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Channel = "beta", BaseUrl = "https://lotr.example/" };
                req.Signers.Add(new ReleaseSigner(key));
                var r = UpdaterPublisher.Publish(req);
                Assert.Equal(Path.Combine(dir, "latest-beta.json"), r.LatestFile);
                Assert.False(File.Exists(Path.Combine(dir, "latest.json")));
                Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", r.Latest.Version);
                Assert.Equal(UpdaterPublisher.VersionOf(exe), r.Latest.Version);
                Assert.Equal("https://lotr.example/files/updater/LotRMPUpdater-" + r.Latest.Version + ".exe", r.Latest.Url);
                Assert.True(File.Exists(r.LatestFile + ".sig"));

                // Re-published without a key: the old signature (of the old latest-beta.json) must not stay next to it.
                UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Channel = "beta", Version = "9.9.9" });
                Assert.False(File.Exists(r.LatestFile + ".sig"));

                Assert.Throws<ArgumentException>(() => UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "not-a-version" }));
                Assert.Throws<ArgumentException>(() => UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0", BaseUrl = "http://plain.example" }));
                Assert.Throws<ArgumentException>(() => UpdaterPublisher.Publish(new UpdaterPublishRequest { ExePath = exe, UpdaterDir = dir, Version = "1.0.0", Channel = "../x" }));
            }
        }
    }
}
