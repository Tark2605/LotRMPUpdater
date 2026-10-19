using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Install;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>Steam Workshop copy of LotRMPCore reused as a read-only local source (0.1.1), duplicate warning, pre-download space.</summary>
    public class WorkshopTests
    {
        private static readonly byte[] Small = EngineTests.Random(600_000, 2);
        private static readonly byte[] Big1 = EngineTests.Random(17 << 20, 3); // >= 16 MiB: chunked by the publisher

        private static string WorkshopItem(Harness h, string item) =>
            Path.Combine(h.Root, "game", "steamapps", "workshop", "content", "261550", item);

        /// <summary>Core-only release (SubModule.xml, a small file, a big chunked file).</summary>
        private static ReleaseBuilder CoreRelease(Harness h, string label, byte[] big, string version = "v1.4.8.0", string workshopItem = null)
        {
            var r = h.NewRelease(label);
            var m = r.AddModule("LotRMPCore", "LotRMPCore", version, "LotRMPCore", "core");
            m.WorkshopItem = workshopItem;
            m.SubModule().File("AssetPackages/small.tpac", Small).File("AssetPackages/big.tpac", big);
            return r;
        }

        /// <summary>Writes a module as Steam lays out a Workshop item (files directly in the folder).</summary>
        private static void WriteCopy(ModuleBuilder m, string folder)
        {
            foreach (var f in m.Files)
            {
                var p = Path.Combine(folder, f.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, f.Content);
            }
        }

        /// <summary>Path, size, write time and content of every file: proves the Workshop folder is never changed.</summary>
        private static string Snapshot(string folder)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(f).Append('|').Append(new FileInfo(f).Length).Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks)
                  .Append('|').Append(ReleaseBuilder.Sha(File.ReadAllBytes(f))).Append('\n');
            return sb.ToString();
        }

        [Fact]
        public async Task Identical_workshop_copy_is_copied_and_nothing_is_downloaded()
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w1", Big1);
                r.WriteTo(h.Releases);
                var ws = WorkshopItem(h, "3772431545");
                WriteCopy(r.Modules[0], ws);
                var before = Snapshot(ws);

                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                var core = c.Plan.Modules.Single();
                Assert.Equal(3, core.Actions.Count);
                Assert.All(core.Actions, a => { Assert.Equal(FileAction.CopyLocal, a.Source); Assert.True(a.FromWorkshop); });
                Assert.Empty(c.Plan.Downloads);
                Assert.Equal(0, c.Plan.DownloadBytes);
                Assert.False(c.Plan.NeedsCoreSlot);
                long total = r.Modules[0].Files.Sum(f => (long)f.Content.Length);
                Assert.Equal(total, c.Plan.WorkshopReusedBytes);
                Assert.Equal(3, c.Plan.WorkshopCopiedFiles);
                Assert.True(c.Plan.StagingBytes >= total); // copied files need room in Modules, not in the cache
                Assert.DoesNotContain(c.Plan.Issues, i => i.Severity == IssueSeverity.Blocker);

                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                foreach (var f in r.Modules[0].Files) Assert.Equal(f.Content, File.ReadAllBytes(h.ModuleFile("LotRMPCore", f.Path)));
                Assert.Equal(0, h.Server.QueueSlotsGranted);
                Assert.Equal(before, Snapshot(ws)); // read-only on the Workshop folder
                Assert.True((await s.CheckAsync(null, CancellationToken.None)).UpToDate);
            }
        }

        [Fact]
        public async Task Older_workshop_copy_rebuilds_big_file_with_only_missing_chunks()
        {
            using (var h = new Harness())
            {
                var old = CoreRelease(h, "w-old", Big1, "v1.4.7.9");
                var ws = WorkshopItem(h, "3772431545");
                WriteCopy(old.Modules[0], ws);
                var before = Snapshot(ws);

                var big2 = (byte[])Big1.Clone();
                Array.Copy(EngineTests.Random(100_000, 77), 0, big2, 9 << 20, 100_000);
                var r = CoreRelease(h, "w2", big2);
                r.WriteTo(h.Releases);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                var acts = c.Plan.Modules.Single().Actions;
                var big = acts.Single(a => a.RelPath == "AssetPackages/big.tpac");
                Assert.Equal(FileAction.Rebuild, big.Source);
                Assert.True(big.FromWorkshop);
                Assert.Contains(big.Chunks, x => x.Local == null);
                Assert.Equal(FileAction.CopyLocal, acts.Single(a => a.RelPath == "AssetPackages/small.tpac").Source);
                Assert.Equal(FileAction.Download, acts.Single(a => a.RelPath == "SubModule.xml").Source); // version changed
                Assert.True(c.Plan.WorkshopReusedBytes > 15 << 20, "reused " + c.Plan.WorkshopReusedBytes);
                Assert.True(c.Plan.DownloadBytes < 2 << 20, "download " + c.Plan.DownloadBytes);

                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(big2, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
                Assert.Equal(before, Snapshot(ws));
            }
        }

        [Fact]
        public async Task Without_workshop_copy_everything_is_downloaded()
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w3", Big1);
                r.WriteTo(h.Releases);
                // A Workshop item of another module is ignored.
                var other = WorkshopItem(h, "999");
                Directory.CreateDirectory(other);
                File.WriteAllText(Path.Combine(other, "SubModule.xml"), "<Module><Id value=\"SomeOtherMod\"/></Module>");
                File.WriteAllBytes(Path.Combine(other, "small.tpac"), Small);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.DoesNotContain(c.Plan.Modules.Single().Actions, a => a.Source == FileAction.CopyLocal || a.FromWorkshop);
                Assert.Equal(0, c.Plan.WorkshopReusedBytes);
                Assert.True(c.Plan.DownloadBytes > 0);
                Assert.True(c.Plan.NeedsCoreSlot);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(Big1, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
            }
        }

        [Theory]
        [InlineData("3772431545", false, "3772431545")] // manifest workshopItem, module at the item root (real Steam layout)
        [InlineData("3772431545", true, "3772431545")]  // manifest workshopItem, module one level deeper
        [InlineData(null, false, "123")]                // no workshopItem: found by SubModule Id at the item root
        [InlineData(null, true, "123")]                 // no workshopItem: found by SubModule Id one level deeper
        [InlineData("3772431545", false, "456")]        // workshopItem not subscribed: falls back to the Id search
        public async Task Workshop_item_is_found_by_id_or_submodule(string manifestItem, bool deeper, string subscribedItem)
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w4", Big1, workshopItem: manifestItem);
                r.WriteTo(h.Releases);
                Assert.Equal(manifestItem, ReleaseFromServer(r).Modules.Single().WorkshopItem);
                var item = WorkshopItem(h, subscribedItem);
                WriteCopy(r.Modules[0], deeper ? Path.Combine(item, "LotRMPCore") : item);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.All(c.Plan.Modules.Single().Actions, a => Assert.True(a.FromWorkshop, a.RelPath));
                Assert.Empty(c.Plan.Downloads);
            }
        }

        private static LotR.Releases.Model.ReleaseManifest ReleaseFromServer(ReleaseBuilder r) =>
            LotR.Releases.Model.ManifestCodec.Parse(r.ManifestBytes);

        [Fact]
        public async Task Unreadable_workshop_file_is_skipped_and_downloaded()
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w5", Big1);
                r.WriteTo(h.Releases);
                var ws = WorkshopItem(h, "3772431545");
                WriteCopy(r.Modules[0], ws);
                h.Fs.Unreadable = p => p.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0 && p.EndsWith("small.tpac", StringComparison.OrdinalIgnoreCase);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                var acts = c.Plan.Modules.Single().Actions;
                Assert.Equal(FileAction.Download, acts.Single(a => a.RelPath == "AssetPackages/small.tpac").Source);
                Assert.Equal(FileAction.CopyLocal, acts.Single(a => a.RelPath == "AssetPackages/big.tpac").Source);
                Assert.Single(c.Plan.Downloads);
                Assert.Contains("Steam Workshop copy: skipped", s.Log.ReadRecent());
                h.Fs.Unreadable = null;
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(Small, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/small.tpac")));
            }
        }

        [Fact]
        public void Duplicate_scanner_finds_workshop_copies_at_root_and_one_level_deeper()
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w6", Small);
                r.WriteTo(h.Releases);
                var manifest = ReleaseFromServer(r);
                var root = WorkshopItem(h, "3772431545");
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "SubModule.xml"), "<Module><Id value=\"LotRMPCore\"/></Module>");
                var deep = Path.Combine(WorkshopItem(h, "42"), "LotRMPCore");
                Directory.CreateDirectory(deep);
                File.WriteAllText(Path.Combine(deep, "SubModule.xml"), "<Module><Id value=\"LotRMPCore\"/></Module>");
                var game = new GameTarget { Root = h.GameRoot, ModulesPath = h.Modules, Store = LotR.GameDetection.GameStore.Steam };
                var d = DuplicateModuleScanner.Scan(h.Fs, game, manifest);
                Assert.Equal(2, d.Count);
                Assert.All(d, x => Assert.True(x.IsWorkshop));
                Assert.Contains(d, x => x.Path == root && x.Where == "Steam Workshop item 3772431545");
                Assert.Contains(d, x => x.Path == deep && x.Where == "Steam Workshop item 42");
                Assert.StartsWith("LotRMPCore is also subscribed on the Steam Workshop", d[0].Advice);
                Assert.Contains("The game uses the copy installed by the updater", d[0].Advice);
                Assert.Contains("we recommend unsubscribing", d[0].Advice);
                // The button opens the item's page in Steam, built from the item number only.
                Assert.Contains(d, x => x.WorkshopItem == "3772431545" && x.WorkshopPageUrl == "steam://url/CommunityFilePage/3772431545");
                Assert.Contains(d, x => x.WorkshopItem == "42" && x.WorkshopPageUrl == "steam://url/CommunityFilePage/42");
                Assert.Null(new DuplicateModule { IsWorkshop = true, WorkshopItem = "12&calc" }.WorkshopPageUrl);
                Assert.Null(new DuplicateModule { IsWorkshop = false, WorkshopItem = "42" }.WorkshopPageUrl);
                // Other stores have no Workshop.
                game.Store = LotR.GameDetection.GameStore.Epic;
                Assert.Empty(DuplicateModuleScanner.Scan(h.Fs, game, manifest));
            }
        }

        [Fact]
        public async Task Workshop_duplicate_is_only_an_info_on_the_plan()
        {
            using (var h = new Harness())
            {
                var r = CoreRelease(h, "w7", Big1);
                r.WriteTo(h.Releases);
                WriteCopy(r.Modules[0], WorkshopItem(h, "3772431545"));
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                var issue = c.Plan.Issues.Single(i => i.Text.Contains("Steam Workshop"));
                Assert.Equal(IssueSeverity.Info, issue.Severity);
                Assert.DoesNotContain("wrong version", issue.Text);
            }
        }

        [Fact]
        public async Task Background_predownload_refuses_when_space_is_short()
        {
            using (var h = new Harness())
            {
                CoreRelease(h, "w8", Big1).WriteTo(h.Releases);
                var s = h.Service();
                s.State.PreDownloadMaxJitterMinutes = 0;
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.True(c.Plan.Downloads.Count > 0);
                h.Fs.FreeSpace = 1_000_000;
                long served = h.Server.BytesServed;
                Assert.False(await s.PreDownloadIfDueAsync(c, null, CancellationToken.None));
                Assert.False(s.State.PendingDownloaded);
                Assert.Equal(served, h.Server.BytesServed);
                Assert.Contains("Background pre-download skipped: Not enough free space", s.Log.ReadRecent());
            }
        }

        [Fact]
        public async Task Unreadable_free_space_is_logged_and_does_not_block()
        {
            using (var h = new Harness())
            {
                CoreRelease(h, "w9", Small).WriteTo(h.Releases);
                h.Fs.FreeSpaceError = new IOException("device not ready (simulated)");
                var s = h.Service();
                s.State.PreDownloadMaxJitterMinutes = 0;
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.DoesNotContain(c.Plan.Issues, i => i.Severity == IssueSeverity.Blocker);
                Assert.True(await s.PreDownloadIfDueAsync(c, null, CancellationToken.None));
                Assert.Contains("Could not read the free space", s.Log.ReadRecent());
            }
        }
    }
}
