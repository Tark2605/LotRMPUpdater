using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using LotR.Releases.Hashing;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Xunit;

namespace LotR.Releases.Tests
{
    /// <summary>
    /// A .tar.gz written like tools/Build-LinuxUpdater.ps1 does (ustar, gzip), with knobs to make it wrong on purpose.
    /// </summary>
    internal static class LinuxTar
    {
        public sealed class Entry
        {
            public string Name { get; set; }
            public int Mode { get; set; } = 420; // 0644
            public char Type { get; set; } = '0';
            public byte[] Data { get; set; } = new byte[0];
            /// <summary>Breaks the header checksum.</summary>
            public bool BadSum { get; set; }
        }

        /// <summary>A 64-bit little-endian x86-64 ELF header followed by filler (what "file" calls "ELF 64-bit LSB ... x86-64").</summary>
        public static byte[] Elf(int size = 4096, byte elfClass = 2, byte machine = 0x3E, int seed = 1)
        {
            var b = TestData.SplitMix((ulong)seed, size);
            byte[] head = { 0x7F, (byte)'E', (byte)'L', (byte)'F', elfClass, 1, 1, 0 };
            Array.Copy(head, b, head.Length);
            b[18] = machine;
            b[19] = 0;
            return b;
        }

        /// <summary>The archive Build-LinuxUpdater.ps1 makes: lotr-updater-&lt;v&gt;/ (755), lotr-updater (755), README.txt (644).</summary>
        public static List<Entry> Standard(string version, byte[] program = null) => new List<Entry>
        {
            new Entry { Name = "lotr-updater-" + version + "/", Mode = 493, Type = '5' },
            new Entry { Name = "lotr-updater-" + version + "/lotr-updater", Mode = 493, Data = program ?? Elf() },
            new Entry { Name = "lotr-updater-" + version + "/README.txt", Data = Encoding.UTF8.GetBytes("lotr-updater " + version + "\n") },
        };

        public static byte[] Build(IEnumerable<Entry> entries, bool endMarker = true)
        {
            var tar = new MemoryStream();
            foreach (var e in entries)
            {
                var h = new byte[512];
                Put(h, 0, 100, e.Name);
                Octal(h, 100, 8, e.Mode);
                Octal(h, 108, 8, 0);
                Octal(h, 116, 8, 0);
                Octal(h, 124, 12, e.Data.Length);
                Octal(h, 136, 12, 1763100000);
                Put(h, 148, 8, "        ");
                h[156] = (byte)e.Type;
                Put(h, 257, 6, "ustar\0");
                Put(h, 263, 2, "00");
                Put(h, 265, 32, "root");
                Put(h, 297, 32, "root");
                int sum = h.Sum(x => (int)x) + (e.BadSum ? 1 : 0);
                Put(h, 148, 8, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
                tar.Write(h, 0, 512);
                tar.Write(e.Data, 0, e.Data.Length);
                int pad = (512 - (e.Data.Length % 512)) % 512;
                tar.Write(new byte[pad], 0, pad);
            }

            if (endMarker)
            {
                tar.Write(new byte[1024], 0, 1024);
            }

            var gz = new MemoryStream();
            using (var z = new GZipStream(gz, CompressionLevel.Optimal, true))
            {
                tar.Position = 0;
                tar.CopyTo(z);
            }

            return gz.ToArray();
        }

        /// <summary>A pax extended header record list ("&lt;len&gt; key=value\n").</summary>
        public static Entry Pax(params (string Key, string Value)[] records)
        {
            var sb = new StringBuilder();
            foreach (var (k, v) in records)
            {
                string body = " " + k + "=" + v + "\n";
                int len = body.Length + 1;
                while ((len.ToString().Length + body.Length) != len)
                {
                    len = len.ToString().Length + body.Length;
                }

                sb.Append(len).Append(body);
            }

            return new Entry { Name = "PaxHeader", Type = 'x', Data = Encoding.UTF8.GetBytes(sb.ToString()) };
        }

        private static void Put(byte[] h, int at, int length, string text)
        {
            var b = Encoding.ASCII.GetBytes(text);
            Array.Copy(b, 0, h, at, Math.Min(b.Length, length));
        }

        private static void Octal(byte[] h, int at, int length, long v) => Put(h, at, length, Convert.ToString(v, 8).PadLeft(length - 1, '0'));
    }

