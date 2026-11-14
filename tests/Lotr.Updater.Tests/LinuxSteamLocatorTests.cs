using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LotR.GameDetection;
using Lotr.Updater.Core.Game.Linux;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    /// <summary>
    /// Bannerlord on Linux / Steam Deck (docs/LINUX.md): simulated home folders in a temp directory (native Steam, Flatpak, Snap,
    /// several libraries, both libraryfolders.vdf formats, an SD card, symbolic links, letter-case variants, a broken VDF).
    /// Nothing outside the temp directory is read.
    /// </summary>
    public sealed class LinuxSteamLocatorTests : IDisposable
    {
        private const string GameDir = "Mount & Blade II Bannerlord";
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lotr-linux-steam", Guid.NewGuid().ToString("N").Substring(0, 10));

        private string Home => Path.Combine(_root, "home", "deck");
        private string NativeSteam => Path.Combine(Home, ".local", "share", "Steam");

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private LinuxSteamLocator Locator(IEnumerable<string> mounts = null, Func<string, string> env = null, Func<string, string> resolve = null) =>
            new LinuxSteamLocator(Home, env ?? (_ => null), mounts ?? new string[0], resolveLinks: resolve);

        /// <summary>A fake game in &lt;library&gt;/steamapps/common/&lt;folder&gt; (+ appmanifest when an installdir is given).</summary>
        private static string Game(string library, string folder = GameDir, string appManifestInstallDir = null, string steamapps = "steamapps", string version = "v1.2.12")
        {
            var root = FakeGame.Create(Path.Combine(library, steamapps, "common", folder), version);
            if (appManifestInstallDir != null)
                File.WriteAllText(Path.Combine(library, steamapps, "appmanifest_261550.acf"),
                    "\"AppState\"\n{\n\t\"appid\"\t\t\"261550\"\n\t\"name\"\t\t\"Mount & Blade II: Bannerlord\"\n\t\"installdir\"\t\t\"" + appManifestInstallDir + "\"\n}\n");
            return root;
        }

        private static string Vdf(params string[] libraries)
        {
            var s = "\"libraryfolders\"\n{\n";
            for (int i = 0; i < libraries.Length; i++)
                s += $"\t\"{i}\"\n\t{{\n\t\t\"path\"\t\t\"{libraries[i].Replace("\\", "\\\\")}\"\n\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{{\n\t\t\t\"261550\"\t\t\"52000000000\"\n\t\t}}\n\t}}\n";
            return s + "}\n";
        }

        private static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        [Fact]
        public void Native_steam_in_the_home_folder_is_found()
        {
            var game = Game(NativeSteam);
            Write(Path.Combine(NativeSteam, "steamapps", "libraryfolders.vdf"), Vdf(NativeSteam));
            var s = Locator().FindAll();
            var g = Assert.Single(s.Found);
            Assert.Equal(game, g.Root);
            Assert.Equal(Path.Combine(game, "Modules"), g.ModulesPath);
            Assert.Equal(GameStore.Steam, g.Store);
            Assert.Equal("v1.2.12", g.GameVersion);
            Assert.True(g.Writable);
            Assert.Contains("Steam library", g.Evidence);
            Assert.Contains("Found Bannerlord in 1 place", s.Message);
            Assert.Contains(NativeSteam, s.ReportForLog);
        }

        [Fact]
        public void Flatpak_steam_is_found()
        {
            var steam = Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam");
            var game = Game(steam);
            Assert.Equal(game, Assert.Single(Locator().FindAll().Found).Root);
        }

        [Fact]
        public void Older_flatpak_and_snap_layouts_are_found()
        {
            var flatpakOld = Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
            var snap = Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam");
            var g1 = Game(flatpakOld);
            var g2 = Game(snap);
            var found = Locator().FindAll().Found.Select(f => f.Root).ToList();
            Assert.Contains(g1, found);
            Assert.Contains(g2, found);
        }

        [Fact]
        public void Xdg_data_home_is_respected()
        {
            var xdg = Path.Combine(_root, "xdg");
            var game = Game(Path.Combine(xdg, "Steam"));
            var s = Locator(env: n => n == "XDG_DATA_HOME" ? xdg : null).FindAll();
            Assert.Equal(game, Assert.Single(s.Found).Root);
        }

        [Fact]
        public void Every_library_of_both_vdf_files_is_searched_and_the_appmanifest_names_the_folder()
        {
            // Main Steam folder without the game; a second disk listed in steamapps/libraryfolders.vdf (current format) with
            // a renamed game folder; a third library listed only in config/libraryfolders.vdf (old format: "1" "path").
            Directory.CreateDirectory(Path.Combine(NativeSteam, "steamapps"));
            var disk2 = Path.Combine(_root, "mnt", "games", "SteamLibrary");
            var disk3 = Path.Combine(_root, "data", "Steam2");
            var g2 = Game(disk2, folder: "Bannerlord Custom", appManifestInstallDir: "Bannerlord Custom");
            var g3 = Game(disk3);
            Write(Path.Combine(NativeSteam, "steamapps", "libraryfolders.vdf"), Vdf(NativeSteam, disk2));
            Write(Path.Combine(NativeSteam, "config", "libraryfolders.vdf"),
                "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1700000000\"\n\t\"ContentStatsID\"\t\t\"-123\"\n\t\"1\"\t\t\"" + disk3.Replace("\\", "\\\\") + "\"\n}\n");
            var s = Locator().FindAll();
            Assert.Equal(new[] { g2, g3 }.OrderBy(x => x), s.Found.Select(f => f.Root).OrderBy(x => x));
            Assert.Contains("libraryfolders.vdf", s.Found.First(f => f.Root == g2).Evidence);
        }

        [Fact]
        public void A_library_on_an_sd_card_is_found_even_when_steam_does_not_list_it()
        {
            var media = Path.Combine(_root, "run", "media");
            var sd = Path.Combine(media, "deck", "SDCARD");
            var game = Game(sd);
            var s = Locator(mounts: new[] { media }).FindAll();
            var g = Assert.Single(s.Found);
            Assert.Equal(game, g.Root);
            Assert.Contains("drive", g.Evidence);
        }

        [Fact]
        public void A_steam_library_folder_on_a_second_drive_is_found()
        {
            var media = Path.Combine(_root, "media");
            var game = Game(Path.Combine(media, "deck", "Games", "SteamLibrary"));
            Assert.Equal(game, Assert.Single(Locator(mounts: new[] { media }).FindAll().Found).Root);
        }

        [Fact]
        public void Steam_links_to_the_same_folder_count_once()
        {
            // ~/.steam/steam and ~/.steam/root are symbolic links to ~/.local/share/Steam on a normal install.
            var game = Game(NativeSteam);
            Directory.CreateDirectory(Path.Combine(Home, ".steam", "steam"));
            Directory.CreateDirectory(Path.Combine(Home, ".steam", "root"));
            Write(Path.Combine(NativeSteam, "steamapps", "libraryfolders.vdf"), Vdf(Path.Combine(Home, ".steam", "steam")));
            string Resolve(string p)
            {
                var full = Path.GetFullPath(p).TrimEnd('\\', '/');
                foreach (var link in new[] { Path.Combine(Home, ".steam", "steam"), Path.Combine(Home, ".steam", "root") })
                    if (full.StartsWith(link, StringComparison.OrdinalIgnoreCase)) return NativeSteam + full.Substring(link.Length);
                return full;
            }
            var s = Locator(resolve: Resolve).FindAll();
            Assert.Equal(game, Assert.Single(s.Found).Root);
            Assert.Contains("already listed", s.ReportForLog);
        }

        [Fact]
        public void A_broken_vdf_is_skipped_and_the_steam_folder_itself_is_still_searched()
        {
            var game = Game(NativeSteam);
            Write(Path.Combine(NativeSteam, "steamapps", "libraryfolders.vdf"), "{{{ \"path\" \"unterminated");
            Write(Path.Combine(NativeSteam, "config", "libraryfolders.vdf"), "\0\0garbageÿ");
            Assert.Equal(game, Assert.Single(Locator().FindAll().Found).Root);
        }

        [Fact]
        public void Letter_case_variants_of_steam_folders_are_accepted_with_their_real_names()
        {
            // Old Linux Steam installs used "SteamApps"; a game copied by hand may have a lower-case folder name.
            var library = NativeSteam;
            var root = FakeGame.Create(Path.Combine(library, "SteamApps", "common", "mount & blade ii bannerlord"));
            var s = Locator().FindAll();
            var g = Assert.Single(s.Found);
            Assert.EndsWith(Path.Combine("SteamApps", "common", "mount & blade ii bannerlord"), g.Root, StringComparison.Ordinal);
            Assert.Equal(root, g.Root);
        }

        [Fact]
        public void An_appmanifest_pointing_to_a_missing_folder_falls_back_to_the_default_name()
        {
            var game = Game(NativeSteam, appManifestInstallDir: "Gone Folder");
            Assert.Equal(game, Assert.Single(Locator().FindAll().Found).Root);
        }

        [Fact]
        public void A_folder_that_is_not_the_game_is_not_taken()
        {
            var fake = Path.Combine(NativeSteam, "steamapps", "common", GameDir);
            Directory.CreateDirectory(Path.Combine(fake, "Modules", "Native")); // no exe, no SubModule.xml
            var s = Locator().FindAll();
            Assert.Empty(s.Found);
            Assert.Contains("--game", s.Message);
        }

        [Fact]
        public void No_steam_at_all_says_how_to_point_to_the_game()
        {
            Directory.CreateDirectory(Home);
            var s = Locator().FindAll();
            Assert.Empty(s.Found);
            Assert.Contains("--game", s.Message);
            Assert.Contains("no Steam folder", s.ReportForLog);
        }

        [Fact]
        public void The_proton_prefix_and_the_launcher_mod_list_are_reported()
        {
            var game = Game(NativeSteam);
            var pfx = Path.Combine(NativeSteam, "steamapps", "compatdata", "261550", "pfx");
            var launcherData = Path.Combine(pfx, "drive_c", "users", "steamuser", "Documents", "Mount and Blade II Bannerlord", "Configs", "LauncherData.xml");
            Write(launcherData, "<UserData/>");
            var g = Assert.Single(Locator().FindAll().Found);
            Assert.Equal(pfx, g.ProtonPrefix);
            Assert.Equal(launcherData, g.LauncherDataPath);
        }

        [Fact]
        public void The_proton_prefix_may_live_in_the_main_steam_folder()
        {
            Directory.CreateDirectory(Path.Combine(NativeSteam, "steamapps"));
            var disk2 = Path.Combine(_root, "disk2");
            Game(disk2);
            Write(Path.Combine(NativeSteam, "steamapps", "libraryfolders.vdf"), Vdf(NativeSteam, disk2));
            var pfx = Path.Combine(NativeSteam, "steamapps", "compatdata", "261550", "pfx");
            Directory.CreateDirectory(pfx);
            Assert.Equal(pfx, Assert.Single(Locator().FindAll().Found).ProtonPrefix);
        }

        [Fact]
        public void A_chosen_folder_is_accepted_from_the_exe_the_bin_folder_modules_or_the_root()
        {
            var game = Game(NativeSteam);
            var loc = Locator();
            foreach (var p in new[]
            {
                game,
                Path.Combine(game, "Modules"),
                Path.Combine(game, "Modules", "Native"),
                Path.Combine(game, "bin", "Win64_Shipping_Client"),
                Path.Combine(game, "bin", "Win64_Shipping_Client", "Bannerlord.exe"),
            })
            {
                var s = loc.Validate(p);
                var g = Assert.Single(s.Found);
                Assert.Equal(game, g.Root);
                Assert.Equal(GameStore.Steam, g.Store);
            }
        }

        [Fact]
        public void A_chosen_folder_accepts_a_home_relative_path()
        {
            var game = Game(Path.Combine(Home, "Games"), folder: "Bannerlord");
            File.Delete(Path.Combine(game, "Modules", "Native", "steam.target"));
            File.WriteAllText(Path.Combine(game, "Modules", "Native", "gog.target"), "");
            var g = Assert.Single(Locator().Validate("~/Games/steamapps/common/Bannerlord").Found);
            Assert.Equal(game, g.Root);
            Assert.Equal(GameStore.Steam, g.Store); // it sits in a steamapps/common folder
            var other = FakeGame.Create(Path.Combine(_root, "heroic", "Bannerlord"));
            File.Delete(Path.Combine(other, "Modules", "Native", "steam.target"));
            File.WriteAllText(Path.Combine(other, "Modules", "Native", "gog.target"), "");
            Assert.Equal(GameStore.Gog, Assert.Single(Locator().Validate(other).Found).Store);
        }

        [Fact]
        public void A_wrong_chosen_folder_explains_what_to_pick()
        {
            Directory.CreateDirectory(Path.Combine(_root, "empty"));
            var s = Locator().Validate(Path.Combine(_root, "empty"));
            Assert.Empty(s.Found);
            Assert.Contains("bin and Modules", s.Message);
            Assert.Contains("does not exist", Locator().Validate(Path.Combine(_root, "nowhere")).Message);
        }

        [Fact]
        public void The_steam_root_candidates_cover_every_known_install()
        {
            var c = Locator(env: n => n == "XDG_DATA_HOME" ? "/xdg" : null).SteamRootCandidates();
            string[] expected =
            {
                Path.Combine("/xdg", "Steam"),
                Path.Combine(Home, ".local", "share", "Steam"),
                Path.Combine(Home, ".steam", "steam"),
                Path.Combine(Home, ".steam", "root"),
                Path.Combine(Home, ".steam", "debian-installation"),
                Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
                Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
                Path.Combine(Home, "snap", "steam", "common", ".local", "share", "Steam"),
            };
            Assert.Equal(expected, c);
        }
    }
}
