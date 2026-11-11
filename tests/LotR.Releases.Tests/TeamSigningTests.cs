using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Team signing (docs/TEAM-SIGNING.md): the server publishes without a key (pending), a PC reviews the pending
    /// manifest against its own module folders and signs, the server installs the signature (and signed updater files).
    /// </summary>
    public class TeamSigningTests
    {
        private static void WriteModule(string dir, string id, string version, params (string Path, string Text)[] files)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SubModule.xml"), "<Module><Name value=\"" + id + "\"/><Id value=\"" + id + "\"/><Version value=\"" + version + "\"/></Module>\n");
            foreach (var f in files)
            {
                string p = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, f.Text);
            }
        }

        private sealed class Bench : IDisposable
        {
            public Bench()
            {
                T = new TempDir("lotr-team-");
                Releases = T.Sub("server", "files", "releases");
                ServerEvent = T.Sub("server", "team-repo", "EvMod");
                ServerCore = T.Sub("server", "team-repo", "CoreMod");
                PcEvent = T.Sub("pc", "repo", "EvMod");
                PcCore = T.Sub("pc", "game", "Modules", "CoreMod");
                Key = EcKeys.Generate();
                Other = EcKeys.Generate();
                Trusted = new List<TrustedKey> { new TrustedKey(EcKeys.ExportSpki(Key)) };
                WriteModule(ServerEvent, "EvMod", "v1.0.0", ("ModuleData/a.xml", "<a>1</a>\n"), ("GUI/b.txt", "bee\n"));
                WriteModule(ServerCore, "CoreMod", "v2.0.0", ("Assets/big.txt", new string('x', 5000)));
                Copy(ServerEvent, PcEvent);
                Copy(ServerCore, PcCore);
            }

            public TempDir T { get; }

            public string Releases { get; }

            public string ServerEvent { get; }

            public string ServerCore { get; }

            public string PcEvent { get; }

            public string PcCore { get; }

            public System.Security.Cryptography.ECDsa Key { get; }

            public System.Security.Cryptography.ECDsa Other { get; }

            public List<TrustedKey> Trusted { get; }

            public PublishRequest Req(DateTime now, bool pending)
            {
                var r = new PublishRequest
                {
                    ReleasesRoot = Releases,
                    Modules =
                    {
                        new ModuleSource { Folder = ServerEvent, Role = ModuleRoles.Event },
                        new ModuleSource { Folder = ServerCore, Role = ModuleRoles.Core },
                    },
                    BaseUrl = "https://example.invalid",
                    PublishedBy = "test",
                    NowUtc = now,
                    Pending = pending,
                    Threads = 2,
                };
                if (pending)
                {
                    r.TrustedKeys.AddRange(Trusted);
                }
                else
                {
                    r.Signers.Add(new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(Key))));
                }

                return r;
            }

            public PendingReviewRequest Review(string id, string liveId)
            {
                var req = new PendingReviewRequest
                {
                    ManifestBytes = File.ReadAllBytes(Path.Combine(Releases, id, "manifest.json")),
                    ExpectedReleaseId = id,
                    ExpectedBaseUrl = "https://example.invalid",
                    Threads = 2,
                };
                if (liveId != null)
                {
                    req.CurrentManifestBytes = File.ReadAllBytes(Path.Combine(Releases, liveId, "manifest.json"));
                    string sig = Path.Combine(Releases, liveId, "manifest.json.sig");
                    req.CurrentSignatureBytes = File.Exists(sig) ? File.ReadAllBytes(sig) : null;
                }

                req.Trusted.AddRange(Trusted);
                req.ModuleRoots.Add(T.Sub("pc", "repo"));
                req.ModuleRoots.Add(T.Sub("pc", "game", "Modules"));
                return req;
            }

            public byte[] Sign(string id, System.Security.Cryptography.ECDsa key = null)
            {
                using (var s = new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key ?? Key))))
                {
                    return ManifestSignature.Create(File.ReadAllBytes(Path.Combine(Releases, id, "manifest.json")), new[] { s });
                }
            }

            public string Live => ChannelIndex.Load(Path.Combine(Releases, "channels.json")).Get("stable");

            public void Dispose()
            {
                Key.Dispose();
                Other.Dispose();
                T.Dispose();
            }

            private static void Copy(string from, string to)
            {
                foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    string d = Path.Combine(to, f.Substring(from.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(d));
                    File.Copy(f, d);
                }
            }
        }

        [Fact]
        public void Pending_publish_writes_an_unsigned_verified_release_and_leaves_the_channel_alone()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                File.AppendAllText(Path.Combine(b.ServerEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));

                Assert.True(r2.Pending);
                Assert.False(r2.ChannelUpdated);
                Assert.True(r2.Verification.Ok, string.Join("; ", r2.Verification.Errors));
                Assert.False(File.Exists(Path.Combine(r2.ReleaseDir, "manifest.json.sig")));
                Assert.Equal(r1.ReleaseId, b.Live);
                var p = PendingMarker.Read(b.Releases);
                Assert.Equal(r2.ReleaseId, p.ReleaseId);
                Assert.Equal("stable", p.Channel);

                // The normal verifier still refuses it (players never get an unsigned release).
                Assert.False(ReleaseVerifier.Verify(r2.ReleaseDir, b.Trusted).Ok);
                Assert.Throws<InvalidOperationException>(() => ReleasePublisher.Publish(new PublishRequest { ReleasesRoot = b.Releases, Modules = { new ModuleSource { Folder = b.ServerEvent, Role = ModuleRoles.Event } } }));
            }
        }

        [Fact]
        public void Pc_review_accepts_unchanged_and_locally_identical_modules_then_server_attach_moves_the_channel()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                File.AppendAllText(Path.Combine(b.ServerEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                File.AppendAllText(Path.Combine(b.PcEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));

                // Core is byte-identical to the signed live module: no local check needed (move the PC copy away).
                Directory.Move(b.PcCore, b.PcCore + "-away");
                var review = PendingReviewer.Review(b.Review(r2.ReleaseId, r1.ReleaseId));
                Assert.True(review.Ok, string.Join("; ", review.Errors.Concat(review.Modules.SelectMany(m => m.Problems))));
                Assert.Equal("verified", review.Modules.Single(m => m.Folder == "EvMod").Status);
                Assert.Equal("unchanged", review.Modules.Single(m => m.Folder == "CoreMod").Status);
                Assert.Equal(1, review.Modules.Single(m => m.Folder == "EvMod").ChangedFromLive);

                var res = ReleaseSignatureAttacher.Attach(b.Releases, r2.ReleaseId, b.Sign(r2.ReleaseId), b.Trusted, 2);
                Assert.True(res.ChannelUpdated);
                Assert.Equal(r1.ReleaseId, res.PreviousChannelRelease);
                Assert.Equal(r2.ReleaseId, b.Live);
                Assert.Null(PendingMarker.Read(b.Releases));
                Assert.True(ReleaseVerifier.Verify(r2.ReleaseDir, b.Trusted).Ok);

                // Applying the same signature again changes nothing.
                var again = ReleaseSignatureAttacher.Attach(b.Releases, r2.ReleaseId, b.Sign(r2.ReleaseId), b.Trusted, 2);
                Assert.False(again.ChannelUpdated);
            }
        }

        [Fact]
        public void Pc_review_refuses_a_module_the_pc_does_not_have_identically()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                // The server's copy changes; the PC's copy does not (a planted file, or the PC is not up to date).
                File.WriteAllText(Path.Combine(b.ServerEvent, "evil.dll"), "MZ planted");
                File.AppendAllText(Path.Combine(b.ServerEvent, "GUI", "b.txt"), "changed\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));
                var review = PendingReviewer.Review(b.Review(r2.ReleaseId, r1.ReleaseId));
                Assert.False(review.Ok);
                var ev = review.Modules.Single(m => m.Folder == "EvMod");
                Assert.Equal("failed", ev.Status);
                Assert.Contains(ev.Problems, p => p.Contains("evil.dll") && p.StartsWith("Missing"));
                Assert.Contains(ev.Problems, p => p.Contains("GUI/b.txt"));

                // No local copy at all: refused too.
                Directory.Delete(b.PcEvent, true);
                review = PendingReviewer.Review(b.Review(r2.ReleaseId, r1.ReleaseId));
                Assert.Contains("no local copy", review.Modules.Single(m => m.Folder == "EvMod").Problems.Single());
            }
        }

        [Fact]
        public void Pc_review_refuses_a_live_release_not_signed_by_the_team_key_and_changed_endpoints_or_bytes()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));
                Assert.True(PendingReviewer.Review(b.Review(r2.ReleaseId, r1.ReleaseId)).Ok);

                // Live release re-signed by another key (a hacked server with its own key).
                var req = b.Review(r2.ReleaseId, r1.ReleaseId);
                using (var s = new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(b.Other))))
                {
                    req.CurrentSignatureBytes = ManifestSignature.Create(req.CurrentManifestBytes, new[] { s });
                }

                Assert.Contains(PendingReviewer.Review(req).Errors, e => e.Contains("LIVE release does not verify"));

                // Endpoints pointed elsewhere.
                req = b.Review(r2.ReleaseId, r1.ReleaseId);
                var m = ManifestCodec.Parse(req.ManifestBytes);
                m.Endpoints.Base = "https://evil.invalid";
                req.ManifestBytes = ManifestCodec.ToCanonicalBytes(m);
                Assert.Contains(PendingReviewer.Review(req).Errors, e => e.Contains("evil.invalid"));
                m.Endpoints.Base = "https://example.invalid";
                m.Endpoints.Queue = "https://evil.invalid/q";
                req.ManifestBytes = ManifestCodec.ToCanonicalBytes(m);
                Assert.Contains(PendingReviewer.Review(req).Errors, e => e.Contains("endpoints"));

                // Not canonical (same JSON, extra whitespace): refused.
                req = b.Review(r2.ReleaseId, r1.ReleaseId);
                req.ManifestBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(req.ManifestBytes) + "\n");
                Assert.Contains(PendingReviewer.Review(req).Errors, e => e.Contains("canonical"));

                // Older than the live release.
                ReleaseSignatureAttacher.Attach(b.Releases, r2.ReleaseId, b.Sign(r2.ReleaseId), b.Trusted, 2);
                req = b.Review(r1.ReleaseId, r2.ReleaseId);
                Assert.Contains(PendingReviewer.Review(req).Errors, e => e.Contains("not newer"));
            }
        }

        [Fact]
        public void Server_attach_refuses_a_foreign_signature_and_a_damaged_release()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                File.AppendAllText(Path.Combine(b.ServerEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));
                string sigPath = Path.Combine(r2.ReleaseDir, "manifest.json.sig");

                Assert.Throws<InvalidOperationException>(() => ReleaseSignatureAttacher.Attach(b.Releases, r2.ReleaseId, b.Sign(r2.ReleaseId, b.Other), b.Trusted, 2));
                Assert.False(File.Exists(sigPath));
                Assert.Equal(r1.ReleaseId, b.Live);

                // An object damaged on the server after the PC signed: the signature is not kept, the channel stays.
                var obj = Directory.GetFiles(Path.Combine(r2.ReleaseDir, "objects"), "*", SearchOption.AllDirectories).First();
                File.SetAttributes(obj, FileAttributes.Normal);
                File.WriteAllBytes(obj, new byte[] { 1, 2, 3 });
                Assert.Throws<InvalidDataException>(() => ReleaseSignatureAttacher.Attach(b.Releases, r2.ReleaseId, b.Sign(r2.ReleaseId), b.Trusted, 2));
                Assert.False(File.Exists(sigPath));
                Assert.Equal(r1.ReleaseId, b.Live);
                Assert.NotNull(PendingMarker.Read(b.Releases));
            }
        }

        [Fact]
        public void Pc_review_tries_every_local_copy_and_keeps_the_one_that_matches()
        {
            using (var b = new Bench())
            {
                var r1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: false));
                File.AppendAllText(Path.Combine(b.ServerEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: true));

                // First root: an old copy (pc\repo\EvMod, unchanged). Second root: an up-to-date copy.
                string fresh = b.T.Sub("pc", "fresh", "EvMod");
                WriteModule(fresh, "EvMod", "v1.0.0", ("ModuleData/a.xml", "<a>1</a>\n<!-- v2 -->\n"), ("GUI/b.txt", "bee\n"));
                var req = b.Review(r2.ReleaseId, r1.ReleaseId);
                var reviewOld = PendingReviewer.Review(req);
                Assert.False(reviewOld.Ok);
                Assert.Contains(reviewOld.Modules.Single(m => m.Folder == "EvMod").Problems, p => p.Contains("ModuleData/a.xml"));

                req.ModuleRoots.Add(b.T.Sub("pc", "fresh"));
                var review = PendingReviewer.Review(req);
                Assert.True(review.Ok, string.Join("; ", review.Modules.SelectMany(m => m.Problems)));
                Assert.Equal(Path.GetFullPath(fresh), review.Modules.Single(m => m.Folder == "EvMod").LocalFolder);
            }
        }

        [Fact]
        public void Server_attach_of_an_older_release_signs_it_but_does_not_move_the_channel()
        {
            using (var b = new Bench())
            {
                var p1 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), pending: true));
                File.AppendAllText(Path.Combine(b.ServerEvent, "ModuleData", "a.xml"), "<!-- v2 -->\n");
                var r2 = ReleasePublisher.Publish(b.Req(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), pending: false));
                var res = ReleaseSignatureAttacher.Attach(b.Releases, p1.ReleaseId, b.Sign(p1.ReleaseId), b.Trusted, 2);
                Assert.False(res.ChannelUpdated);
                Assert.Single(res.Warnings);
                Assert.Equal(r2.ReleaseId, b.Live);
            }
        }

        // ------------------------------------------------------------------------------------------- updater

        private static string SignedUpdater(TempDir t, string sub, string version, byte[] exe, System.Security.Cryptography.ECDsa key)
        {
            string build = t.Sub(sub + "-build", "LotRMPUpdater.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(build));
            File.WriteAllBytes(build, exe);
            string dir = t.Sub(sub, "updater");
            var req = new UpdaterPublishRequest { ExePath = build, UpdaterDir = dir, Version = version, BaseUrl = "https://example.invalid" };
            if (key != null)
            {
                req.Signers.Add(new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key))));
            }

            UpdaterPublisher.Publish(req);
            return Path.Combine(dir, "latest.json");
        }

        [Fact]
        public void Updater_signed_on_the_pc_is_installed_once_and_never_downgraded()
        {
            using (var t = new TempDir("lotr-team-upd-"))
            using (var key = EcKeys.Generate())
            using (var other = EcKeys.Generate())
            {
                var trusted = new List<TrustedKey> { new TrustedKey(EcKeys.ExportSpki(key)) };
                string live = t.Sub("server", "files", "updater");
                var exe3 = TestData.SplitMix(3, 120_000);
                string l3 = SignedUpdater(t, "pc3", "0.3.3", exe3, key);

                var r = UpdaterInstaller.Apply(l3, live, trusted);
                Assert.False(r.AlreadyLive);
                Assert.Equal("0.3.3", r.Version);
                Assert.Equal(exe3, File.ReadAllBytes(Path.Combine(live, "LotRMPUpdater-0.3.3.exe")));
                Assert.Equal(File.ReadAllBytes(l3), File.ReadAllBytes(Path.Combine(live, "latest.json")));
                Assert.True(ManifestSignature.Verify(File.ReadAllBytes(Path.Combine(live, "latest.json")), File.ReadAllBytes(Path.Combine(live, "latest.json.sig")), trusted).Valid);
                Assert.Equal(Sha256Util.Of(exe3), Sha256SumFile.Parse(File.ReadAllText(Path.Combine(live, "LotRMPUpdater-0.3.3.exe.sha256"))).Sha256);
                Assert.True(UpdaterInstaller.Apply(l3, live, trusted).AlreadyLive);

                // Older version: refused without --force.
                string l2 = SignedUpdater(t, "pc2", "0.3.2", TestData.SplitMix(2, 100_000), key);
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.Apply(l2, live, trusted));
                Assert.Contains("\"0.3.3\"", File.ReadAllText(Path.Combine(live, "latest.json")));

                // Signed by another key, unsigned, or an exe swapped after signing: refused, nothing written.
                string lo = SignedUpdater(t, "pco", "0.3.4", TestData.SplitMix(4, 100_000), other);
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.Apply(lo, live, trusted));
                string lu = SignedUpdater(t, "pcu", "0.3.5", TestData.SplitMix(5, 100_000), null);
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.Apply(lu, live, trusted));
                string ls = SignedUpdater(t, "pcs", "0.3.6", TestData.SplitMix(6, 100_000), key);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(ls), "LotRMPUpdater-0.3.6.exe"), TestData.SplitMix(66, 100_000));
                Assert.Throws<InvalidDataException>(() => UpdaterInstaller.Apply(ls, live, trusted));
                Assert.False(File.Exists(Path.Combine(live, "LotRMPUpdater-0.3.4.exe")));
                Assert.False(File.Exists(Path.Combine(live, "LotRMPUpdater-0.3.6.exe")));
                Assert.Contains("\"0.3.3\"", File.ReadAllText(Path.Combine(live, "latest.json")));

                // Newer, properly signed: installed.
                string l4 = SignedUpdater(t, "pc4", "0.4.0", TestData.SplitMix(40, 100_000), key);
                Assert.Equal("0.3.3", UpdaterInstaller.Apply(l4, live, trusted).PreviousVersion);
                Assert.Contains("\"0.4.0\"", File.ReadAllText(Path.Combine(live, "latest.json")));
            }
        }

        [Theory]
        [InlineData("0.3.3", "0.3.2", 1)]
        [InlineData("0.3.10", "0.3.9", 1)]
        [InlineData("0.4.0", "0.4.0.0", 0)]
        [InlineData("1.0.0-beta", "1.0.0", -1)]
        [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
        public void Updater_versions_compare_numerically(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(UpdaterInstaller.CompareVersions(a, b)));

        // ------------------------------------------------------------------------------------------- key

        [Fact]
        public void User_scope_key_round_trips_and_is_not_a_plain_pem()
        {
            using (var t = new TempDir("lotr-team-key-"))
            using (var key = EcKeys.Generate())
            {
                string path = t.Sub("release-signing.key");
                KeyStore.Save(path, key, KeyProtection.DpapiUser, applyAcl: true);
                string pem = File.ReadAllText(path);
                Assert.Contains("BEGIN " + KeyStore.DpapiUserLabel, pem);
                Assert.DoesNotContain("BEGIN PRIVATE KEY", pem);
                using (var back = KeyStore.Load(path))
                {
                    Assert.Equal(EcKeys.ExportSpki(key), EcKeys.ExportSpki(back));
                }

                Assert.Equal(KeyProtection.DpapiUser, KeyStore.ParseProtection("dpapi-user", KeyProtection.Plain));
                Assert.Throws<ArgumentException>(() => KeyStore.ParseProtection("usb", KeyProtection.Plain));
            }
        }
    }
}
