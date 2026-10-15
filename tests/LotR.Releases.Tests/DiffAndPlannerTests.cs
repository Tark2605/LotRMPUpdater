using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LotR.Releases.Chunking;
using LotR.Releases.Hashing;
using LotR.Releases.Install;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using Xunit;

namespace LotR.Releases.Tests
{
    public class DiffTests
    {
        private static ManifestFile F(string path, char sha, long size, long objectSize) =>
            new ManifestFile { Path = path, Size = size, Sha256 = TestData.Sha(sha), Object = TestData.Sha(sha) + ".zst", ObjectSize = objectSize };

        private static ReleaseManifest M(string id, params ManifestFile[] files) => new ReleaseManifest
        {
            ReleaseId = id,
            Modules = { new ManifestModule { Id = "E", Version = "v1", Folder = "E", Role = "event", Files = files.ToList() } },
        };

        private static Dictionary<string, ObjectRef> Objects(ReleaseManifest m, params (char Sha, long Size)[] chunks)
        {
            var d = m.AllFiles.GroupBy(f => f.Sha256).Select(g => g.First()).ToDictionary(f => f.Sha256, f => new ObjectRef { Sha256 = f.Sha256, ObjectSize = f.ObjectSize, Ext = "zst" });
            foreach (var c in chunks)
            {
                d[TestData.Sha(c.Sha)] = new ObjectRef { Sha256 = TestData.Sha(c.Sha), ObjectSize = c.Size, Ext = "zst", Kind = "chunk" };
            }

            return d;
        }

        [Fact]
        public void Added_changed_removed_and_bytes()
        {
            var a = M("20261001-000000-00000000", F("SubModule.xml", 'a', 100, 50), F("x.xml", 'b', 1000, 200), F("gone.png", 'c', 500, 500), F("same.wav", 'd', 9000, 8000));
            var b = M("20261002-000000-00000000", F("SubModule.xml", 'a', 100, 50), F("x.xml", 'e', 1100, 210), F("new.png", 'f', 70, 70), F("same.wav", 'd', 9000, 8000), F("copy.wav", 'd', 9000, 8000));
            var d = ReleaseDiff.Compute(a, Objects(a), b, Objects(b));
            var mod = Assert.Single(d.Modules);
            Assert.Equal(new[] { "new.png", "copy.wav" }, mod.Added.Select(x => x.Path));
            Assert.Equal(new[] { "x.xml" }, mod.Changed.Select(x => x.Path));
            Assert.Equal(new[] { "gone.png" }, mod.Removed.Select(x => x.Path));
            Assert.Equal(2, mod.Unchanged);
            Assert.Equal(210 + 70, d.UpdateDownloadBytes); // copy.wav has the same content: nothing to fetch
            Assert.Equal(2, d.NewObjects);
            Assert.Equal(50 + 210 + 70 + 8000, d.FreshInstallBytes);
        }

        [Fact]
        public void Chunked_files_count_only_missing_chunks()
        {
            var a = M("20261001-000000-00000000", F("SubModule.xml", 'a', 1, 1));
            var b = M("20261002-000000-00000000", F("SubModule.xml", 'a', 1, 1));
            var prev = Objects(a, ('1', 300), ('2', 300), ('3', 300), ('4', 50));
            var next = Objects(b, ('1', 300), ('2', 300), ('5', 310), ('6', 60));
            var d = ReleaseDiff.Compute(a, prev, b, next);
            Assert.Equal(310 + 60, d.UpdateDownloadBytes);
            Assert.Equal(2, d.NewObjects);
        }

        [Fact]
        public void First_release_and_removed_module()
        {
            var a = M("20261001-000000-00000000", F("SubModule.xml", 'a', 10, 5));
            var first = ReleaseDiff.Compute(null, null, a, Objects(a));
            Assert.Equal("added", first.Modules[0].Status);
            Assert.Equal(5, first.UpdateDownloadBytes);

            var b = M("20261002-000000-00000000", F("SubModule.xml", 'a', 10, 5));
            b.Modules[0].Id = "Other";
            var d = ReleaseDiff.Compute(a, Objects(a), b, Objects(b));
            Assert.Contains(d.Modules, m => m.Id == "E" && m.Status == "removed");
        }
    }

