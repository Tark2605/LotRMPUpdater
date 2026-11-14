using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Install;
using LotR.Releases.Model;
using Lotr.Updater.Core.Game.Linux;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>
    /// The Linux rules of the shared engine (docs/LINUX.md), tested on any OS: paths, errno codes, player texts, log masking,
    /// letter-case copies of release files, and the /proc check for a running game.
    /// </summary>
    public class LinuxSupportTests
    {
        // ------------------------------------------------------------------ paths

        [Fact]
        public void Strictly_under_needs_a_real_parent_folder()
        {
            var root = Path.Combine(Path.GetTempPath(), "lotr-ospath", "backup");
            Assert.True(OsPath.IsStrictlyUnder(Path.Combine(root, "r1"), root));
            Assert.True(OsPath.IsStrictlyUnder(Path.Combine(root, "r1", "a"), root + Path.DirectorySeparatorChar));
            Assert.False(OsPath.IsStrictlyUnder(root, root));
            Assert.False(OsPath.IsStrictlyUnder(root + "2", root));          // same prefix, other folder
            Assert.False(OsPath.IsStrictlyUnder(Path.GetDirectoryName(root), root));
            Assert.False(OsPath.IsStrictlyUnder(null, root));
        }

        [Fact]
        public void Path_comparison_ignores_case_only_on_windows()
        {
            Assert.Equal(StringComparison.OrdinalIgnoreCase, OsPath.ComparisonFor(true));
            Assert.Equal(StringComparison.Ordinal, OsPath.ComparisonFor(false));
            Assert.True(OsPath.ComparerFor(true).Equals("ModuleData/a.xml", "moduledata/a.xml"));
            Assert.False(OsPath.ComparerFor(false).Equals("ModuleData/a.xml", "moduledata/a.xml"));
            Assert.Equal(OsPath.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, OsPath.Comparison);
        }

        [Theory]
        [InlineData("/run/media/deck/SD/steamapps/common/x", "/run/media/deck/SD")]
        [InlineData("/run/media/deck/SD", "/run/media/deck/SD")]
        [InlineData("/run/media/deck/SD2/steamapps", "/run")]    // "/run/media/deck/SD" is not a parent at a folder boundary
        [InlineData("/home/deck/.local/share/Steam", "/home")]
        [InlineData("/opt/games", "/")]
        public void The_volume_of_a_linux_path_is_its_longest_mount_point(string path, string expected)
        {
            var mounts = new[] { "/", "/home", "/run/media/deck/SD", "/run", "/boot/efi", "relative" };
            Assert.Equal(expected, RealFileSystem.LongestMountPoint(path, mounts));
        }

        [Fact]
        public void Manifest_paths_still_resolve_inside_the_module_folder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "lotr-ospath", "Modules", "LotRMPEvent");
            var full = RelativePath.ToFileSystem(folder, "ModuleData/items.xml");
            Assert.Equal(Path.Combine(folder, "ModuleData", "items.xml"), full);
            Assert.Equal("ModuleData/items.xml", RelativePath.FromFileSystem(folder, full));
            Assert.Throws<InvalidDataException>(() => RelativePath.ToFileSystem(folder, "../LotRMPCore/x.dll"));
        }

        // ------------------------------------------------------------------ errno and player texts

        private static IOException Io(int hresult) => new IOException("simulated", hresult);

        [Fact]
        public void Disk_full_and_locks_follow_the_codes_of_each_os()
        {
            // Windows: HRESULT 0x80070070 (112) and 39; Linux: the errno itself (ENOSPC 28, EDQUOT 122).
            Assert.True(PlayerErrors.IsDiskFull(Io(unchecked((int)0x80070070)), windows: true));
            Assert.True(PlayerErrors.IsDiskFull(Io(39), windows: true));
            Assert.True(PlayerErrors.IsDiskFull(Io(28), windows: false));
            Assert.True(PlayerErrors.IsDiskFull(Io(122), windows: false));
            Assert.False(PlayerErrors.IsDiskFull(Io(39), windows: false));  // ENOTEMPTY
            Assert.False(PlayerErrors.IsDiskFull(Io(112), windows: false));
            Assert.False(PlayerErrors.IsDiskFull(Io(28), windows: true));
            Assert.False(PlayerErrors.IsDiskFull(new UnauthorizedAccessException(), windows: false));

            Assert.True(PlayerErrors.IsSharingViolation(Io(unchecked((int)0x80070020)), windows: true));
            Assert.False(PlayerErrors.IsSharingViolation(Io(32), windows: false)); // EPIPE: Linux has no mandatory locks
            Assert.False(PlayerErrors.IsSharingViolation(Io(33), windows: false));

            Assert.True(PlayerErrors.IsReadOnlyFileSystem(Io(30), windows: false)); // EROFS
            Assert.False(PlayerErrors.IsReadOnlyFileSystem(Io(30), windows: true));
        }

        [Fact]
        public void Linux_player_texts_are_calm_english_without_windows_advice()
        {
            var texts = new[]
            {
                PlayerErrors.CannotWriteText("/run/media/deck/SD/steamapps/common/Mount & Blade II Bannerlord/Modules", false, windows: false),
                PlayerErrors.GameRunningText(new[] { "Bannerlord.exe" }, windows: false),
                PlayerErrors.LinuxWriteAdvice,
            };
            foreach (var t in texts)
            {
                Assert.False(PlayerErrors.LooksTechnical(t), t);
                Assert.True(t.All(ch => ch < 128), "non-English letters: " + t);
                Assert.DoesNotContain("Windows", t);
                Assert.DoesNotContain("D:\\", t);
                Assert.DoesNotContain("Xbox", t);
            }
            Assert.Contains("sudo", texts[0]);
            // Windows texts are unchanged.
            Assert.Contains("Controlled folder access", PlayerErrors.CannotWriteText("C:\\Games\\Modules", false, windows: true));
            Assert.Contains("Windows does not let anyone replace", PlayerErrors.GameRunningText(new[] { "Bannerlord.exe" }, windows: true));
        }

        [Fact]
        public void Workshop_paths_are_recognised_with_both_separators()
        {
            Assert.True(PlayerErrors.IsWorkshopPath(@"D:\SteamLibrary\steamapps\workshop\content\261550\3774022783\SubModule.xml"));
            Assert.True(PlayerErrors.IsWorkshopPath("/home/deck/.local/share/Steam/steamapps/workshop/content/261550/3774022783/SubModule.xml"));
            Assert.False(PlayerErrors.IsWorkshopPath("/home/deck/Games/Modules/LotRMPCore/SubModule.xml"));
        }

        // ------------------------------------------------------------------ support log masking

        [Fact]
        public void A_linux_home_is_masked_in_the_support_log()
        {
            var log = "Game: Steam v1.2.12 at /home/alice/.local/share/Steam/steamapps/common/Mount & Blade II Bannerlord\n" +
                      "cache /home/alice/.local/share/LotRMPUpdater/cache, user alice, other /home/alicebob/x";
            var m = LogMasker.Mask(log, "alice", "/home/alice", "alice-pc");
            Assert.Contains("/home/<user>/.local/share/Steam/steamapps", m);
            Assert.Contains("/home/<user>/.local/share/LotRMPUpdater/cache", m);
            Assert.DoesNotContain("alice/", m);
            Assert.Contains("user <user>", m);
            Assert.Contains("/home/alicebob/x", m); // another user's folder is not this user's name
        }

        [Fact]
        public void The_steam_deck_account_name_keeps_steam_deck_readable()
        {
            var m = LogMasker.Mask("Steam Deck, game in /home/deck/.local/share/Steam", "deck", "/home/deck", "steamdeck");
            Assert.Contains("/home/<user>/.local/share/Steam", m);
            Assert.Contains("Steam Deck", m);
        }

        [Fact]
        public void Windows_masking_is_unchanged()
        {
            var m = LogMasker.Mask(@"C:\Users\Alice\AppData\Local\LotRMPUpdater and C:/Users/Alice/x", "Alice", @"C:\Users\Alice", "PC1");
            Assert.Contains(@"C:\Users\<user>\AppData\Local\LotRMPUpdater", m);
            Assert.Contains("C:/Users/<user>/x", m);
        }

        // ------------------------------------------------------------------ letter-case copies (Linux rules on any OS)

        private static ReleaseBuilder EventRelease(Harness h)
        {
            var r = h.NewRelease("case");
            r.AddModule("OrderMiddleEarthEvent", "LotRMPEvent", "v1.0.2.7", "LotRMPEvent").SubModule()
                .File("ModuleData/Items.xml", EngineTests.Text(5_000, "item"))
                .File("bin/Win64_Shipping_Client/OrderMiddleEarthEvent.dll", EngineTests.Random(9_000, 5));
            return r;
        }

        [Fact]
        public async Task With_linux_rules_a_letter_case_copy_of_a_release_file_is_moved_to_the_backup()
        {
            using (var h = new Harness())
            {
                EventRelease(h).WriteTo(h.Releases);
                // On disk: an old copy under another letter case, and a file of the player's own.
                var folder = Path.Combine(h.Modules, "LotRMPEvent");
                Directory.CreateDirectory(Path.Combine(folder, "moduledata"));
                File.WriteAllText(Path.Combine(folder, "moduledata", "items.xml"), "old copy");
                File.WriteAllText(Path.Combine(folder, "moduledata", "my-notes.txt"), "mine");
                var s = h.Service();
                s.Planner.PathComparer = StringComparer.Ordinal; // what OsPath.Comparer is on Linux
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.Null(c.Error);
                var mp = c.Plan.Modules.Single();
                var remove = mp.Actions.Where(a => a.Kind == FileActionKind.Remove).Select(a => a.RelPath).ToList();
                Assert.Equal(new[] { "moduledata/items.xml" }, remove);
                Assert.Contains("moduledata/my-notes.txt", mp.Extra);
                Assert.DoesNotContain("moduledata/items.xml", mp.Extra);
                var info = Assert.Single(c.Plan.Issues, i => i.Text.Contains("different letter case"));
                Assert.Equal(IssueSeverity.Info, info.Severity);
                Assert.False(PlayerErrors.LooksTechnical(info.Text));
                // NTFS cannot hold both names, so the plan is checked, not applied (the swap itself is the tested engine).
            }
        }

        [Fact]
        public async Task With_windows_rules_letter_case_never_makes_a_copy()
        {
            using (var h = new Harness())
            {
                EventRelease(h).WriteTo(h.Releases);
                var folder = Path.Combine(h.Modules, "LotRMPEvent");
                Directory.CreateDirectory(Path.Combine(folder, "moduledata"));
                File.WriteAllText(Path.Combine(folder, "moduledata", "items.xml"), "old copy");
                var s = h.Service();
                s.Planner.PathComparer = StringComparer.OrdinalIgnoreCase;
                var c = await s.CheckAsync(null, CancellationToken.None);
                Assert.DoesNotContain(c.Plan.Modules.Single().Actions, a => a.Kind == FileActionKind.Remove);
                Assert.DoesNotContain(c.Plan.Issues, i => i.Text.Contains("letter case"));
            }
        }

        [Fact]
        public void The_verifier_lists_a_letter_case_copy_as_extra_with_exact_comparison()
        {
            var root = Path.Combine(Path.GetTempPath(), "lotr-case-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "moduledata"));
                File.WriteAllText(Path.Combine(root, "moduledata", "a.xml"), "x");
                var module = new ManifestModule { Id = "M", Folder = "M", Files = { new ManifestFile { Path = "ModuleData/a.xml", Size = 1, Sha256 = new string('0', 64) } } };
                Assert.Contains("moduledata/a.xml", InstalledModuleVerifier.Verify(root, module, InstallCheckMode.SizeOnly, pathComparer: StringComparer.Ordinal).Extra);
                Assert.Empty(InstalledModuleVerifier.Verify(root, module, InstallCheckMode.SizeOnly, pathComparer: StringComparer.OrdinalIgnoreCase).Extra);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        // ------------------------------------------------------------------ running game (/proc)

        [Theory]
        [InlineData("Bannerlord.exe", @"Z:\home\deck\.local\share\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe", "Bannerlord.exe")]
        [InlineData("Bannerlord.exe", null, "Bannerlord.exe")]
        [InlineData(null, @"C:\Games\bin\Win64_Shipping_Client\Bannerlord.Native.exe", "Bannerlord.Native.exe")]
        [InlineData("TaleWorlds.Moun", null, "TaleWorlds.MountAndBlade.Launcher.exe")] // the kernel's 15-character cut
        [InlineData("bannerlord", "/usr/bin/bannerlord", "Bannerlord.exe")]
        [InlineData("wine64-preload", "/home/deck/.steam/root/compatibilitytools.d/proton/files/bin/wine64-preloader", null)]
        [InlineData("python3", "python3", null)]
        [InlineData("lotr-updater", "./lotr-updater", null)]
        [InlineData("Bannerlord.exeX", null, null)]
        public void Wine_game_processes_are_recognised(string comm, string argv0, string expected)
        {
            Assert.Equal(expected, LinuxProcessProbe.Match(comm, argv0));
        }

        [Fact]
        public void The_proc_scan_finds_the_game_and_skips_everything_else()
        {
            var proc = Path.Combine(Path.GetTempPath(), "lotr-proc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                void P(string pid, string comm, string cmdline)
                {
                    var d = Path.Combine(proc, pid);
                    Directory.CreateDirectory(d);
                    if (comm != null) File.WriteAllText(Path.Combine(d, "comm"), comm + "\n");
                    if (cmdline != null) File.WriteAllBytes(Path.Combine(d, "cmdline"), Encoding.UTF8.GetBytes(cmdline));
                }
                P("4242", "Bannerlord.exe", "Z:\\home\\deck\\Bannerlord.exe\0-multiplayer\0");
                P("77", "bash", "/bin/bash\0");
                P("2", "kthreadd", "");
                P("self", "Bannerlord.exe", "x\0");    // not a pid
                P("990", null, null);                  // vanished while scanning
                var found = new LinuxProcessProbe(proc).RunningGameProcesses();
                var g = Assert.Single(found);
                Assert.Equal(4242, g.Pid);
                Assert.Equal("Bannerlord.exe", g.Name);
                Assert.Empty(new LinuxProcessProbe(Path.Combine(proc, "missing")).RunningGameProcesses());
            }
            finally { try { Directory.Delete(proc, true); } catch { } }
        }

        [Fact]
        public void The_first_argument_of_a_cmdline_is_read_up_to_the_first_nul()
        {
            Assert.Equal("Z:\\a\\Bannerlord.exe", LinuxProcessProbe.FirstArgument(Encoding.UTF8.GetBytes("Z:\\a\\Bannerlord.exe\0--x\0")));
            Assert.Equal("steam", LinuxProcessProbe.FirstArgument(Encoding.UTF8.GetBytes("steam")));
            Assert.Null(LinuxProcessProbe.FirstArgument(new byte[0]));
            Assert.Null(LinuxProcessProbe.FirstArgument(new byte[] { 0, 1 }));
        }
    }
}
