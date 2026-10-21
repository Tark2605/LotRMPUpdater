using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Install;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    public class EngineTests
    {
        internal static byte[] Random(int size, int seed)
        {
            var b = new byte[size];
            new Random(seed).NextBytes(b);
            return b;
        }

        internal static byte[] Text(int size, string word)
        {
            var sb = new StringBuilder();
            while (sb.Length < size) sb.Append(word).Append(' ').Append(sb.Length).Append('\n');
            return Encoding.ASCII.GetBytes(sb.ToString(0, size));
        }

        private static readonly byte[] Dll1 = Random(50_000, 1);
        private static readonly byte[] Items = Text(200_000, "item");

        /// <summary>Event module (compressible XML + a "dll") and a Core module.</summary>
        private static ReleaseBuilder Release1(Harness h)
        {
            var r = h.NewRelease("r1");
            r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.2", "LotRMPEvent").SubModule()
                .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Dll1)
                .File("ModuleData/items.xml", Items)
                .File("ModuleData/old.xml", Text(5_000, "old"));
            r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                .File("AssetPackages/small.tpac", Random(600_000, 2));
            return r;
        }

        private static async Task<CheckResult> CheckAndInstall(UpdaterService s)
        {
            var c = await s.CheckAsync(null, CancellationToken.None);
            Assert.Null(c.Error);
            await s.InstallAsync(c.Plan, null, CancellationToken.None);
            return c;
        }

        [Fact]
        public async Task Fresh_install_end_to_end()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.WriteTo(h.Releases);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                Assert.True(c.Plan.HasChanges);
                Assert.All(c.Plan.Modules, m => Assert.True(m.IsNewFolder));
                Assert.Equal(6, c.Plan.Modules.SelectMany(m => m.Actions).Count(a => a.Kind == FileActionKind.Add));
                Assert.False(c.Plan.IsBlocked);
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent"))); // a check writes nothing in the game folder

                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                foreach (var m in r.Modules)
                    foreach (var f in m.Files)
                        Assert.Equal(f.Content, File.ReadAllBytes(h.ModuleFile(m.Folder, f.Path)));
                Assert.Equal(r.ReleaseId, s.State.InstalledReleaseId);
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent", ".lotr-staging")));
                Assert.Equal(1, h.Server.QueueSlotsGranted);
                Assert.Equal(1, h.Server.QueueSlotsReleased);
                Assert.True((await s.CheckAsync(null, CancellationToken.None)).UpToDate);
            }
        }

        [Fact]
        public async Task Update_downloads_only_changed_files_and_backs_up_old_ones()
        {
            using (var h = new Harness())
            {
                var r1 = Release1(h);
                r1.WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                File.WriteAllText(Path.Combine(h.Modules, "LotRMPEvent", "player-notes.txt"), "mine");

                var r2 = h.NewRelease("r2");
                var dll2 = Random(50_000, 99);
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.3", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", dll2)
                    .File("ModuleData/items.xml", Items)
                    .File("ModuleData/new.xml", Text(3_000, "new"));
                r2.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                    .File("AssetPackages/small.tpac", Random(600_000, 2));
                r2.WriteTo(h.Releases);

                var c = await s.CheckAsync(null, CancellationToken.None);
                var ev = c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPEvent");
                Assert.Equal(new[] { "ModuleData/new.xml" }, ev.Actions.Where(a => a.Kind == FileActionKind.Add).Select(a => a.RelPath));
                Assert.Equal(new[] { "SubModule.xml", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll" }, ev.Actions.Where(a => a.Kind == FileActionKind.Replace).Select(a => a.RelPath).OrderBy(x => x, StringComparer.Ordinal));
                Assert.Equal(new[] { "ModuleData/old.xml" }, ev.Actions.Where(a => a.Kind == FileActionKind.Remove).Select(a => a.RelPath));
                Assert.Equal(new[] { "player-notes.txt" }, ev.Extra); // reported, never deleted
                Assert.False(c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPCore").HasChanges);
                Assert.Equal(3, c.Plan.Downloads.Count);
                Assert.False(c.Plan.NeedsCoreSlot);

                int itemsHits = h.Server.Hits(r2.ObjectUrlPath(Items));
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(dll2, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll")));
                Assert.False(File.Exists(h.ModuleFile("LotRMPEvent", "ModuleData/old.xml")));
                Assert.True(File.Exists(h.ModuleFile("LotRMPEvent", "player-notes.txt")));
                Assert.Equal(itemsHits, h.Server.Hits(r2.ObjectUrlPath(Items)));
                var backup = Path.Combine(h.Home, "backup", r1.ReleaseId, "LotRMPEvent");
                Assert.True(File.Exists(Path.Combine(backup, "ModuleData", "old.xml")));
                Assert.Equal(Dll1, File.ReadAllBytes(Path.Combine(backup, "bin", "Win64_Shipping_Client", "OrderMiddleEarthEvent.dll")));
                Assert.Equal(r1.ReleaseId, s.State.PreviousReleaseId);
            }
        }

        [Fact]
        public async Task Chunked_file_update_reuses_local_chunks()
        {
            using (var h = new Harness())
            {
                var big1 = Random(17 << 20, 2);
                var r1 = h.NewRelease("c1");
                r1.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule().File("AssetPackages/big.tpac", big1);
                r1.WriteTo(h.Releases);
                var s = h.Service();
                var c1 = await CheckAndInstall(s);
                Assert.Equal(Lotr.Updater.Core.Planning.FileActionKind.Add, c1.Plan.Modules[0].Actions.Single(a => a.RelPath.EndsWith(".tpac")).Kind);
                Assert.Equal(big1, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));

                var big2 = (byte[])big1.Clone();
                Array.Copy(Random(100_000, 77), 0, big2, 9 << 20, 100_000);
                var r2 = h.NewRelease("c2");
                r2.AddModule("LotRMPCore", "LotRMPCore", "v1.4.8.0", "LotRMPCore", "core").SubModule().File("AssetPackages/big.tpac", big2);
                r2.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                var a = c.Plan.Modules.Single().Actions.Single(x => x.RelPath == "AssetPackages/big.tpac");
                Assert.Equal(FileAction.Rebuild, a.Source);
                Assert.True(c.Plan.ReusedLocalBytes > 15 << 20, "reused " + c.Plan.ReusedLocalBytes);
                Assert.True(c.Plan.DownloadBytes < 2 << 20, "download " + c.Plan.DownloadBytes);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(big2, File.ReadAllBytes(h.ModuleFile("LotRMPCore", "AssetPackages/big.tpac")));
            }
        }

        [Fact]
        public async Task Interrupted_download_resumes_with_range()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.WriteTo(h.Releases);
                var path = r.ObjectUrlPath(Dll1);
                h.Server.Faults = (p, hit) => p == path && hit == 1 ? new Fault { CutAfterBytes = 20_000 } : null;
                var s = h.Service();
                await CheckAndInstall(s);
                Assert.Equal(Dll1, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll")));
                // resumed from what had arrived before the cut
                Assert.Contains(h.Server.RangeRequests, x => x.StartsWith(path) && x.Contains("bytes=") && !x.Contains("bytes=0-"));
            }
        }

        [Fact]
        public async Task Server_ignoring_range_restarts_cleanly()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.WriteTo(h.Releases);
                var path = r.ObjectUrlPath(Dll1);
                h.Server.Faults = (p, hit) => p != path ? null : hit == 1 ? new Fault { CutAfterBytes = 10_000 } : new Fault { IgnoreRange = true };
                var s = h.Service();
                await CheckAndInstall(s);
                Assert.Equal(Dll1, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll")));
            }
        }

        [Fact]
        public async Task Corrupt_object_is_downloaded_again()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.WriteTo(h.Releases);
                var path = r.ObjectUrlPath(Items);
                h.Server.Faults = (p, hit) => p == path && hit == 1 ? new Fault { Corrupt = true } : null;
                var s = h.Service();
                await CheckAndInstall(s);
                Assert.Equal(Items, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "ModuleData/items.xml")));
                Assert.Equal(2, h.Server.Hits(path));
            }
        }

        [Fact]
        public async Task Object_corrupt_twice_stops_and_changes_nothing()
        {
            using (var h = new Harness())
            {
                var r1 = Release1(h);
                r1.WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var r2 = h.NewRelease("r2");
                var dll5 = Random(50_000, 5);
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.3", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", dll5);
                r2.WriteTo(h.Releases);
                var path = r2.ObjectUrlPath(dll5);
                h.Server.Faults = (p, hit) => p == path ? new Fault { Corrupt = true } : null;
                var c = await s.CheckAsync(null, CancellationToken.None);
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.Contains("damaged twice", ex.Message);
                Assert.Equal(Dll1, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll")));
                Assert.True(File.Exists(h.ModuleFile("LotRMPEvent", "ModuleData/old.xml")));
                Assert.Equal(r1.ReleaseId, s.State.InstalledReleaseId);
            }
        }

        [Fact]
        public async Task Retry_after_is_honoured_on_503_and_429()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.WriteTo(h.Releases);
                var path = r.ObjectUrlPath(Dll1);
                h.Server.Faults = (p, hit) =>
                    p.EndsWith("channels.json") && hit == 1 ? new Fault { Status = 503, RetryAfterSeconds = 7 } :
                    p == path && hit == 1 ? new Fault { Status = 429, RetryAfterSeconds = 3 } : null;
                var s = h.Service();
                await CheckAndInstall(s);
                Assert.Contains(TimeSpan.FromSeconds(7), h.Delays);
                Assert.Contains(TimeSpan.FromSeconds(3), h.Delays);
            }
        }

        [Fact]
        public async Task Core_download_waits_in_the_queue()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                h.Server.QueueBusyAnswers = 2;
                var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
                h.Server.OnRequest = requests.Add;
                var s = h.Service();
                await CheckAndInstall(s);
                Assert.Equal(1, h.Server.QueueSlotsGranted);
                Assert.Equal(1, h.Server.QueueSlotsReleased);
                Assert.Contains(TimeSpan.FromSeconds(1), h.Delays);
                Assert.Contains(requests, x => x.Contains("?slot=tok-3"));
            }
        }

        [Fact]
        public async Task Locked_file_rolls_back_and_names_the_process()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var r2 = h.NewRelease("r2");
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.3", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Random(50_000, 6))
                    .File("ModuleData/items.xml", Text(200_000, "item2"));
                r2.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                var dll = h.ModuleFile("LotRMPEvent", "bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll");
                InstallException ex;
                using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))
                    ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.Contains("Bannerlord.exe is using it", ex.Message); // named by Restart Manager, without jargon
                Assert.Contains("(PID 4242)", s.Log.ReadRecent());             // the PID stays in the log
                Assert.False(PlayerErrors.LooksTechnical(ex.Message));
                Assert.True(ex.RolledBack);
                Assert.Equal(Dll1, File.ReadAllBytes(dll));
                Assert.Equal(Items, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "ModuleData/items.xml")));
                Assert.True(File.Exists(h.ModuleFile("LotRMPEvent", "ModuleData/old.xml")));
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent", ".lotr-staging")));
                var c2 = await s.CheckAsync(null, CancellationToken.None); // works once the file is free
                await s.InstallAsync(c2.Plan, null, CancellationToken.None);
                Assert.Equal(Random(50_000, 6), File.ReadAllBytes(dll));
            }
        }

        [Fact]
        public async Task Running_game_blocks_the_install()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                h.Processes.Running.Add(new Lotr.Updater.Core.Game.RunningProcessInfo { Name = "Bannerlord.exe", Pid = 1 });
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Contains(c.Plan.Issues, i => i.Text.Contains("Bannerlord.exe"));
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.Contains("Close it", ex.Message);
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent")));
            }
        }

        [Fact]
        public async Task No_disk_space_blocks_before_anything()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                h.Fs.FreeSpace = 10_000;
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.True(c.Plan.IsBlocked);
                Assert.Contains(c.Plan.Issues, i => i.Text.StartsWith("Not enough free space"));
                await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent")));
                var cache = Path.Combine(h.Home, "cache", "objects");
                Assert.Empty(Directory.Exists(cache) ? Directory.GetFiles(cache, "*.raw", SearchOption.AllDirectories) : new string[0]);
            }
        }

        [Fact]
        public async Task Read_only_folder_is_explained_without_elevation()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                h.Fs.ReadOnly = p => p.StartsWith(h.Modules, StringComparison.OrdinalIgnoreCase);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                var b = c.Plan.Issues.Single(i => i.Severity == IssueSeverity.Blocker);
                Assert.Contains("never asks for administrator rights", b.Text);
                await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
            }
        }

        [Fact]
        public async Task Failure_in_the_middle_of_the_swap_rolls_back_everything()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var before = Snapshot(h);

                var r2 = h.NewRelease("r2");
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.3", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Random(50_000, 7))
                    .File("ModuleData/items.xml", Text(200_000, "x"))
                    .File("ModuleData/added.xml", "<a/>");
                r2.AddModule("LotRMPCore", "LotRMPCore", "v1.4.8.0", "LotRMPCore", "core").SubModule()
                    .File("AssetPackages/small.tpac", Random(600_000, 8));
                r2.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                // The event module is fully swapped; fail on the second "new file -> game folder" rename of the Core module.
                int coreMoves = 0;
                h.Fs.OnMove = (a, b) => a.Contains(@"LotRMPCore\.lotr-staging\new") && ++coreMoves == 2 ? new IOException("simulated disk error") : null;
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                h.Fs.OnMove = null;
                Assert.True(ex.RolledBack);
                Assert.Equal(before, Snapshot(h));
            }
        }

        [Fact]
        public async Task Crash_during_swap_is_rolled_back_at_next_start()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var before = Snapshot(h);
                var r2 = h.NewRelease("r2");
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.3", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Random(50_000, 9))
                    .File("ModuleData/items.xml", Text(200_000, "y"));
                r2.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                s.Engine.BeforeSwapStep = (i, p) => { if (i == 2) throw new TimeoutException("power cut"); };
                s.Engine.SimulatesCrash = e => e is TimeoutException;
                await Assert.ThrowsAsync<TimeoutException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.True(File.Exists(Path.Combine(h.Modules, "LotRMPEvent", ".lotr-staging", "journal.json")));
                Assert.NotEqual(before, Snapshot(h));

                h.Service(); // "next start": LocateGame runs the recovery
                Assert.Equal(before, Snapshot(h));
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent", ".lotr-staging")));
            }
        }

        [Fact]
        public async Task Mark_of_the_web_is_removed_and_verify_repairs()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var xml = h.ModuleFile("LotRMPEvent", "ModuleData/items.xml");
                // .NET Framework refuses ':' in paths; cmd writes the stream as a browser download would.
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo [ZoneTransfer]>\"" + xml + ":Zone.Identifier\"") { CreateNoWindow = true, UseShellExecute = false })) p.WaitForExit();
                Assert.True(s.Fs.HasMarkOfTheWeb(xml));
                File.AppendAllText(h.ModuleFile("LotRMPEvent", "ModuleData/old.xml"), "tampered");
                File.Delete(h.ModuleFile("LotRMPEvent", "SubModule.xml"));
                File.WriteAllText(Path.Combine(h.Modules, "LotRMPEvent", "stray.txt"), "x");

                var v = await s.VerifyAsync(null, CancellationToken.None);
                Assert.False(v.Healthy);
                Assert.Equal(new[] { "LotRMPEvent/SubModule.xml" }, v.Missing);
                Assert.Equal(new[] { "LotRMPEvent/ModuleData/old.xml" }, v.Modified);
                Assert.Equal(new[] { "LotRMPEvent/stray.txt" }, v.Extra);
                Assert.Equal(1, v.MarkOfTheWebCleared);
                Assert.False(s.Fs.HasMarkOfTheWeb(xml));

                await s.InstallAsync(v.RepairPlan, null, CancellationToken.None);
                var v2 = await s.VerifyAsync(null, CancellationToken.None);
                Assert.True(v2.Healthy);
                Assert.Equal(new[] { "LotRMPEvent/stray.txt" }, v2.Extra); // the player's own files are never deleted
            }
        }

        [Fact]
        public async Task Moved_file_is_copied_locally_instead_of_downloaded()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var r2 = h.NewRelease("r2");
                r2.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.2", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", Dll1)
                    .File("ModuleData/renamed/items.xml", Items);
                r2.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.9", "LotRMPCore", "core").SubModule()
                    .File("AssetPackages/small.tpac", Random(600_000, 2));
                r2.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                var a = c.Plan.Modules[0].Actions.Single(x => x.RelPath == "ModuleData/renamed/items.xml");
                Assert.Equal(FileAction.CopyLocal, a.Source);
                Assert.Empty(c.Plan.Downloads);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(Items, File.ReadAllBytes(h.ModuleFile("LotRMPEvent", "ModuleData/renamed/items.xml")));
                Assert.False(File.Exists(h.ModuleFile("LotRMPEvent", "ModuleData/items.xml")));
            }
        }

        [Fact]
        public async Task Older_release_is_refused_until_the_player_accepts_the_rollback()
        {
            using (var h = new Harness())
            {
                var r1 = Release1(h);
                r1.WriteTo(h.Releases);
                var s = h.Service();
                await CheckAndInstall(s);
                var old = h.NewRelease("old");
                old.PublishedAtUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
                old.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.1.0", "LotRMPEvent").SubModule().File("ModuleData/items.xml", Text(1_000, "old"));
                old.WriteTo(h.Releases);
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.True(c.RollbackOffered);
                Assert.Null(c.Plan);
                s.AcceptRollback(old.ReleaseId);
                var c2 = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c2.Error);
                await s.InstallAsync(c2.Plan, null, CancellationToken.None);
                Assert.Equal(old.ReleaseId, s.State.InstalledReleaseId);
                Assert.Null(s.State.AllowRollbackTo);
            }
        }

        [Fact]
        public async Task Workshop_copy_with_same_id_is_flagged()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var ws = Path.Combine(h.Root, "game", "steamapps", "workshop", "content", "261550", "3774022783");
                Directory.CreateDirectory(ws);
                File.WriteAllText(Path.Combine(ws, "SubModule.xml"), "<Module><Id value=\"OrderMiddleEarthEvent\"/></Module>");
                var copy = Path.Combine(h.Modules, "LotRMPEvent - Copy");
                Directory.CreateDirectory(copy);
                File.WriteAllText(Path.Combine(copy, "SubModule.xml"), "<Module><Id value=\"LotRMPCore\"/></Module>");
                var s = h.Service();
                Assert.Equal(LotR.GameDetection.GameStore.Steam, s.Game.Store);
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Contains(c.Plan.Issues, i => i.Text.Contains("Steam Workshop item 3774022783"));
                Assert.Contains(c.Plan.Issues, i => i.Text.Contains("LotRMPEvent - Copy"));
            }
        }

        [Fact]
        public async Task Game_version_outside_range_is_warned()
        {
            using (var h = new Harness())
            {
                var r = Release1(h);
                r.GameMin = "v1.3.0";
                r.WriteTo(h.Releases);
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Contains(c.Plan.Issues, i => i.Text.Contains("needs v1.3.0 or newer"));
            }
        }

        [Fact]
        public async Task Background_predownload_fills_the_cache_without_touching_the_game()
        {
            using (var h = new Harness())
            {
                Release1(h).WriteTo(h.Releases);
                var s = h.Service();
                s.State.PreDownloadMaxJitterMinutes = 0;
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.True(await s.PreDownloadIfDueAsync(c, null, CancellationToken.None));
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent")));
                var c2 = await s.CheckAsync(null, CancellationToken.None);
                Assert.Empty(c2.Plan.Downloads);
                long served = h.Server.BytesServed;
                await s.InstallAsync(c2.Plan, null, CancellationToken.None);
                Assert.Equal(served, h.Server.BytesServed);
                Assert.True(File.Exists(h.ModuleFile("LotRMPEvent", "SubModule.xml")));
            }
        }

        private static string Snapshot(Harness h)
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.GetFiles(h.Modules, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(f.Substring(h.Modules.Length)).Append('=').Append(ReleaseBuilder.Sha(File.ReadAllBytes(f))).Append('\n');
            return sb.ToString();
        }
    }
}