    public class PlannerTests
    {
        [Fact]
        public void Keep_copy_download_delete_and_rebuild()
        {
            using (var t = new TempDir())
            {
                string mod = t.Sub("Modules", "E");
                Directory.CreateDirectory(mod);
                byte[] keep = TestData.SplitMix(1, 1000);
                byte[] moved = TestData.SplitMix(2, 2000);
                byte[] stale = TestData.SplitMix(3, 3000);
                File.WriteAllBytes(Path.Combine(mod, "SubModule.xml"), keep);
                File.WriteAllBytes(Path.Combine(mod, "old-name.bin"), moved);
                File.WriteAllBytes(Path.Combine(mod, "changed.xml"), stale);
                File.WriteAllBytes(Path.Combine(mod, "user-notes.txt"), new byte[] { 1 });

                // Big file: installed version, then a target with 100 bytes changed in the middle.
                byte[] bigOld = TestData.SplitMix(4, 5 * 1024 * 1024);
                byte[] bigNew = (byte[])bigOld.Clone();
                for (int i = 0; i < 100; i++)
                {
                    bigNew[2_500_000 + i] ^= 0x5A;
                }

                File.WriteAllBytes(Path.Combine(mod, "big.bin"), bigOld);
                var newIndex = Index(bigNew);

                byte[] fresh = TestData.SplitMix(5, 4000);
                ManifestFile Whole(string path, byte[] data) => new ManifestFile { Path = path, Size = data.Length, Sha256 = Sha256Util.Of(data), Object = Sha256Util.Of(data) + ".zst", ObjectSize = data.Length / 2 };
                var target = new ReleaseManifest
                {
                    Modules =
                    {
                        new ManifestModule
                        {
                            Id = "E", Folder = "E", Version = "v2", Role = "event",
                            Files =
                            {
                                Whole("SubModule.xml", keep),
                                Whole("new-name.bin", moved),
                                Whole("changed.xml", fresh),
                                new ManifestFile { Path = "big.bin", Size = bigNew.Length, Sha256 = newIndex.Sha256, Chunked = true, ChunkIndex = TestData.Sha('9') + ".zst", ObjectSize = newIndex.DistinctObjectBytes },
                            },
                        },
                    },
                };
                var installedManifest = new ReleaseManifest
                {
                    Modules = { new ManifestModule { Id = "E", Folder = "E", Files = { Whole("SubModule.xml", keep), Whole("old-name.bin", moved), Whole("changed.xml", stale), Whole("big.bin", bigOld) } } },
                };

                var check = InstalledModuleVerifier.Verify(mod, target.Modules[0]);
                Assert.Contains("user-notes.txt", check.Extra);
                Assert.Contains("old-name.bin", check.Extra);
                Assert.Equal(InstalledFileState.Missing, check.Files.Single(f => f.Path == "new-name.bin").State);
                Assert.Equal(InstalledFileState.SizeDiffers, check.Files.Single(f => f.Path == "changed.xml").State);
                Assert.Equal(InstalledFileState.HashDiffers, check.Files.Single(f => f.Path == "big.bin").State);

                var local = new LocalContent();
                local.AddInstalled(check);
                local.AddFile(Sha256Util.Of(moved), Path.Combine(mod, "old-name.bin"));
                var installed = new Dictionary<string, InstallCheck> { ["E"] = check };

                // Pass 1: the chunk index is not fetched yet.
                var p1 = DownloadPlanner.Compute(target, installed, installedManifest, _ => null, local);
                Assert.True(p1.NeedsIndexes);
                Assert.Contains(p1.Downloads, d => d.Kind == "index");

                // Pass 2: index available, old big file re-chunked locally.
                local.AddChunksByScanning(Path.Combine(mod, "big.bin"));
                var plan = DownloadPlanner.Compute(target, installed, installedManifest, _ => newIndex, local);
                Assert.Equal(FileAction.Keep, plan.Files.Single(f => f.Target.Path == "SubModule.xml").Action);
                Assert.Equal(FileAction.CopyLocal, plan.Files.Single(f => f.Target.Path == "new-name.bin").Action);
                Assert.Equal(FileAction.Download, plan.Files.Single(f => f.Target.Path == "changed.xml").Action);
                var big = plan.Files.Single(f => f.Target.Path == "big.bin");
                Assert.Equal(FileAction.Rebuild, big.Action);
                int missing = big.Chunks.Count(c => c.Local == null);
                Assert.InRange(missing, 1, 2);
                Assert.Equal(big.Chunks.Count - missing, big.Chunks.Count(c => c.Local != null));
                Assert.Equal(new[] { ("E", "old-name.bin") }, plan.Deletes.ToArray());
                Assert.Equal(1 + missing, plan.Downloads.Count);
                Assert.DoesNotContain(plan.Deletes, d => d.Path == "user-notes.txt");
            }
        }

        [Fact]
        public void Size_only_check_trusts_the_recorded_hash_only()
        {
            using (var t = new TempDir())
            {
                string mod = t.Sub("E");
                Directory.CreateDirectory(mod);
                File.WriteAllBytes(Path.Combine(mod, "a.xml"), new byte[] { 1, 2, 3 });
                var oldFile = new ManifestFile { Path = "a.xml", Size = 3, Sha256 = Sha256Util.Of(new byte[] { 1, 2, 3 }), Object = "x.raw" };
                var newFile = new ManifestFile { Path = "a.xml", Size = 3, Sha256 = Sha256Util.Of(new byte[] { 3, 2, 1 }), Object = Sha256Util.Of(new byte[] { 3, 2, 1 }) + ".raw", ObjectSize = 3 };
                var target = new ReleaseManifest { Modules = { new ManifestModule { Id = "E", Folder = "E", Files = { newFile } } } };
                var installedManifest = new ReleaseManifest { Modules = { new ManifestModule { Id = "E", Folder = "E", Files = { oldFile } } } };
                var check = InstalledModuleVerifier.Verify(mod, target.Modules[0], InstallCheckMode.SizeOnly);
                Assert.True(check.Ok); // same size: a size-only check cannot tell
                var plan = DownloadPlanner.Compute(target, new Dictionary<string, InstallCheck> { ["E"] = check }, installedManifest, null, new LocalContent());
                Assert.Equal(FileAction.Download, plan.Files[0].Action); // but the recorded hash differs
            }
        }

        [Fact]
        public void Manifest_paths_cannot_escape_the_module_folder()
        {
            Assert.Throws<InvalidDataException>(() => RelativePath.ToFileSystem(@"C:\Modules\E", "../F/x.dll"));
            Assert.Equal(@"C:\Modules\E\bin\x.dll", RelativePath.ToFileSystem(@"C:\Modules\E", "bin/x.dll"));
        }

        private static ChunkIndex Index(byte[] data)
        {
            var idx = new ChunkIndex { Size = data.Length, Sha256 = Sha256Util.Of(data) };
            FastCdc.Contract.Split(new MemoryStream(data), (s, o) => idx.Chunks.Add(new ChunkRef { Sha256 = Sha256Util.Of(s), Size = s.Count, ObjectSize = s.Count, Ext = "raw" }));
            return idx;
        }
    }
}