    /// <summary>The Linux updater download: archive check, publish-updater --platform linux, apply-signed, site export.</summary>
    public class LinuxUpdaterTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

        private static string WriteArchive(TempDir t, string sub, string version, byte[] bytes = null, bool buildSum = true)
        {
            string path = t.Sub(sub, LinuxUpdaterArchive.FileName(version));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bytes = bytes ?? LinuxTar.Build(LinuxTar.Standard(version));
            File.WriteAllBytes(path, bytes);
            if (buildSum)
            {
                File.WriteAllText(path + ".sha256", Sha256SumFile.Format(Sha256Util.Of(bytes), Path.GetFileName(path)));
            }

            return path;
        }

        private static LinuxUpdaterPublishResult Publish(string archive, string updaterDir, System.Security.Cryptography.ECDsa key, string baseUrl = null, string version = null, bool force = false)
        {
            var req = new LinuxUpdaterPublishRequest { ArchivePath = archive, UpdaterDir = updaterDir, BaseUrl = baseUrl, Version = version, NowUtc = Now, Force = force };
            if (key != null)
            {
                req.Signers.Add(new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key))));
            }

            try
            {
                return LinuxUpdaterPublisher.Publish(req);
            }
            finally
            {
                foreach (var s in req.Signers)
                {
                    s.Dispose();
                }
            }
        }

        // ------------------------------------------------------------------------------------------- names, urls

        [Theory]
        [InlineData("lotr-updater-0.1.0-linux-x64.tar.gz", "0.1.0")]
        [InlineData("lotr-updater-1.2.3.4-linux-x64.tar.gz", null)] // x.y.z only: r2-sync and the WAF rule refuse the rest
        [InlineData("lotr-updater-0.2.0-beta.1-linux-x64.tar.gz", null)]
        [InlineData("lotr-updater-0.2.0-rc1-linux-x64.tar.gz", null)]
        [InlineData("lotr-updater-0.1.0-linux-arm64.tar.gz", null)]
        [InlineData("LotRMPUpdater-0.1.0.exe", null)]
        [InlineData("lotr-updater-0.1-linux-x64.tar.gz", null)]
        [InlineData("lotr-updater-0.1.0-linux-x64.tar.gz.sha256", null)]
        public void Version_comes_from_the_build_file_name(string name, string version) => Assert.Equal(version, LinuxUpdaterArchive.VersionOfFileName(name));

        [Theory]
        [InlineData("/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", true)]
        [InlineData("https://middleearthevent.com/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", true)]
        [InlineData("https://localhost:8443/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", true)]
        [InlineData("http://middleearthevent.com/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", false)]
        [InlineData("https://evil.example/x/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", false)]
        [InlineData("https://user@evil.example/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", false)]
        [InlineData("https:///files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", false)]
        [InlineData("/files/updater/lotr-updater-0.1.0-linux-x64.tar.gz", false)]
        [InlineData("/files/updater/linux/lotr-updater-0.1.1-linux-x64.tar.gz", false)]
        [InlineData("/files/updater/LotRMPUpdater-0.1.0.exe", false)]
        [InlineData("/files/updater/linux/../LotRMPUpdater-0.1.0.exe", false)]
        public void Only_this_servers_archive_of_that_version_is_a_valid_url(string url, bool ok) => Assert.Equal(ok, LinuxUpdaterArchive.IsOwnUrl(url, "0.1.0"));

        // ------------------------------------------------------------------------------------------- the archive

        [Fact]
        public void The_build_scripts_layout_passes_inspection()
        {
            var info = LinuxUpdaterArchive.Inspect(LinuxTar.Build(LinuxTar.Standard("0.1.0", LinuxTar.Elf(70_000))), "0.1.0");
            Assert.Equal(new[] { "lotr-updater-0.1.0/", "lotr-updater-0.1.0/lotr-updater", "lotr-updater-0.1.0/README.txt" }, info.Entries);
            Assert.Equal(70_000, info.ProgramSize);
            Assert.Equal(493, info.ProgramMode);

            // "./" prefixes (GNU tar run inside the folder) and a pax header naming the program are fine too.
            var dotted = LinuxTar.Standard("0.1.0");
            dotted.ForEach(e => e.Name = "./" + e.Name);
            Assert.Single(LinuxUpdaterArchive.Inspect(LinuxTar.Build(dotted), "0.1.0").Entries, e => e.EndsWith("/lotr-updater", StringComparison.Ordinal));
            var pax = LinuxTar.Standard("0.1.0");
            pax.Insert(1, LinuxTar.Pax(("path", "lotr-updater-0.1.0/lotr-updater"), ("mtime", "1763100000.5")));
            pax[2].Name = "lotr-updater-0.1.0/@LongLink-placeholder";
            Assert.Contains("lotr-updater-0.1.0/lotr-updater", LinuxUpdaterArchive.Inspect(LinuxTar.Build(pax), "0.1.0").Entries);
        }

        public static IEnumerable<object[]> BadArchives()
        {
            List<LinuxTar.Entry> S(Action<List<LinuxTar.Entry>> change)
            {
                var l = LinuxTar.Standard("0.1.0");
                change(l);
                return l;
            }

            yield return new object[] { "windows exe", new byte[] { (byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0, 0xFF, 0xFF, 0, 0, 0xB8, 0, 0, 0 }, "gzip" };
            yield return new object[] { "wrong version folder", LinuxTar.Build(LinuxTar.Standard("0.0.9")), "outside the folder" };
            yield return new object[] { "no program", LinuxTar.Build(S(l => l.RemoveAt(1))), "has no lotr-updater-0.1.0/lotr-updater" };
            yield return new object[] { "not executable", LinuxTar.Build(S(l => l[1].Mode = 420)), "not executable" };
            yield return new object[] { "not ELF (a script)", LinuxTar.Build(S(l => l[1].Data = Encoding.ASCII.GetBytes("#!/bin/sh\necho hi\n" + new string(' ', 100)))), "no ELF header" };
            yield return new object[] { "32-bit ELF", LinuxTar.Build(S(l => l[1].Data = LinuxTar.Elf(elfClass: 1))), "x86-64" };
            yield return new object[] { "arm64 ELF", LinuxTar.Build(S(l => l[1].Data = LinuxTar.Elf(machine: 0xB7))), "x86-64" };
            yield return new object[] { "symlink", LinuxTar.Build(S(l => l.Add(new LinuxTar.Entry { Name = "lotr-updater-0.1.0/evil", Type = '2' }))), "tar type '2'" };
            yield return new object[] { "hard link", LinuxTar.Build(S(l => l.Add(new LinuxTar.Entry { Name = "lotr-updater-0.1.0/evil", Type = '1' }))), "tar type '1'" };
            yield return new object[] { "GNU long name", LinuxTar.Build(S(l => l.Insert(1, new LinuxTar.Entry { Name = "././@LongLink", Type = 'L', Data = Encoding.ASCII.GetBytes("x\0") }))), "tar type 'L'" };
            yield return new object[] { "dot-dot path", LinuxTar.Build(S(l => l.Add(new LinuxTar.Entry { Name = "lotr-updater-0.1.0/../../.bashrc", Data = new byte[3] }))), "unsafe path" };
            yield return new object[] { "absolute path", LinuxTar.Build(S(l => l.Add(new LinuxTar.Entry { Name = "/home/deck/.bashrc", Data = new byte[3] }))), "unsafe path" };
            yield return new object[] { "pax path escaping", LinuxTar.Build(S(l => l.InsertRange(2, new[] { LinuxTar.Pax(("path", "../../.profile")), new LinuxTar.Entry { Name = "lotr-updater-0.1.0/ok", Data = new byte[3] } }))), "unsafe path" };
            yield return new object[] { "pax link", LinuxTar.Build(S(l => l.InsertRange(2, new[] { LinuxTar.Pax(("linkpath", "/etc/passwd")), new LinuxTar.Entry { Name = "lotr-updater-0.1.0/ok", Data = new byte[3] } }))), "link" };
            yield return new object[] { "program twice", LinuxTar.Build(S(l => l.Add(l[1]))), "twice" };
            yield return new object[] { "bad header checksum", LinuxTar.Build(S(l => l[2].BadSum = true)), "checksum" };
            yield return new object[] { "no end-of-archive marker", LinuxTar.Build(LinuxTar.Standard("0.1.0"), endMarker: false), "truncated" };
            yield return new object[] { "truncated gzip", LinuxTar.Build(LinuxTar.Standard("0.1.0")).Take(200).ToArray(), null };
        }

        [Theory]
        [MemberData(nameof(BadArchives))]
        public void Anything_but_the_linux_x64_updater_is_refused(string what, byte[] archive, string reason)
        {
            var e = Assert.ThrowsAny<InvalidDataException>(() => LinuxUpdaterArchive.Inspect(archive, "0.1.0"));
            if (reason != null)
            {
                Assert.True(e.Message.Contains(reason), what + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------------------------------- publish (team PC)

        [Fact]
        public void Publishes_archive_sha256_and_signed_latest_json_in_the_linux_folder_only()
        {
            using (var t = new TempDir("lotr-linux-"))
            using (var key = EcKeys.Generate())
            {
                var bytes = LinuxTar.Build(LinuxTar.Standard("0.1.0", LinuxTar.Elf(50_000)));
                string archive = WriteArchive(t, "dist", "0.1.0", bytes);
                string updater = t.Sub("signed", "updater");
                var r = Publish(archive, updater, key);

                string linux = Path.Combine(updater, "linux");
                Assert.Equal(Path.Combine(linux, "lotr-updater-0.1.0-linux-x64.tar.gz"), r.ArchiveFile);
                Assert.Equal(bytes, File.ReadAllBytes(r.ArchiveFile));
                Assert.Equal("lotr-updater-0.1.0-linux-x64.tar.gz.sha256", Path.GetFileName(r.Sha256File));
                Assert.Equal(Sha256Util.Of(bytes) + "  lotr-updater-0.1.0-linux-x64.tar.gz\n", File.ReadAllText(r.Sha256File));

                byte[] latestBytes = File.ReadAllBytes(Path.Combine(linux, "latest.json"));
                var latest = UpdaterLatest.Parse(latestBytes);
                Assert.True(latest.Validate().IsValid);
                Assert.Equal("0.1.0", latest.Version);
                Assert.Equal("/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", latest.Url);
                Assert.Equal(Sha256Util.Of(bytes), latest.Sha256);
                Assert.Equal(bytes.LongLength, latest.Size);
                Assert.Equal("2026-11-14T09:00:00Z", latest.ReleasedAt);
                Assert.False(latest.Signed);
                Assert.Equal(new[] { "releasedAt", "sha256", "signed", "size", "url", "version" }, ((JsonObject)JsonParser.Parse(latestBytes)).Keys.ToArray()); // canonical, like updater/latest.json
                var check = ManifestSignature.Verify(latestBytes, File.ReadAllBytes(r.SignatureFile), new[] { new TrustedKey(EcKeys.ExportSpki(key)) });
                Assert.True(check.Valid, check.Message);

                // Nothing next to the Windows files, no temporary file left.
                Assert.Empty(Directory.GetFiles(updater));
                Assert.Equal(4, Directory.GetFiles(linux).Length);
                Assert.Empty(Directory.GetFiles(linux, "*.tmp"));

                // Absolute url with --base-url (same rule as the Windows exe).
                var abs = Publish(archive, t.Sub("abs", "updater"), key, "https://lotr.example/");
                Assert.Equal("https://lotr.example/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", abs.Latest.Url);
            }
        }

        [Fact]
        public void Publishing_linux_leaves_the_windows_updater_untouched()
        {
            using (var t = new TempDir("lotr-linux-"))
            using (var key = EcKeys.Generate())
            {
                string updater = t.Sub("files", "updater");
                string exe = t.Sub("build", "LotRMPUpdater.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(exe));
                File.WriteAllBytes(exe, TestData.SplitMix(9, 50_000));
                var win = new UpdaterPublishRequest { ExePath = exe, UpdaterDir = updater, Version = "0.3.3", NowUtc = Now };
                win.Signers.Add(new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key))));
                UpdaterPublisher.Publish(win);
                var before = Directory.GetFiles(updater).ToDictionary(f => f, File.ReadAllBytes);

                Publish(WriteArchive(t, "dist", "0.1.0"), updater, key);

                var after = Directory.GetFiles(updater).ToDictionary(f => f, File.ReadAllBytes);
                Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
                foreach (var kv in before)
                {
                    Assert.Equal(kv.Value, after[kv.Key]);
                }

                Assert.Equal("0.3.3", UpdaterLatest.Parse(File.ReadAllBytes(Path.Combine(updater, "latest.json"))).Version);
                Assert.Equal("0.1.0", UpdaterLatest.Parse(File.ReadAllBytes(Path.Combine(updater, "linux", "latest.json"))).Version);
            }
        }

        [Fact]
        public void Same_bytes_again_is_idempotent_other_bytes_need_a_new_version_and_an_unsigned_publish_drops_the_old_signature()
        {
            using (var t = new TempDir("lotr-linux-"))
            using (var key = EcKeys.Generate())
            {
                string updater = t.Sub("updater");
                string a = WriteArchive(t, "a", "0.1.0", LinuxTar.Build(LinuxTar.Standard("0.1.0", LinuxTar.Elf(seed: 1))));
                Assert.False(Publish(a, updater, key).AlreadyPublished);
                Assert.True(Publish(a, updater, key).AlreadyPublished);

                string b = WriteArchive(t, "b", "0.1.0", LinuxTar.Build(LinuxTar.Standard("0.1.0", LinuxTar.Elf(seed: 2))));
                Assert.Throws<InvalidOperationException>(() => Publish(b, updater, key));
                Assert.False(Publish(b, updater, key, force: true).AlreadyPublished);
                Assert.Equal(Sha256Util.OfFile(b), UpdaterLatest.Parse(File.ReadAllBytes(Path.Combine(updater, "linux", "latest.json"))).Sha256);

                Assert.True(File.Exists(Path.Combine(updater, "linux", "latest.json.sig")));
                Assert.Null(Publish(b, updater, null).SignatureFile);
                Assert.False(File.Exists(Path.Combine(updater, "linux", "latest.json.sig")));
            }
        }

        [Fact]
        public void Publish_refuses_wrong_names_versions_urls_damaged_copies_and_bad_archives()
        {
            using (var t = new TempDir("lotr-linux-"))
            {
                string updater = t.Sub("updater");
                string good = WriteArchive(t, "dist", "0.1.0");

                // --version must agree with the file name; an unrecognised name needs --version.
                Assert.Throws<ArgumentException>(() => Publish(good, updater, null, version: "0.2.0"));
                string renamed = t.Sub("dist", "linux.tgz");
                File.Copy(good, renamed);
                Assert.Throws<ArgumentException>(() => Publish(renamed, updater, null));
                Assert.Equal("0.1.0", Publish(renamed, t.Sub("renamed"), null, version: "0.1.0").Latest.Version);
                Assert.Throws<ArgumentException>(() => Publish(renamed, updater, null, version: "latest"));

                // Base url: https host only.
                Assert.Throws<ArgumentException>(() => Publish(good, updater, null, "http://lotr.example"));
                Assert.Throws<ArgumentException>(() => Publish(good, updater, null, "https://lotr.example/sub"));

                // The build's .sha256 does not match the archive (damaged copy).
                string damaged = WriteArchive(t, "damaged", "0.1.0");
                File.WriteAllText(damaged + ".sha256", Sha256SumFile.Format(new string('0', 64), Path.GetFileName(damaged)));
                Assert.Contains("damaged", Assert.Throws<InvalidDataException>(() => Publish(damaged, updater, null)).Message);

                // The Windows exe renamed as the Linux archive, or an archive of another version.
                string exe = WriteArchive(t, "exe", "0.1.0", TestData.SplitMix(5, 30_000));
                Assert.Throws<InvalidDataException>(() => Publish(exe, updater, null));
                string other = WriteArchive(t, "other", "0.1.0", LinuxTar.Build(LinuxTar.Standard("0.0.9")));
                Assert.Throws<InvalidDataException>(() => Publish(other, updater, null));

                Assert.False(Directory.Exists(Path.Combine(updater, "linux")) && Directory.GetFiles(Path.Combine(updater, "linux")).Any());
            }
        }

        // ------------------------------------------------------------------------------------------- apply-signed (server)

        [Fact]
        public void Linux_updater_signed_on_the_pc_is_installed_once_never_downgraded_and_only_when_everything_checks()
        {
            using (var t = new TempDir("lotr-linux-apply-"))
            using (var key = EcKeys.Generate())
            using (var other = EcKeys.Generate())
            {
                var trusted = new List<TrustedKey> { new TrustedKey(EcKeys.ExportSpki(key)) };
                string live = t.Sub("server", "files", "updater", "linux");
                string Signed(string sub, string version, System.Security.Cryptography.ECDsa k, int seed = 1)
                {
                    string updater = t.Sub(sub, "updater");
                    Publish(WriteArchive(t, sub + "-dist", version, LinuxTar.Build(LinuxTar.Standard(version, LinuxTar.Elf(seed: seed)))), updater, k, "https://example.invalid");
                    return UpdaterInstaller.LinuxLatestFile(updater);
                }

                string l1 = Signed("pc1", "0.1.0", key);
                Assert.Equal(t.Sub("pc1", "updater", "linux", "latest.json"), l1);
                var r = UpdaterInstaller.ApplyLinux(l1, live, trusted);
                Assert.False(r.AlreadyLive);
                Assert.Equal("0.1.0", r.Version);
                Assert.Equal(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(l1), "lotr-updater-0.1.0-linux-x64.tar.gz")), File.ReadAllBytes(Path.Combine(live, "lotr-updater-0.1.0-linux-x64.tar.gz")));
                Assert.Equal(File.ReadAllBytes(l1), File.ReadAllBytes(Path.Combine(live, "latest.json")));
                Assert.True(File.Exists(Path.Combine(live, "latest.json.sig")));
                Assert.Equal(Sha256Util.OfFile(Path.Combine(live, "lotr-updater-0.1.0-linux-x64.tar.gz")), Sha256SumFile.Parse(File.ReadAllText(Path.Combine(live, "lotr-updater-0.1.0-linux-x64.tar.gz.sha256"))).Sha256);
                Assert.True(UpdaterInstaller.ApplyLinux(l1, live, trusted).AlreadyLive);

                // Refused, nothing written: older, other key, unsigned, archive swapped after signing, not a Linux updater.
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.ApplyLinux(Signed("old", "0.0.9", key), live, trusted));
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.ApplyLinux(Signed("other", "0.1.1", other), live, trusted));
                Assert.Throws<InvalidOperationException>(() => UpdaterInstaller.ApplyLinux(Signed("unsigned", "0.1.2", null), live, trusted));
                string swapped = Signed("swapped", "0.1.3", key);
                File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(swapped), "lotr-updater-0.1.3-linux-x64.tar.gz"), LinuxTar.Build(LinuxTar.Standard("0.1.3", LinuxTar.Elf(seed: 77))));
                Assert.Throws<InvalidDataException>(() => UpdaterInstaller.ApplyLinux(swapped, live, trusted));
                Assert.Throws<ArgumentException>(() => UpdaterInstaller.ApplyLinux(t.Sub("pc1", "updater", "linux", "latest-beta.json"), live, trusted));

                // A latest.json signed by the team key but pointing elsewhere, or at a file that is not the updater.
                string crafted = Crafted(t, "crafted", key, "0.1.4", "/files/updater/LotRMPUpdater-0.1.4.exe", TestData.SplitMix(4, 10_000));
                Assert.Contains("is not this server's /files/updater/linux/", Assert.Throws<InvalidDataException>(() => UpdaterInstaller.ApplyLinux(crafted, live, trusted)).Message);
                string notElf = Crafted(t, "notelf", key, "0.1.5", "/files/updater/linux/lotr-updater-0.1.5-linux-x64.tar.gz", TestData.SplitMix(5, 10_000));
                Assert.Contains("gzip", Assert.Throws<InvalidDataException>(() => UpdaterInstaller.ApplyLinux(notElf, live, trusted)).Message);

                Assert.Equal(new[] { "latest.json", "latest.json.sig", "lotr-updater-0.1.0-linux-x64.tar.gz", "lotr-updater-0.1.0-linux-x64.tar.gz.sha256" }, Directory.GetFiles(live).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
                Assert.Contains("\"0.1.0\"", File.ReadAllText(Path.Combine(live, "latest.json")));

                // Newer and correct: installed, the old archive stays (a link someone shared keeps working).
                Assert.Equal("0.1.0", UpdaterInstaller.ApplyLinux(Signed("pc2", "0.2.0", key, 20), live, trusted).PreviousVersion);
                Assert.Contains("\"0.2.0\"", File.ReadAllText(Path.Combine(live, "latest.json")));
                Assert.True(File.Exists(Path.Combine(live, "lotr-updater-0.1.0-linux-x64.tar.gz")));

                // The Windows installer does not take the Linux latest.json (its url is not an exe).
                Assert.Throws<InvalidDataException>(() => UpdaterInstaller.Apply(l1, t.Sub("server", "files", "updater"), trusted));
                Assert.Null(UpdaterInstaller.LinuxLatestFile(t.Sub("nowhere")));
            }
        }

        /// <summary>A linux/latest.json signed with <paramref name="key"/> by hand (what a compromised build PC could write).</summary>
        private static string Crafted(TempDir t, string sub, System.Security.Cryptography.ECDsa key, string version, string url, byte[] payload)
        {
            string dir = t.Sub(sub, "updater", "linux");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, LinuxUpdaterArchive.FileName(version)), payload);
            var latest = new UpdaterLatest { Version = version, Url = url, Sha256 = Sha256Util.Of(payload), Size = payload.Length, ReleasedAt = "2026-11-14T09:00:00Z" };
            byte[] bytes = latest.ToJson().ToCanonicalBytes();
            File.WriteAllBytes(Path.Combine(dir, "latest.json"), bytes);
            using (var s = new ReleaseSigner(EcKeys.ImportPkcs8(EcKeys.ExportPkcs8(key))))
            {
                File.WriteAllBytes(Path.Combine(dir, "latest.json.sig"), ManifestSignature.Create(bytes, new[] { s }));
            }

            return Path.Combine(dir, "latest.json");
        }

        // ------------------------------------------------------------------------------------------- export-site-json

        [Fact]
        public void Site_export_writes_updaterLinux_only_for_a_signed_pointer_to_this_servers_archive()
        {
            using (var t = new TempDir("lotr-linux-site-"))
            using (var key = EcKeys.Generate())
            using (var other = EcKeys.Generate())
            {
                var trusted = new[] { new TrustedKey(EcKeys.ExportSpki(key)) };
                string releases = t.Sub("files", "releases");
                Directory.CreateDirectory(releases);
                string updater = t.Sub("files", "updater");
                string winLatest = Path.Combine(updater, "latest.json");
                string linuxLatest = Path.Combine(updater, "linux", "latest.json");

                // Nothing published: both null, and the key is there (the site checks for it).
                var w = new List<string>();
                var none = SiteExporter.Build(releases, "stable", trusted, winLatest, w);
                Assert.True(none["updaterLinux"].IsNull);
                Assert.Equal(new[] { "current", "updater", "updaterLinux", "history" }, none.Keys.ToArray());
                Assert.Empty(w);

                // Published and signed: the same fields as latest.json; the Windows "updater" stays as it was.
                var r = Publish(WriteArchive(t, "dist", "0.1.0"), updater, key, "https://lotr.example");
                var ok = SiteExporter.Build(releases, "stable", trusted, winLatest, w);
                Assert.Empty(w);
                Assert.True(ok["updater"].IsNull);
                var lu = ok["updaterLinux"].AsObject();
                Assert.Equal(r.Latest.ToJson().ToCanonicalBytes(), lu.ToCanonicalBytes());
                Assert.Equal("https://lotr.example/files/updater/linux/lotr-updater-0.1.0-linux-x64.tar.gz", lu["url"].AsString());
                Assert.False(lu["signed"].AsBool());
                Assert.Contains("\"updaterLinux\": {", Encoding.UTF8.GetString(JsonWriter.ToPrettyBytes(ok)));

                // Explicit path (--updater-linux-latest) works without a Windows latest.json path.
                Assert.Equal("0.1.0", SiteExporter.Build(releases, "stable", trusted, null, w, linuxLatest)["updaterLinux"].AsObject()["version"].AsString());
                Assert.True(SiteExporter.Build(releases, "stable", trusted, null, w)["updaterLinux"].IsNull);

                // No trusted key: shown, with a warning that it was not verified.
                Assert.False(SiteExporter.Build(releases, "stable", null, winLatest, w)["updaterLinux"].IsNull);
                Assert.Contains(w, x => x.Contains("no trusted public key"));
                w.Clear();

                // Signed with another key, no signature, a foreign url, a broken file: null + a warning each.
                Assert.True(SiteExporter.Build(releases, "stable", new[] { new TrustedKey(EcKeys.ExportSpki(other)) }, winLatest, w)["updaterLinux"].IsNull);
                Assert.Single(w);
                File.Delete(linuxLatest + ".sig");
                Assert.True(SiteExporter.Build(releases, "stable", trusted, winLatest, w)["updaterLinux"].IsNull);
                Assert.Contains("no latest.json.sig", w[1]);
                Crafted(t, Path.Combine("files"), key, "0.1.1", "https://evil.example/files/updater/linux/x.tar.gz", new byte[10]);
                Assert.True(SiteExporter.Build(releases, "stable", trusted, winLatest, w)["updaterLinux"].IsNull);
                Assert.Contains("is not this server's", w[2]);
                File.WriteAllText(linuxLatest, "{ not json");
                Assert.True(SiteExporter.Build(releases, "stable", trusted, winLatest, w)["updaterLinux"].IsNull);
                Assert.Contains("unreadable", w[3]);
            }
        }
    }
}
