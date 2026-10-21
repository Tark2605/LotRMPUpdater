using System;
using System.Collections.Generic;
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
    /// <summary>
    /// Real-world install scenarios (docs/TEST-GRID.md). Each one runs the real engine against a local test server and a fake
    /// game in a temp folder, then checks the final state: the files match the release, nothing is left behind (staging,
    /// partial downloads), the player's own files are kept, and every text the player sees is calm English without raw
    /// .NET exception text.
    /// </summary>
    public class ScenarioGridTests
    {
        private const string CoreItem = "3772431545";
        private const string EventItem = "3774022783";

        private static readonly byte[] EventDll = EngineTests.Random(50_000, 11);
        private static readonly byte[] Brushes = EngineTests.Text(40_000, "brush");
        private static readonly byte[] Items = EngineTests.Text(120_000, "item");
        private static readonly byte[] CoreSmall = EngineTests.Random(600_000, 12);
        private static readonly byte[] CoreXml = EngineTests.Text(30_000, "core");

        /// <summary>Event (with GUI/Brushes/MeeBrushes.xml, the file of the player report) + Core.</summary>
        private static ReleaseBuilder Std(Harness h, string label, byte[] coreSmall = null, string eventVersion = "v1.0.2.2", bool workshopIds = false)
        {
            var r = h.NewRelease(label);
            r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", eventVersion, "LotRMPEvent").SubModule()
                .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", EventDll)
                .File("GUI/Brushes/MeeBrushes.xml", Brushes)
                .File("ModuleData/items.xml", Items);
            r.AddModule("LotRMPCore", "LotRMPCore", "v1.4.8.0", "LotRMPCore", "core").SubModule()
                .File("AssetPackages/small.tpac", coreSmall ?? CoreSmall)
                .File("ModuleData/core.xml", CoreXml);
            if (workshopIds) { Event(r).WorkshopItem = EventItem; Core(r).WorkshopItem = CoreItem; }
            r.WriteTo(h.Releases);
            return r;
        }

        private static ModuleBuilder Event(ReleaseBuilder r) => r.Modules.Single(m => m.Folder == "LotRMPEvent");
        private static ModuleBuilder Core(ReleaseBuilder r) => r.Modules.Single(m => m.Folder == "LotRMPCore");

        private static void WriteModule(ModuleBuilder m, string folder)
        {
            foreach (var f in m.Files)
            {
                var p = Path.Combine(folder, f.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllBytes(p, f.Content);
            }
        }

        private static async Task<CheckResult> Check(UpdaterService s)
        {
            var c = await s.CheckAsync(null, CancellationToken.None);
            Assert.Null(c.Error);
            AssertFriendly(c.Plan.Issues.Select(i => i.Text));
            return c;
        }

        /// <summary>Every release file is in place with the right bytes, nothing of ours is left behind.</summary>
        private static void AssertInstalled(Harness h, ReleaseBuilder r, params string[] allowedExtra)
        {
            foreach (var m in r.Modules)
            {
                var folder = Path.Combine(h.Modules, m.Folder);
                foreach (var f in m.Files) Assert.Equal(f.Content, File.ReadAllBytes(h.ModuleFile(m.Folder, f.Path)));
                Assert.False(Directory.Exists(Path.Combine(folder, ".lotr-staging")), "staging left in " + m.Folder);
                var expected = new HashSet<string>(m.Files.Select(f => f.Path.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
                foreach (var e in allowedExtra) expected.Add(e);
                var actual = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(p => p.Substring(folder.Length + 1));
                Assert.DoesNotContain(actual, p => !expected.Contains(p));
            }
            var cache = Path.Combine(h.Home, "cache");
            if (Directory.Exists(cache)) Assert.Empty(Directory.GetFiles(cache, "*.part", SearchOption.AllDirectories));
        }

        private static void AssertFriendly(IEnumerable<string> texts)
        {
            foreach (var t in texts) AssertFriendly(t);
        }

        /// <summary>No raw exception text (any language), no non-English letters, short enough to read.</summary>
        internal static void AssertFriendly(string text)
        {
            if (text == null) return;
            Assert.False(PlayerErrors.LooksTechnical(text), "technical text shown to the player: " + text);
            Assert.DoesNotMatch("[àâçéèêëîïôûùüÿœÀÂÇÉÈÊÔÛäöß]", text);
            Assert.True(text.Length < 600, "too long: " + text);
        }

        private static string Snapshot(string folder)
        {
            var sb = new StringBuilder();
            if (!Directory.Exists(folder)) return "";
            foreach (var f in Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(f.Substring(folder.Length)).Append('=').Append(ReleaseBuilder.Sha(File.ReadAllBytes(f))).Append('\n');
            return sb.ToString();
        }

        private static IOException Locked() => new IOException("Le processus ne peut pas accéder au fichier (simulated)", unchecked((int)0x80070020));
        private static IOException DiskFull() => new IOException("Espace insuffisant sur le disque (simulated)", unchecked((int)0x80070070));

        // ------------------------------------------------------------------ starting points

        [Fact]
        public async Task Fresh_pc_installs_everything()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "fresh");
                var s = h.Service();
                var c = await Check(s);
                Assert.All(c.Plan.Modules, m => Assert.True(m.IsNewFolder));
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.True((await s.CheckAsync(null, CancellationToken.None)).UpToDate);
            }
        }

        [Fact]
        public async Task Core_only_from_workshop_copies_core_and_downloads_event()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "core-ws");
                var ws = h.WorkshopItem(CoreItem);
                WriteModule(Core(r), ws);
                var before = Snapshot(ws);
                var s = h.Service();
                var c = await Check(s);
                Assert.All(c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPCore").Actions, a => Assert.True(a.FromWorkshop));
                Assert.False(c.Plan.NeedsCoreSlot);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Equal(0, h.Server.Hits(r.ObjectUrlPath(CoreSmall)));
                Assert.Equal(before, Snapshot(ws)); // the Workshop folder is only read
            }
        }

        [Fact]
        public async Task Core_only_in_local_modules_downloads_only_the_event()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "core-local");
                WriteModule(Core(r), Path.Combine(h.Modules, "LotRMPCore"));
                var s = h.Service();
                var c = await Check(s);
                Assert.False(c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPCore").HasChanges);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Equal(0, h.Server.Hits(r.ObjectUrlPath(CoreSmall)));
            }
        }

        [Fact]
        public async Task Core_in_modules_and_workshop_keeps_modules_and_only_informs()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "core-both");
                WriteModule(Core(r), Path.Combine(h.Modules, "LotRMPCore"));
                WriteModule(Core(r), h.WorkshopItem(CoreItem));
                var s = h.Service();
                var c = await Check(s);
                Assert.False(c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPCore").HasChanges);
                var info = c.Plan.Issues.Single(i => i.Text.Contains("Steam Workshop"));
                Assert.Equal(IssueSeverity.Info, info.Severity);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Event_only_installed_downloads_the_core()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "event-only");
                WriteModule(Event(r), Path.Combine(h.Modules, "LotRMPEvent"));
                var s = h.Service();
                var c = await Check(s);
                Assert.False(c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPEvent").HasChanges);
                Assert.True(c.Plan.NeedsCoreSlot);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Both_modules_from_workshop_download_nothing()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "both-ws");
                WriteModule(Core(r), h.WorkshopItem(CoreItem));
                WriteModule(Event(r), h.WorkshopItem(EventItem));
                var s = h.Service();
                var c = await Check(s);
                Assert.Empty(c.Plan.Downloads);
                long served = h.Server.BytesServed;
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                Assert.Equal(served, h.Server.BytesServed);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Both_local_and_up_to_date_needs_nothing()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "uptodate");
                WriteModule(Core(r), Path.Combine(h.Modules, "LotRMPCore"));
                WriteModule(Event(r), Path.Combine(h.Modules, "LotRMPEvent"));
                var s = h.Service();
                var c = await Check(s);
                Assert.True(c.UpToDate);
                Assert.Equal(r.ReleaseId, s.State.InstalledReleaseId);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Old_local_versions_are_updated_and_player_files_kept()
        {
            using (var h = new Harness())
            {
                var old = h.NewRelease("old-local");
                old.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.1.0", "LotRMPEvent").SubModule()
                    .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", EngineTests.Random(50_000, 99))
                    .File("GUI/Brushes/MeeBrushes.xml", EngineTests.Text(39_000, "oldbrush"));
                old.AddModule("LotRMPCore", "LotRMPCore", "v1.4.7.0", "LotRMPCore", "core").SubModule()
                    .File("AssetPackages/small.tpac", EngineTests.Random(600_000, 98));
                WriteModule(old.Modules[0], Path.Combine(h.Modules, "LotRMPEvent"));
                WriteModule(old.Modules[1], Path.Combine(h.Modules, "LotRMPCore"));
                File.WriteAllText(Path.Combine(h.Modules, "LotRMPEvent", "my-notes.txt"), "mine");
                var r = Std(h, "new");
                var s = h.Service();
                var c = await Check(s);
                Assert.Contains("my-notes.txt", c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPEvent").Extra);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r, "my-notes.txt");
            }
        }

        [Theory]
        [InlineData(true)]  // the Workshop copy is older than the release
        [InlineData(false)] // the Workshop copy is newer (Workshop updated before the team published here)
        public async Task Workshop_copy_of_another_version_reuses_identical_files_only(bool older)
        {
            using (var h = new Harness())
            {
                var r = Std(h, "ws-version");
                var ws = h.WorkshopItem(CoreItem);
                WriteModule(Core(r), ws);
                File.WriteAllBytes(Path.Combine(ws, "AssetPackages", "small.tpac"), EngineTests.Random(600_000, older ? 50 : 51));
                if (!older) File.WriteAllText(Path.Combine(ws, "ModuleData", "only-in-newer.xml"), "<x/>");
                var s = h.Service();
                var c = await Check(s);
                var core = c.Plan.Modules.Single(m => m.Module.Folder == "LotRMPCore");
                Assert.Equal(FileAction.Download, core.Actions.Single(a => a.RelPath == "AssetPackages/small.tpac").Source);
                Assert.Equal(FileAction.CopyLocal, core.Actions.Single(a => a.RelPath == "ModuleData/core.xml").Source);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        // ------------------------------------------------------------------ things that change between check and install

        /// <summary>The player report: unsubscribed from the Workshop items right before installing; Steam deleted the folders.</summary>
        [Fact]
        public async Task Unsubscribed_between_check_and_install_downloads_instead()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "unsub");
                WriteModule(Core(r), h.WorkshopItem(CoreItem));
                WriteModule(Event(r), h.WorkshopItem(EventItem));
                var s = h.Service();
                var c = await Check(s);
                Assert.Empty(c.Plan.Downloads);
                Directory.Delete(h.WorkshopItem(CoreItem), true);
                Directory.Delete(h.WorkshopItem(EventItem), true);

                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.True(h.Server.Hits(r.ObjectUrlPath(Brushes)) > 0);
                Assert.Contains("it will be downloaded instead", s.Log.ReadRecent());
            }
        }

        /// <summary>Steam deletes the Workshop folder while the updater is reading it (after the pre-install re-check).</summary>
        [Fact]
        public async Task Workshop_folder_vanishing_while_preparing_is_downloaded_on_the_spot()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "vanish");
                WriteModule(Event(r), h.WorkshopItem(EventItem));
                var s = h.Service();
                var c = await Check(s);
                Assert.Contains(c.Plan.Modules.SelectMany(m => m.Actions), a => a.FromWorkshop);
                h.Fs.OnOpenRead = p =>
                {
                    if (p.IndexOf(@"\workshop\content\", StringComparison.OrdinalIgnoreCase) < 0) return null;
                    try { Directory.Delete(h.WorkshopItem(EventItem), true); } catch (IOException) { }
                    return new DirectoryNotFoundException("Impossible de trouver une partie du chemin d'accès '" + p + "'.");
                };
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                h.Fs.OnOpenRead = null;
                AssertInstalled(h, r);
                Assert.Contains("Downloading it instead", s.Log.ReadRecent());
            }
        }

        /// <summary>Second safety net: with the on-the-spot fallback off, the service re-plans once and succeeds.</summary>
        [Fact]
        public async Task Failing_local_source_is_replanned_once_automatically()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "replan");
                WriteModule(Event(r), h.WorkshopItem(EventItem));
                var s = h.Service();
                s.Engine.Options.DownloadWhenLocalSourceFails = false;
                var c = await Check(s);
                int opens = 0;
                h.Fs.OnOpenRead = p =>
                {
                    if (p.IndexOf(@"\workshop\content\", StringComparison.OrdinalIgnoreCase) < 0 || opens++ > 0) return null;
                    Directory.Delete(h.WorkshopItem(EventItem), true);
                    return new DirectoryNotFoundException("Impossible de trouver une partie du chemin d'accès '" + p + "'.");
                };
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                h.Fs.OnOpenRead = null;
                AssertInstalled(h, r);
                Assert.Contains("retrying once", s.Log.ReadRecent());
            }
        }

        /// <summary>When nothing works (Steam keeps the Workshop files locked), the player gets the calm message, not the French .NET one.</summary>
        [Fact]
        public async Task Workshop_source_that_keeps_failing_gives_a_calm_message_and_changes_nothing()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "stuck", workshopIds: true);
                WriteModule(Event(r), h.WorkshopItem(EventItem));
                var s = h.Service();
                s.Engine.Options.DownloadWhenLocalSourceFails = false;
                var c = await Check(s);
                var before = Snapshot(h.Modules);
                h.Fs.OnOpenRead = p => p.IndexOf(@"\workshop\content\", StringComparison.OrdinalIgnoreCase) >= 0
                    ? new DirectoryNotFoundException("Impossible de trouver une partie du chemin d'accès '" + p + "'.") : null;
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                h.Fs.OnOpenRead = null;
                Assert.Equal(PlayerErrors.WorkshopSourceGone, ex.Message);
                Assert.True(ex.RolledBack);
                Assert.Equal(ProblemKind.LocalSourceChanged, ex.Kind);
                Assert.Contains("Impossible de trouver", ex.Detail); // the raw text stays in the details
                AssertFriendly(ex.Message);
                Assert.Equal(before, Snapshot(h.Modules));
                // The player clicks again once Steam is done: it works.
                s.Engine.Options.DownloadWhenLocalSourceFails = true;
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Steam_rewriting_a_workshop_file_is_noticed_before_installing()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "steam-rewrite");
                var ws = h.WorkshopItem(EventItem);
                WriteModule(Event(r), ws);
                var s = h.Service();
                var c = await Check(s);
                var brushes = Path.Combine(ws, "GUI", "Brushes", "MeeBrushes.xml");
                File.WriteAllBytes(brushes, EngineTests.Text(Brushes.Length, "steam")); // same size, new content, new date
                File.SetLastWriteTimeUtc(brushes, DateTime.UtcNow.AddMinutes(5));
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Contains("has different content now", s.Log.ReadRecent());
            }
        }

        /// <summary>Same size AND same date: the hash cache would trust the old hash; the final SHA-256 check catches it.</summary>
        [Fact]
        public async Task Stale_hash_cache_entry_is_caught_by_the_final_check()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "stale-hash");
                var ws = h.WorkshopItem(EventItem);
                WriteModule(Event(r), ws);
                var s = h.Service();
                var c = await Check(s);
                var brushes = Path.Combine(ws, "GUI", "Brushes", "MeeBrushes.xml");
                var when = File.GetLastWriteTimeUtc(brushes);
                File.WriteAllBytes(brushes, EngineTests.Text(Brushes.Length, "steam"));
                File.SetLastWriteTimeUtc(brushes, when);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Contains("content differs from the release", s.Log.ReadRecent());
            }
        }

        [Fact]
        public async Task Player_changes_in_modules_between_check_and_install_are_handled()
        {
            using (var h = new Harness())
            {
                var r1 = Std(h, "p1");
                var s = h.Service();
                await s.InstallAsync((await Check(s)).Plan, null, CancellationToken.None);
                var r2 = Std(h, "p2", EngineTests.Random(600_000, 13), "v1.0.2.3");
                var c = await Check(s);
                Assert.Contains(c.Plan.Modules.SelectMany(m => m.Actions), a => a.RelPath == "AssetPackages/small.tpac" && a.Kind == FileActionKind.Replace);
                File.Delete(h.ModuleFile("LotRMPCore", "AssetPackages/small.tpac"));      // planned Replace, now missing
                File.Delete(h.ModuleFile("LotRMPEvent", "ModuleData/items.xml"));        // planned Keep, now missing
                File.AppendAllText(h.ModuleFile("LotRMPCore", "ModuleData/core.xml"), "!"); // planned Keep, now different
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r2);
            }
        }

        // ------------------------------------------------------------------ locks, disks, network

        [Fact]
        public async Task Briefly_locked_file_is_retried_and_the_install_succeeds()
        {
            using (var h = new Harness())
            {
                Std(h, "lock1");
                var s = h.Service();
                await s.InstallAsync((await Check(s)).Plan, null, CancellationToken.None);
                var r2 = Std(h, "lock2", EngineTests.Random(600_000, 14));
                var c = await Check(s);
                var target = h.ModuleFile("LotRMPCore", "AssetPackages/small.tpac");
                int tries = 0;
                h.Fs.OnMove = (a, b) => string.Equals(a, target, StringComparison.OrdinalIgnoreCase) && tries++ < 2 ? Locked() : null;
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                h.Fs.OnMove = null;
                AssertInstalled(h, r2);
                Assert.Equal(3, tries);
                Assert.Contains(TimeSpan.FromMilliseconds(250), h.Delays);
            }
        }

        [Theory]
        [InlineData(true)]  // Restart Manager names the program
        [InlineData(false)] // it cannot tell
        public async Task File_locked_for_good_rolls_back_with_a_clear_message(bool known)
        {
            using (var h = new Harness())
            {
                Std(h, "lockA");
                var s = h.Service();
                await s.InstallAsync((await Check(s)).Plan, null, CancellationToken.None);
                Std(h, "lockB", EngineTests.Random(600_000, 15));
                var c = await Check(s);
                var before = Snapshot(h.Modules);
                if (!known) h.LockFinder.Lockers = null;
                else h.LockFinder.Lockers = new List<Lotr.Updater.Core.Game.RunningProcessInfo> { new Lotr.Updater.Core.Game.RunningProcessInfo { Name = "MsMpEng.exe", Pid = 7 } };
                var target = h.ModuleFile("LotRMPCore", "AssetPackages/small.tpac");
                h.Fs.OnMove = (a, b) => string.Equals(a, target, StringComparison.OrdinalIgnoreCase) ? Locked() : null;
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                h.Fs.OnMove = null;
                Assert.Equal(ProblemKind.FileLocked, ex.Kind);
                Assert.True(ex.RolledBack);
                Assert.Contains("small.tpac", ex.Message);
                Assert.Contains(known ? "MsMpEng.exe is using it" : "another program is using it", ex.Message);
                Assert.Contains(PlayerErrors.NothingChanged, ex.Message);
                AssertFriendly(ex.Message);
                Assert.Equal(before, Snapshot(h.Modules));
            }
        }

        [Fact]
        public async Task Disk_full_while_preparing_rolls_back_with_a_clear_message()
        {
            using (var h = new Harness())
            {
                Std(h, "full1");
                var s = h.Service();
                await s.InstallAsync((await Check(s)).Plan, null, CancellationToken.None);
                Std(h, "full2", EngineTests.Random(600_000, 16));
                var c = await Check(s);
                var before = Snapshot(h.Modules);
                h.Fs.OnOpenWrite = p => p.IndexOf(@"\.lotr-staging\new\", StringComparison.OrdinalIgnoreCase) >= 0 ? DiskFull() : null;
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                h.Fs.OnOpenWrite = null;
                Assert.Equal(ProblemKind.DiskFull, ex.Kind);
                Assert.StartsWith("Your disk became full", ex.Message);
                AssertFriendly(ex.Message);
                Assert.Equal(before, Snapshot(h.Modules));
            }
        }

        [Fact]
        public async Task Disk_full_while_downloading_stops_at_once_without_touching_the_game()
        {
            using (var h = new Harness())
            {
                Std(h, "full-dl");
                var s = h.Service();
                var c = await Check(s);
                h.Fs.OnOpenWrite = p => p.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? DiskFull() : null;
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                h.Fs.OnOpenWrite = null;
                Assert.Equal(ProblemKind.DiskFull, ex.Kind);
                Assert.Contains("Nothing was changed in your game folder", ex.Message);
                AssertFriendly(ex.Message);
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPEvent")));
                Assert.DoesNotContain("resuming in", s.Log.ReadRecent()); // not retried as if it were the network
            }
        }

        [Fact]
        public async Task Interrupted_download_fails_calmly_then_resumes()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "resume");
                var path = r.ObjectUrlPath(CoreSmall);
                h.Server.Faults = (p, hit) => p == path ? new Fault { CutAfterBytes = 10_000 } : null;
                var s = h.Service();
                var c = await Check(s);
                var ex = await Assert.ThrowsAsync<InstallException>(() => s.InstallAsync(c.Plan, null, CancellationToken.None));
                Assert.Equal(ProblemKind.Network, ex.Kind);
                Assert.Contains("what was already downloaded is kept", ex.Message);
                AssertFriendly(ex.Message);
                Assert.False(Directory.Exists(Path.Combine(h.Modules, "LotRMPCore")));

                h.Server.Faults = null;
                var c2 = await Check(s);
                Assert.Contains(c2.Plan.Downloads, d => d.AlreadyHave > 0);
                await s.InstallAsync(c2.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Contains(h.Server.RangeRequests, x => x.StartsWith(path) && x.Contains("bytes=") && !x.Contains("bytes=0-"));
            }
        }

        [Fact]
        public async Task Unreachable_server_gives_a_calm_check_message()
        {
            using (var h = new Harness())
            {
                Std(h, "down");
                h.Server.Faults = (p, hit) => new Fault { Status = 503 };
                var s = h.Service();
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.NotNull(c.Error);
                Assert.StartsWith("The updater could not reach the LotR server", c.Error);
                Assert.NotNull(c.ErrorDetail);
                AssertFriendly(c.Error);
            }
        }

        // ------------------------------------------------------------------ where the game is

        [Fact]
        public async Task Game_in_another_steam_library_uses_that_library_workshop()
        {
            using (var h = new Harness(FakeStore.SteamOtherLibrary))
            {
                var r = Std(h, "lib2");
                WriteModule(Core(r), h.WorkshopItem(CoreItem));
                var s = h.Service();
                Assert.Equal(LotR.GameDetection.GameStore.Steam, s.Game.Store);
                var c = await Check(s);
                Assert.True(c.Plan.WorkshopCopiedFiles > 0);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Backup_on_another_drive_is_copied_and_the_update_completes()
        {
            using (var h = new Harness())
            {
                Std(h, "drv1");
                var s = h.Service();
                await s.InstallAsync((await Check(s)).Plan, null, CancellationToken.None);
                var r2 = Std(h, "drv2", EngineTests.Random(600_000, 17));
                h.Fs.AllDifferentVolumes = true;
                var c = await Check(s);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r2);
                Assert.Equal(CoreSmall, File.ReadAllBytes(Path.Combine(c.Plan.BackupPath, "LotRMPCore", "AssetPackages", "small.tpac")));
            }
        }

        [Theory]
        [InlineData(FakeStore.Epic, LotR.GameDetection.GameStore.Epic)]
        [InlineData(FakeStore.Gog, LotR.GameDetection.GameStore.Gog)]
        [InlineData(FakeStore.Xbox, LotR.GameDetection.GameStore.Xbox)]
        public async Task Other_stores_install_without_any_workshop(FakeStore store, LotR.GameDetection.GameStore expected)
        {
            using (var h = new Harness(store))
            {
                var r = Std(h, "store-" + store);
                var s = h.Service();
                Assert.Equal(expected, s.Game.Store);
                var c = await Check(s);
                Assert.Equal(0, c.Plan.WorkshopReusedBytes);
                Assert.DoesNotContain(c.Plan.Issues, i => i.Text.Contains("Workshop"));
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
            }
        }

        [Fact]
        public async Task Duplicate_copy_under_another_folder_name_is_warned_and_left_alone()
        {
            using (var h = new Harness())
            {
                var r = Std(h, "dup");
                var copy = Path.Combine(h.Modules, "LotRMPCore (old)");
                WriteModule(Core(r), copy);
                var before = Snapshot(copy);
                var s = h.Service();
                var c = await Check(s);
                var warn = c.Plan.Issues.Single(i => i.Text.Contains("LotRMPCore (old)"));
                Assert.Equal(IssueSeverity.Warning, warn.Severity);
                await s.InstallAsync(c.Plan, null, CancellationToken.None);
                AssertInstalled(h, r);
                Assert.Equal(before, Snapshot(copy));
            }
        }

        [Fact]
        public void Start_up_crash_report_travels_with_the_support_log()
        {
            using (var h = new Harness())
            {
                var s = h.Service();
                Directory.CreateDirectory(s.Paths.LogDir);
                File.WriteAllText(Path.Combine(s.Paths.LogDir, "crash-20261021-101010.txt"), "LotR MP Event updater crash report\nwhere=startup");
                var log = s.BuildSupportLog();
                Assert.Contains("--- latest crash report (crash-20261021-101010.txt) ---", log);
                Assert.Contains("where=startup", log);
            }
        }

        // ------------------------------------------------------------------ wording

        [Fact]
        public void The_reported_french_exception_becomes_a_calm_english_message()
        {
            var raw = new DirectoryNotFoundException(@"Impossible de trouver une partie du chemin d'accès 'C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3774022783\GUI\Brushes\MeeBrushes.xml'.");
            var p = PlayerErrors.ForInstall(raw);
            Assert.Equal(ProblemKind.LocalSourceChanged, p.Kind);
            Assert.Equal(PlayerErrors.WorkshopSourceGone, p.Message);
            Assert.Contains("Impossible de trouver", p.Detail);
            AssertFriendly(p.Message);
            Assert.True(PlayerErrors.LooksTechnical(raw.Message));
        }

        [Fact]
        public void Every_install_message_is_friendly()
        {
            var samples = new Exception[]
            {
                new UnauthorizedAccessException("L'accès au chemin 'C:\\x' est refusé."),
                new IOException("Espace insuffisant sur le disque.", unchecked((int)0x80070070)),
                Locked(),
                new Lotr.Updater.Core.Net.HttpFetchException("Download kept failing (Une connexion existante a dû être fermée): /x", null),
                new Lotr.Updater.Core.Net.HttpFetchException("The server answered 404 Not Found for /x", System.Net.HttpStatusCode.NotFound),
                new Lotr.Updater.Core.Objects.CorruptObjectException("arrived damaged twice"),
                new InvalidOperationException("Opération non valide en raison de l'état actuel de l'objet."),
                new FileNotFoundException("Fichier introuvable", @"C:\Games\Bannerlord\Modules\LotRMPCore\x.xml"),
            };
            foreach (var ex in samples)
            {
                AssertFriendly(PlayerErrors.ForInstall(ex).Message);
                AssertFriendly(PlayerErrors.ForCheck(ex).Message);
                AssertFriendly(PlayerErrors.ForVerify(ex).Message);
                AssertFriendly(PlayerErrors.ForSupport(ex).Message);
                Assert.NotNull(PlayerErrors.ForInstall(ex).Detail);
            }
        }
    }
}
