using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Hashing;
using LotR.Releases.Install;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Xunit;
using Xunit.Abstractions;

namespace LotR.Releases.Tests
{
    /// <summary>
    /// Publishes the REAL event module (read-only copy of the team repo clone into a temp folder) twice: as is, then
    /// with a small simulated change. Checks that the second publish stores only the new objects, that verify passes,
    /// and that tampering is caught. Numbers go to the test output and to %TEMP%\lotr-e2e-numbers-&lt;tfm&gt;.txt.
    /// </summary>
    public class EndToEndPublishTests
    {
        private readonly ITestOutputHelper output;

        public EndToEndPublishTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void Publish_real_event_module_twice_verify_and_detect_tampering()
        {
            string source = TestData.RealEventModule;
            if (source == null)
            {
                output.WriteLine("team repo clone not found: skipped");
                return;
            }

            var log = new List<string>();
            void Note(string s)
            {
                log.Add(s);
                output.WriteLine(s);
            }

#if NET48
            string tfm = "net48";
#else
            string tfm = "net8.0";
#endif
            Note("runtime: " + tfm + ", " + Environment.ProcessorCount + " logical CPUs");
            using (var t = new TempDir("lotr-e2e-"))
            using (var signer = new ReleaseSigner(EcKeys.Generate()))
            {
                var sw = Stopwatch.StartNew();
                string work = t.Sub("work", "LotRMPEvent");
                string installedV1 = t.Sub("install", "LotRMPEvent");
                CopyTree(source, work);
                CopyTree(source, installedV1);
                Note("copied the module twice in " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                string releases = t.Sub("files", "releases");
                var trusted = new[] { new TrustedKey(signer.Spki) };

                PublishRequest Req(DateTime now) => new PublishRequest
                {
                    ReleasesRoot = releases,
                    Modules = { new ModuleSource { Folder = work, Role = ModuleRoles.Event, WorkshopItem = "3774022783" } },
                    Channel = "stable",
                    PublishedBy = "e2e-test",
                    BaseUrl = "https://example.invalid",
                    GameMinVersion = "v1.2.12",
                    Notes = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["en"] = "e2e" },
                    Signers = { signer },
                    NowUtc = now,
                };

                // ---------- release 1
                var r1 = ReleasePublisher.Publish(Req(new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc)));
                var m1 = r1.Plan.Manifest;
                long size1 = m1.TotalSize;
                int chunked1 = m1.AllFiles.Count(f => f.Chunked);
                Note(string.Format(CultureInfo.InvariantCulture,
                    "publish #1 {0}: {1} files, {2:0.0} MB raw ({3} chunked files >= 16 MiB), hashed+compressed in {4:0.0} s, total {5:0.0} s",
                    r1.ReleaseId, m1.AllFiles.Count(), size1 / 1048576.0, chunked1, r1.Plan.HashElapsed.TotalSeconds, r1.Elapsed.TotalSeconds));
                Note(string.Format(CultureInfo.InvariantCulture,
                    "  stored: {0} objects, {1:0.0} MB ({2:0.0} % of raw); zst {3} objects / raw {4} objects; fresh install download {5:0.0} MB",
                    r1.WrittenObjects, r1.WrittenBytes / 1048576.0, 100.0 * r1.WrittenBytes / size1,
                    Directory.EnumerateFiles(Path.Combine(r1.ReleaseDir, "objects"), "*.zst", SearchOption.AllDirectories).Count(),
                    Directory.EnumerateFiles(Path.Combine(r1.ReleaseDir, "objects"), "*.raw", SearchOption.AllDirectories).Count(),
                    r1.Plan.Diff.FreshInstallBytes / 1048576.0));
                Note(string.Format(CultureInfo.InvariantCulture, "  verify #1: {0} objects decoded+hashed in {1:0.00} s", r1.Verification.ObjectsChecked, r1.Verification.Elapsed.TotalSeconds));
                Assert.True(r1.Verification.Ok, string.Join("\n", r1.Verification.Errors));
                Assert.Equal(m1.AllFiles.Count(), Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Count());
                Assert.Equal("OrderMiddleEarthEvent", m1.Modules[0].Id);
                Assert.True(m1.Modules[0].Scenes.Count >= 7);
                Assert.True(chunked1 >= 1);
                Assert.Equal(r1.ReleaseId, ChannelIndex.Load(Path.Combine(releases, "channels.json")).Get("stable"));
                Assert.Equal(r1.Plan.Diff.FreshInstallBytes, r1.WrittenBytes);

                // Fast hash-only plan of the same folder: nothing to download.
                var sw2 = Stopwatch.StartNew();
                var same = ReleasePublisher.Plan(Req(DateTime.UtcNow), exact: false);
                Note(string.Format(CultureInfo.InvariantCulture, "plan (hash only, unchanged folder): {0:0.0} MB hashed in {1:0.00} s -> {2:0.0} MB/s, update download {3} bytes",
                    same.BytesHashed / 1048576.0, sw2.Elapsed.TotalSeconds, same.BytesHashed / 1048576.0 / sw2.Elapsed.TotalSeconds, same.Diff.UpdateDownloadBytes));
                Assert.Equal(0, same.Diff.UpdateDownloadBytes);
                Assert.Equal(0, same.Diff.NewObjects);

                // ---------- simulated small change
                string xml = Directory.EnumerateFiles(Path.Combine(work, "ModuleData"), "*.xml", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).First();
                File.AppendAllText(xml, "\n<!-- e2e change -->\n");
                File.WriteAllText(Path.Combine(work, "ModuleData", "e2e_added.txt"), "added by the e2e test\n");
                string removed = Directory.EnumerateFiles(Path.Combine(work, "GUI"), "*.png", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).First();
                File.Delete(removed);
                var bigEntry = m1.AllFiles.Where(f => f.Chunked).OrderByDescending(f => f.Size).First();
                string bigPath = Path.Combine(work, bigEntry.Path.Replace('/', Path.DirectorySeparatorChar));
                using (var fs = new FileStream(bigPath, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Position = fs.Length / 2;
                    int b = fs.ReadByte();
                    fs.Position = fs.Length / 2;
                    fs.WriteByte((byte)(b ^ 0xFF));
                }

                Note("change: appended to " + RelativePath.FromFileSystem(work, xml) + ", added ModuleData/e2e_added.txt, deleted " + RelativePath.FromFileSystem(work, removed) + ", flipped 1 byte in the middle of " + bigEntry.Path + " (" + (bigEntry.Size / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB, chunked)");

                var plan2 = ReleasePublisher.Plan(Req(DateTime.UtcNow), exact: true);
                var mod2 = plan2.Diff.Modules.Single();
                Assert.Equal(new[] { "ModuleData/e2e_added.txt" }, mod2.Added.Select(x => x.Path));
                Assert.Equal(new[] { bigEntry.Path, RelativePath.FromFileSystem(work, xml) }.OrderBy(p => p, StringComparer.Ordinal), mod2.Changed.Select(x => x.Path).OrderBy(p => p, StringComparer.Ordinal));
                Assert.Equal(new[] { RelativePath.FromFileSystem(work, removed) }, mod2.Removed.Select(x => x.Path));

                // ---------- release 2
                var r2 = ReleasePublisher.Publish(Req(new DateTime(2026, 10, 16, 14, 0, 0, DateTimeKind.Utc)));
                Note(string.Format(CultureInfo.InvariantCulture,
                    "publish #2 {0}: wrote {1} new objects ({2:N0} bytes), hard-linked {3} objects ({4:0.0} MB), copied {5}; update download for players on #1: {6:N0} bytes in {7} objects; total {8:0.0} s",
                    r2.ReleaseId, r2.WrittenObjects, r2.WrittenBytes, r2.LinkedObjects, r2.LinkedBytes / 1048576.0, r2.CopiedObjects, r2.Plan.Diff.UpdateDownloadBytes, r2.Plan.Diff.NewObjects, r2.Elapsed.TotalSeconds));
                Assert.True(r2.Verification.Ok, string.Join("\n", r2.Verification.Errors));
                Assert.Equal(plan2.Diff.UpdateDownloadBytes, r2.Plan.Diff.UpdateDownloadBytes); // exact plan == publish
                Assert.Equal(r2.Plan.Diff.NewObjects, r2.WrittenObjects);
                Assert.Equal(r2.Plan.Diff.UpdateDownloadBytes, r2.WrittenBytes);
                // xml + added txt + changed chunk(s) + new chunk index = 4..6 objects
                Assert.InRange(r2.WrittenObjects, 4, 6);
                Assert.True(r2.WrittenBytes < 3 * 1024 * 1024, "second publish wrote " + r2.WrittenBytes + " bytes");
                Assert.Equal(0, r2.CopiedObjects);
                Assert.Equal(r2.ReleaseId, ChannelIndex.Load(Path.Combine(releases, "channels.json")).Get("stable"));

                // ---------- verify passes, tampering is caught
                Assert.True(ReleaseVerifier.Verify(r1.ReleaseDir, trusted).Ok);
                var m2 = ReleaseFolder.Open(r2.ReleaseDir).Manifest;
                var addedEntry = m2.Modules[0].FindFile("ModuleData/e2e_added.txt");
                string addedObject = ReleaseFolder.Open(r2.ReleaseDir).ObjectPath(addedEntry.Object);
                byte[] original = File.ReadAllBytes(addedObject);
                var bad = (byte[])original.Clone();
                bad[bad.Length - 1] ^= 0x20;
                File.WriteAllBytes(addedObject, bad);
                var tamperedObject = ReleaseVerifier.Verify(r2.ReleaseDir, trusted);
                Assert.False(tamperedObject.Ok);
                Assert.Contains(tamperedObject.Errors, e => e.Contains("e2e_added.txt"));
                Note("tampered object detected: " + tamperedObject.Errors.First());
                File.WriteAllBytes(addedObject, original);

                string manifestPath = Path.Combine(r2.ReleaseDir, "manifest.json");
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                File.WriteAllBytes(manifestPath, Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifestBytes).Replace("\"publishedBy\":\"e2e-test\"", "\"publishedBy\":\"e2e-tesT\"")));
                var tamperedManifest = ReleaseVerifier.Verify(r2.ReleaseDir, trusted);
                Assert.False(tamperedManifest.Signature.Valid);
                Note("tampered manifest detected: " + tamperedManifest.Signature.Message);
                File.WriteAllBytes(manifestPath, manifestBytes);
                Assert.True(ReleaseVerifier.Verify(r2.ReleaseDir, trusted).Ok);

                // ---------- updater side: installed copy of #1 -> plan to #2
                var rel1 = ReleaseFolder.Open(r1.ReleaseDir);
                var rel2 = ReleaseFolder.Open(r2.ReleaseDir);
                var sw3 = Stopwatch.StartNew();
                var checkV1 = InstalledModuleVerifier.Verify(installedV1, rel1.Manifest.Modules[0], InstallCheckMode.Full, 8);
                Note(string.Format(CultureInfo.InvariantCulture, "verify install (full re-hash of {0} files, {1:0.0} MB): {2:0.00} s, ok={3}", checkV1.Files.Count, checkV1.BytesHashed / 1048576.0, sw3.Elapsed.TotalSeconds, checkV1.Ok));
                Assert.True(checkV1.Ok);
                Assert.Empty(checkV1.Extra);

                var checkAgainstV2 = InstalledModuleVerifier.Verify(installedV1, rel2.Manifest.Modules[0], InstallCheckMode.Full, 8);
                var local = new LocalContent();
                local.AddInstalled(checkAgainstV2);
                foreach (var f in rel1.Manifest.Modules[0].Files.Where(f => f.Chunked))
                {
                    local.AddChunks(Path.Combine(installedV1, f.Path.Replace('/', Path.DirectorySeparatorChar)), rel1.ReadChunkIndex(f.ChunkIndex));
                }

                var dl = DownloadPlanner.Compute(rel2.Manifest, new Dictionary<string, InstallCheck> { [rel2.Manifest.Modules[0].Id] = checkAgainstV2 }, rel1.Manifest, rel2.ReadChunkIndex, local);
                Note(string.Format(CultureInfo.InvariantCulture, "updater plan #1 -> #2: keep {0}, download {1} objects ({2:N0} bytes), rebuild {3} chunked file(s) reusing {4:0.0} MB locally, delete {5}",
                    dl.Files.Count(f => f.Action == FileAction.Keep), dl.Downloads.Count, dl.DownloadBytes, dl.Files.Count(f => f.Action == FileAction.Rebuild), dl.LocalReuseBytes / 1048576.0, dl.Deletes.Count));
                Assert.False(dl.NeedsIndexes);
                Assert.Single(dl.Deletes);
                Assert.Equal(1, dl.Files.Count(f => f.Action == FileAction.Rebuild));
                long indexBytes = r2.Plan.Diff.UpdateDownloadBytes - dl.DownloadBytes; // the planner was handed the index
                Assert.InRange(indexBytes, 1, 64 * 1024);

                // ---------- site data with a security sidecar
                File.WriteAllText(Path.Combine(r2.ReleaseDir, "security.json"),
                    "{\"schema\":1,\"releaseId\":\"" + r2.ReleaseId + "\",\"updatedAt\":\"2026-10-16T15:00:00Z\",\"virustotal\":[{\"sha256\":\"" + TestData.Sha('a') + "\",\"url\":\"u\",\"detections\":0,\"engines\":71,\"status\":\"done\"},{\"sha256\":\"" + TestData.Sha('b') + "\",\"status\":\"pending\"}],\"hybridAnalysis\":[{\"sha256\":\"" + TestData.Sha('c') + "\",\"verdict\":\"no specific threat\",\"status\":\"done\"}]}");
                var site = SiteExporter.Build(releases, "stable", trusted, null);
                var current = site["current"].AsObject();
                Assert.Equal(r2.ReleaseId, current["releaseId"].AsString());
                Assert.True(current["signature"].AsObject()["verified"].AsBool());
                Assert.Equal(EcKeys.FormatFingerprint(signer.KeyId), current["signature"].AsObject()["fingerprint"].AsString());
                Assert.Equal(1, current["security"].AsObject()["virustotal"].AsObject()["files"].AsLong());
                Assert.Equal(1, current["security"].AsObject()["virustotal"].AsObject()["pending"].AsLong());
                Assert.Equal(2, site["history"].AsArray().Count);
                Assert.True(site["updater"].IsNull);
                string sitePath = t.Sub("site", "src", "data", "releases.json");
                SiteExporter.Write(sitePath, site);
                Assert.Equal(site.ToCanonicalString(), JsonParser.Parse(File.ReadAllBytes(sitePath)).ToCanonicalString());

                // ---------- rollback by channel
                ReleasePublisher.SetChannel(releases, "stable", r1.ReleaseId);
                Assert.Equal(r1.ReleaseId, ChannelIndex.Load(Path.Combine(releases, "channels.json")).Get("stable"));
            }

            try
            {
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "lotr-e2e-numbers-" + tfm + ".txt"), log);
            }
            catch (IOException)
            {
            }
        }

        private static void CopyTree(string from, string to)
        {
            foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, dir.Substring(from.Length + 1)));
            }

            Directory.CreateDirectory(to);
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(to, file.Substring(from.Length + 1)));
            }
        }
    }
}
