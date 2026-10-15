using System;
using System.Linq;
using LotR.GameDetection.Abstractions;
using LotR.GameDetection.Tests.Bench;
using Xunit;

namespace LotR.GameDetection.Tests
{
    /// <summary>The test bench: one simulated PC per scenario of the step 1 brief.</summary>
    public class ScenarioTests
    {
        private const string SteamGameC = @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord";

        private static GameInstallation Single(DetectionReport r)
        {
            Assert.True(r.Found, r.Summary + " | " + string.Join(" | ", FakeMachine.Lines(r)));
            Assert.Single(r.Installations);
            return r.Installations[0];
        }

        [Fact]
        public void Steam_in_default_Program_Files_x86()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(FakeMachine.SteamDefault, game: true);
            m.AddSteamUninstallEntry(SteamGameC);

            var r = m.Detect();
            var i = Single(r);
            Assert.Equal(SteamGameC, i.RootPath);
            Assert.Equal(SteamGameC + @"\Modules", i.ModulesPath);
            Assert.Equal(SteamGameC + @"\bin\Win64_Shipping_Client\Bannerlord.exe", i.ExecutablePath);
            Assert.Equal(GameStore.Steam, i.Store);
            Assert.Equal("v1.4.8", i.GameVersion);
            Assert.EndsWith(@"bin\Win64_Shipping_Client\Version.xml", i.GameVersionSource);
            Assert.True(i.IsWritable);
            Assert.Equal(WriteProbeMode.TempFile, i.WriteCheckMode);
            Assert.Equal(DiscoverySource.Steam, i.PrimarySource);
            Assert.True(i.FoundBy(DiscoverySource.UninstallRegistry));
            Assert.Empty(i.Warnings);
            Assert.Empty(r.Rejected);
            Assert.Empty(r.SourceErrors);
            Assert.Contains("Found Bannerlord v1.4.8 (Steam)", r.Summary);
        }

        [Fact]
        public void Steam_library_on_D()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);

            var i = Single(m.Detect());
            Assert.Equal(@"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord\Modules", i.ModulesPath);
            Assert.Equal(GameStore.Steam, i.Store);
            Assert.Contains(i.Discoveries, d => d.Detail.Contains(@"library D:\SteamLibrary"));
        }

        [Fact]
        public void Two_libraries_game_in_the_second()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: false);
            m.AddSteamLibrary(@"E:\Games\Steam", game: true);

            var i = Single(m.Detect());
            Assert.Equal(@"E:\Games\Steam\steamapps\common\Mount & Blade II Bannerlord", i.RootPath);
        }

        [Fact]
        public void Steam_custom_installdir_from_appmanifest()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true, installDir: "Bannerlord Custom");

            var i = Single(m.Detect());
            Assert.Equal(@"D:\SteamLibrary\steamapps\common\Bannerlord Custom", i.RootPath);
        }

        [Fact]
        public void Epic()
        {
            var m = new FakeMachine();
            m.InstallEpicGame(@"C:\Program Files\Epic Games\MountAndBlade2");

            var i = Single(m.Detect());
            Assert.Equal(GameStore.Epic, i.Store);
            Assert.Equal(@"C:\Program Files\Epic Games\MountAndBlade2\Modules", i.ModulesPath);
            Assert.Equal(DiscoverySource.Epic, i.PrimarySource);
            Assert.Equal("v1.4.8", i.GameVersion);
            Assert.Empty(i.Warnings);
        }

        [Fact]
        public void Epic_entry_of_another_game_is_ignored_silently()
        {
            var m = new FakeMachine();
            m.InstallEpicGame(@"C:\Program Files\Epic Games\Fortnite", appName: "Fortnite", layGame: false);
            m.Fs.AddDirectory(@"C:\Program Files\Epic Games\Fortnite");

            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Empty(r.Rejected);
        }

        [Fact]
        public void Gog()
        {
            var m = new FakeMachine();
            m.InstallGogGame(@"D:\GOG Games\Mount & Blade II Bannerlord");

            var i = Single(m.Detect());
            Assert.Equal(GameStore.Gog, i.Store);
            Assert.Equal(DiscoverySource.Gog, i.PrimarySource);
            Assert.True(i.FoundBy(DiscoverySource.UninstallRegistry));
        }

        [Fact]
        public void Gog_package_id_is_also_recognised()
        {
            var m = new FakeMachine();
            m.InstallGogGame(@"D:\GOG Games\MB2", id: BannerlordConstants.GogPackageId);
            Assert.Equal(GameStore.Gog, Single(m.Detect()).Store);
        }

        [Fact]
        public void Xbox_XboxGames_on_E()
        {
            var m = new FakeMachine();
            var content = m.InstallXboxGame(@"E:\");

            var i = Single(m.Detect());
            Assert.Equal(content, i.RootPath);
            Assert.Equal(@"E:\XboxGames\Mount & Blade II- Bannerlord\Content\Modules", i.ModulesPath);
            Assert.Equal(BannerlordConstants.BinXbox, i.BinFolderName);
            Assert.Equal(GameStore.Xbox, i.Store);
            Assert.True(i.IsWritable);
            Assert.False(i.IsProtectedLocation);
            Assert.Equal(DiscoverySource.Xbox, i.PrimarySource);
        }

        [Fact]
        public void Xbox_custom_gaming_folder_read_from_GamingRoot()
        {
            var m = new FakeMachine();
            m.InstallXboxGame(@"F:\", gamingFolder: @"Jeux Xbox");
            var i = Single(m.Detect());
            Assert.StartsWith(@"F:\Jeux Xbox\", i.RootPath);
            Assert.Contains(i.Discoveries, d => d.Detail.Contains(".GamingRoot"));
        }

        [Fact]
        public void Xbox_renamed_folder_is_still_probed()
        {
            var m = new FakeMachine();
            m.InstallXboxGame(@"E:\", folderName: "MB2");
            Assert.Equal(GameStore.Xbox, Single(m.Detect()).Store);
        }

        [Fact]
        public void Xbox_build_with_only_Launcher_Native_exe()
        {
            var m = new FakeMachine();
            m.InstallXboxGame(@"E:\", exeName: BannerlordConstants.XboxFallbackExecutable);
            var i = Single(m.Detect());
            Assert.EndsWith(@"Gaming.Desktop.x64_Shipping_Client\Launcher.Native.exe", i.ExecutablePath);
            Assert.Equal(GameStore.Xbox, i.Store);
        }

        [Fact]
        public void Xbox_WindowsApps_detected_but_not_writable()
        {
            var m = new FakeMachine();
            var root = m.InstallXboxWindowsApps();

            var i = Single(m.Detect());
            Assert.Equal(root, i.RootPath);
            Assert.Equal(GameStore.Xbox, i.Store);
            Assert.True(i.IsProtectedLocation);
            Assert.False(i.IsWritable);
            Assert.Contains(i.Warnings, w => w.Contains("WindowsApps") && w.Contains("XboxGames"));
            // the generic "no write access" advice is not added on top of the WindowsApps one
            Assert.DoesNotContain(i.Warnings, w => w.Contains("ask the PC owner"));
        }

        [Fact]
        public void Game_installed_twice_Steam_and_Epic()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            m.InstallEpicGame(@"E:\Epic Games\MountAndBlade2");

            var r = m.Detect();
            Assert.Equal(2, r.Installations.Count);
            Assert.Contains(r.Installations, i => i.Store == GameStore.Steam && i.RootPath.StartsWith(@"D:\"));
            Assert.Contains(r.Installations, i => i.Store == GameStore.Epic && i.RootPath.StartsWith(@"E:\"));
            Assert.All(r.Installations, i => Assert.Contains(i.Warnings, w => w.Contains("installed 2 times")));
            Assert.Contains("Found 2 Bannerlord installations", r.Summary);
        }

        [Fact]
        public void Broken_shortcut_is_reported()
        {
            var m = new FakeMachine();
            m.AddLnk(m.Desktop("Mount & Blade II Bannerlord.lnk"), @"D:\Old Games\Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe");

            var r = m.Detect();
            Assert.False(r.Found);
            var rej = Assert.Single(r.Rejected);
            Assert.Equal(RejectionReason.ShortcutBroken, rej.Reason);
            Assert.Equal(DiscoverySource.Shortcut, rej.Source);
            Assert.Contains("no longer exists", rej.Message);
            Assert.Contains("not found", r.Summary);
            Assert.Contains("rejected", r.Summary);
        }

        [Fact]
        public void Unreadable_named_shortcut_is_reported()
        {
            var m = new FakeMachine();
            m.Fs.AddFile(m.Desktop("Bannerlord.lnk"), new byte[] { 1, 2, 3 });
            var rej = Assert.Single(m.Detect().Rejected);
            Assert.Equal(RejectionReason.ShortcutUnreadable, rej.Reason);
        }

        [Fact]
        public void Shortcut_to_renamed_folder_with_the_game_found_elsewhere()
        {
            var m = new FakeMachine();
            // a copied (store-less) install: only a shortcut knows about it
            m.AddGame(@"D:\Games\Bannerlord old", storeTarget: BannerlordConstants.SteamTargetFile);
            m.AddLnk(m.PublicDesktop("Bannerlord.lnk"), @"D:\Games\Bannerlord old\bin\Win64_Shipping_Client\Bannerlord.exe");
            Assert.True(m.Detect().Found);

            m.Fs.Rename(@"D:\Games\Bannerlord old", "Bannerlord");
            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Equal(RejectionReason.ShortcutBroken, Assert.Single(r.Rejected).Reason);

            // the opt-in drive scan finds the renamed folder
            var deep = m.Detect(new DetectionOptions { DeepScan = true });
            var i = Single(deep);
            Assert.Equal(@"D:\Games\Bannerlord", i.RootPath);
            Assert.Equal(DiscoverySource.DeepScan, i.PrimarySource);
            Assert.Equal(GameStore.Steam, i.Store); // from Modules\Native\steam.target
            Assert.Contains(deep.Rejected, x => x.Reason == RejectionReason.ShortcutBroken);

            // and so does the manual pick
            Assert.True(m.Detector.ValidateManualSelection(@"D:\Games\Bannerlord").Found);
        }

        [Fact]
        public void Spaces_accents_and_very_long_paths()
        {
            var deep = string.Join(@"\", Enumerable.Range(1, 12).Select(n => "Dossier très long numéro " + n));
            var lib = @"D:\Jeux vidéo — Été 2026\" + deep + @"\Bibliothèque Steam";
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(lib, game: true);
            m.AddLnk(m.Desktop("Mount & Blade II Bannerlord (copie).lnk"), FakeMachine.SteamGameRoot(lib) + @"\bin\Win64_Shipping_Client\Bannerlord.exe");

            var i = Single(m.Detect());
            Assert.True(i.ModulesPath.Length > 260, "path length " + i.ModulesPath.Length);
            Assert.Equal(FakeMachine.SteamGameRoot(lib) + @"\Modules", i.ModulesPath);
            Assert.True(i.FoundBy(DiscoverySource.Shortcut));
            Assert.Equal("v1.4.8", i.GameVersion);
        }

        [Fact]
        public void Registry_pointing_to_deleted_folders()
        {
            var m = new FakeMachine().InstallSteam(@"D:\Steam");
            m.AddSteamLibrary(@"D:\Steam", game: true);
            m.InstallGogGame(@"E:\GOG\Bannerlord");
            m.Fs.Delete(@"D:\Steam");
            m.Fs.Delete(@"E:\GOG\Bannerlord");

            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Contains(r.Hints, h => h.Contains("points to a missing folder"));
            var gog = Assert.Single(r.Rejected);
            Assert.Equal(RejectionReason.PathNotFound, gog.Reason);
            Assert.Equal(DiscoverySource.Gog, gog.Source);
            Assert.Equal(@"E:\GOG\Bannerlord", gog.Path);
        }

        [Fact]
        public void Modules_Native_missing_is_rejected_with_reason()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: false);
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true, layGame: false);
            var root = FakeMachine.SteamGameRoot(@"D:\SteamLibrary");
            m.AddGame(root, native: false);

            var r = m.Detect();
            Assert.False(r.Found);
            var rej = Assert.Single(r.Rejected);
            Assert.Equal(RejectionReason.NativeModuleMissing, rej.Reason);
            Assert.Contains(@"Modules\Native is missing", rej.Message);
            Assert.Contains("Verify the game files", rej.Message);

            var manual = m.Detector.ValidateManualSelection(root + @"\bin\Win64_Shipping_Client\Bannerlord.exe");
            Assert.False(manual.Found);
            Assert.Equal(RejectionReason.NativeModuleMissing, Assert.Single(manual.Rejected).Reason);
        }

        [Fact]
        public void Native_SubModule_xml_missing_is_rejected()
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\Bannerlord", nativeXml: false);
            var r = m.Detector.ValidateManualSelection(@"D:\Bannerlord");
            Assert.Equal(RejectionReason.NativeSubModuleXmlMissing, Assert.Single(r.Rejected).Reason);
        }

        [Fact]
        public void Case_variations_are_one_installation_with_real_case()
        {
            var m = new FakeMachine().InstallSteam();
            // the real folders use their own case; registry, vdf and uninstall key use others
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            m.AddSteamUninstallEntry(@"d:\STEAMLIBRARY\SteamApps\Common\MOUNT & BLADE II BANNERLORD\");
            m.AddLnk(m.Desktop("bannerlord.lnk"), @"d:/steamlibrary/STEAMAPPS/common/mount & blade ii bannerlord/BIN/win64_shipping_client/BANNERLORD.EXE");

            var r = m.Detect();
            var i = Single(r);
            Assert.Equal(@"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord", i.RootPath);
            Assert.True(i.FoundBy(DiscoverySource.Steam));
            Assert.True(i.FoundBy(DiscoverySource.UninstallRegistry));
            Assert.True(i.FoundBy(DiscoverySource.Shortcut));
        }

        [Fact]
        public void Junction_library_is_deduplicated_and_keeps_the_known_path()
        {
            var m = new FakeMachine().InstallSteam();
            m.Fs.AddDirectory(@"F:\RealLibrary");
            m.Fs.AddJunction(@"D:\SteamLibrary", @"F:\RealLibrary");
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            m.AddSteamUninstallEntry(@"F:\RealLibrary\steamapps\common\Mount & Blade II Bannerlord");

            var i = Single(m.Detect());
            Assert.Equal(@"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord", i.RootPath);
            Assert.Equal(@"F:\RealLibrary\steamapps\common\Mount & Blade II Bannerlord", i.CanonicalRootPath);
            Assert.True(i.FoundBy(DiscoverySource.UninstallRegistry));
        }

        [Fact]
        public void Short_8dot3_path_is_deduplicated()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(FakeMachine.SteamDefault, game: true);
            m.Fs.AddShortName(@"C:\PROGRA~2", @"C:\Program Files (x86)");
            m.AddSteamUninstallEntry(@"C:\PROGRA~2\Steam\steamapps\common\Mount & Blade II Bannerlord");
            var i = Single(m.Detect());
            Assert.Equal(SteamGameC, i.RootPath);
        }

        [Fact]
        public void No_game_gives_a_clear_not_found()
        {
            var m = new FakeMachine().InstallSteam();
            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Empty(r.Rejected);
            Assert.Empty(r.SourceErrors);
            Assert.StartsWith("Bannerlord was not found on this PC.", r.Summary);
            Assert.Contains("choose its folder manually", r.Summary);
        }

        [Fact]
        public void Running_process_is_the_primary_source()
        {
            var m = new FakeMachine();
            m.AddGame(@"G:\Portable\Bannerlord");
            m.Processes.Processes.Add(new RunningProcess("Bannerlord", 4242, @"G:\Portable\Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe"));
            m.Processes.Processes.Add(new RunningProcess("Bannerlord.Native", 4243, null, "access denied"));

            var r = m.Detect();
            var i = Single(r);
            Assert.Equal(DiscoverySource.RunningProcess, i.PrimarySource);
            Assert.Equal(GameStore.Unknown, i.Store);
            Assert.Contains(r.Hints, h => h.Contains("4243"));

            Assert.False(m.Detect(FakeMachine.NoProcess()).Found);
        }

        [Fact]
        public void Steam_url_shortcut_is_mapped_to_a_Steam_lookup()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            m.AddUrl(m.Desktop("Mount & Blade II Bannerlord.url"), "steam://rungameid/261550");

            var i = Single(m.Detect());
            var viaShortcut = Assert.Single(i.Discoveries, d => d.Source == DiscoverySource.Shortcut);
            Assert.Equal(GameStore.Steam, viaShortcut.StoreHint);
            Assert.Contains("steam://rungameid/261550", viaShortcut.Detail);
        }

        [Fact]
        public void Steam_url_shortcut_without_installed_game_gives_a_hint()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddUrl(m.Desktop("Mount & Blade II Bannerlord.url"), "steam://rungameid/261550");
            m.AddUrl(m.Desktop("Other game.url"), "steam://rungameid/2615500");
            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Single(r.Hints, h => h.Contains("Steam does not list an installed copy"));
        }

        [Fact]
        public void Epic_url_shortcut_is_mapped_to_an_Epic_lookup()
        {
            var m = new FakeMachine();
            m.InstallEpicGame(@"D:\Epic\MountAndBlade2");
            m.AddUrl(m.Desktop("Mount & Blade II Bannerlord.url"), "com.epicgames.launcher://apps/aeac94c7a11048758064b82f8f8d20ed%3A1234%3AChickadee?action=launch&silent=true");
            var i = Single(m.Detect());
            Assert.Contains(i.Discoveries, d => d.Source == DiscoverySource.Shortcut && d.StoreHint == GameStore.Epic);
        }

        [Fact]
        public void Steam_launcher_lnk_with_applaunch_is_mapped_to_a_Steam_lookup()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(FakeMachine.SteamDefault, game: true);
            m.AddLnk(m.StartMenuPrograms(@"Steam\Bannerlord.lnk"), FakeMachine.SteamDefault + @"\steam.exe", "-applaunch 261550");
            var i = Single(m.Detect());
            Assert.Contains(i.Discoveries, d => d.Source == DiscoverySource.Shortcut && d.StoreHint == GameStore.Steam);
        }

        [Fact]
        public void Dedicated_server_is_not_a_client()
        {
            var m = new FakeMachine();
            m.AddDedicatedServer(@"D:\Steam\steamapps\common\Mount & Blade II Dedicated Server");
            var r = m.Detector.ValidateManualSelection(@"D:\Steam\steamapps\common\Mount & Blade II Dedicated Server");
            Assert.False(r.Found);
            Assert.Equal(RejectionReason.DedicatedServer, Assert.Single(r.Rejected).Reason);
        }

        [Fact]
        public void Modding_kit_only_is_not_a_client()
        {
            var m = new FakeMachine();
            m.Fs.AddFile(@"D:\MBK\bin\Win64_Shipping_wEditor\Bannerlord.exe", "MZ");
            m.Fs.AddFile(@"D:\MBK\Modules\Native\SubModule.xml", "<Module/>");
            Assert.Equal(RejectionReason.ModdingKitOnly, Assert.Single(m.Detector.ValidateManualSelection(@"D:\MBK").Rejected).Reason);
        }

        [Theory]
        [InlineData(@"D:\G")]
        [InlineData(@"D:\G\")]
        [InlineData(@"""D:\G""")]
        [InlineData(@"D:\G\bin")]
        [InlineData(@"D:\G\bin\Win64_Shipping_Client")]
        [InlineData(@"D:\G\bin\Win64_Shipping_Client\Bannerlord.exe")]
        [InlineData(@"D:\G\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.Launcher.exe")]
        [InlineData(@"D:\G\Modules")]
        [InlineData(@"D:\G\Modules\Native")]
        [InlineData(@"d:/g/modules/native/SubModule.xml")]
        public void Manual_pick_accepts_any_folder_of_the_game(string pick)
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\G");
            m.Fs.AddFile(@"D:\G\bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.Launcher.exe", "MZ");
            var r = m.Detector.ValidateManualSelection(pick);
            var i = Single(r);
            Assert.Equal(@"D:\G\Modules", i.ModulesPath);
            Assert.Equal(DiscoverySource.Manual, i.PrimarySource);
        }

        [Fact]
        public void Manual_pick_of_Xbox_outer_folder_uses_Content()
        {
            var m = new FakeMachine();
            var content = m.InstallXboxGame(@"E:\", writeGamingRoot: false);
            var i = Single(m.Detector.ValidateManualSelection(@"E:\XboxGames\Mount & Blade II- Bannerlord"));
            Assert.Equal(content, i.RootPath);
        }

        [Fact]
        public void Manual_pick_of_unrelated_or_missing_folder()
        {
            var m = new FakeMachine();
            m.Fs.AddDirectory(@"D:\Photos");
            Assert.Equal(RejectionReason.NoGameExecutable, Assert.Single(m.Detector.ValidateManualSelection(@"D:\Photos").Rejected).Reason);
            Assert.Equal(RejectionReason.PathNotFound, Assert.Single(m.Detector.ValidateManualSelection(@"Z:\Nope").Rejected).Reason);
            Assert.Equal(RejectionReason.PathNotFound, Assert.Single(m.Detector.ValidateManualSelection("  ").Rejected).Reason);
        }

        [Fact]
        public void Modules_not_writable_gives_a_warning_without_admin()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(FakeMachine.SteamDefault, game: true);
            m.Fs.DenyWrite(@"C:\Program Files (x86)");

            var i = Single(m.Detect());
            Assert.False(i.IsWritable);
            Assert.Contains(i.Warnings, w => w.Contains("never asks for administrator rights"));
        }

        [Fact]
        public void Write_check_modes()
        {
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(FakeMachine.SteamDefault, game: true);

            var access = Single(m.Detect(new DetectionOptions { WriteProbe = WriteProbeMode.AccessCheck }));
            Assert.True(access.IsWritable);
            Assert.Empty(m.Fs.ProbedDirectories);

            var none = Single(m.Detect(new DetectionOptions { WriteProbe = WriteProbeMode.None }));
            Assert.Null(none.IsWritable);
            Assert.Empty(m.Fs.ProbedDirectories);

            m.Fs.MakeAclUnreadable(SteamGameC);
            var unknown = Single(m.Detect(new DetectionOptions { WriteProbe = WriteProbeMode.AccessCheck }));
            Assert.Null(unknown.IsWritable);
        }

        [Fact]
        public void Store_markers_classify_an_install_no_store_lists()
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\Copies\Epic Bannerlord", storeTarget: BannerlordConstants.EpicTargetFile);
            var i = Single(m.Detector.ValidateManualSelection(@"D:\Copies\Epic Bannerlord"));
            Assert.Equal(GameStore.Epic, i.Store);
            Assert.Contains("epic.target", i.StoreEvidence);

            m.AddGame(@"D:\Copies\Plain");
            var plain = Single(m.Detector.ValidateManualSelection(@"D:\Copies\Plain"));
            Assert.Equal(GameStore.Unknown, plain.Store);
        }

        [Fact]
        public void Steam_install_with_BLSE_files_in_the_Xbox_bin_folder_stays_Steam()
        {
            // seen on the maintainer's PC: BLSE drops its exes into bin\Gaming.Desktop.x64_Shipping_Client of a Steam install
            var m = new FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\Program Files (x86)\Steam", game: true, customizeGame: root =>
                m.Fs.AddFile(root + @"\bin\Gaming.Desktop.x64_Shipping_Client\Bannerlord.BLSE.Standalone.exe", "MZ"));
            var i = Single(m.Detect());
            Assert.Equal(GameStore.Steam, i.Store);
            Assert.Equal(BannerlordConstants.BinWin64, i.BinFolderName);
        }

        [Fact]
        public void Version_falls_back_to_Native_SubModule()
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\G", versionXml: false, version: "v1.3.4");
            var i = Single(m.Detector.ValidateManualSelection(@"D:\G"));
            Assert.Equal("v1.3.4", i.GameVersion);
            Assert.EndsWith(@"Modules\Native\SubModule.xml", i.GameVersionSource);
            Assert.Contains(i.Warnings, w => w.Contains("Version.xml is missing"));
        }

        [Fact]
        public void A_failing_source_does_not_stop_the_others()
        {
            var m = new FakeMachine();
            m.InstallEpicGame(@"D:\Epic\MountAndBlade2");
            m.Registry.ThrowOnAccess = true;

            var r = m.Detect();
            Assert.True(r.Found);
            Assert.Contains(r.SourceErrors, e => e.Source == DiscoverySource.Steam);
            Assert.Contains(r.SourceErrors, e => e.Source == DiscoverySource.Gog);
        }

        [Fact]
        public void Deep_scan_is_opt_in_and_skips_removable_drives()
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\Games\Mount & Blade II Bannerlord");
            m.AddGame(@"U:\Games\Bannerlord USB");
            m.Fs.MarkDriveNotFixed(@"U:\");

            Assert.False(m.Detect().Found);
            Assert.DoesNotContain(DiscoverySource.DeepScan, m.Detect().SourcesRun);

            var r = m.Detect(new DetectionOptions { DeepScan = true });
            var i = Single(r);
            Assert.Equal(@"D:\Games\Mount & Blade II Bannerlord", i.RootPath);
        }

        [Fact]
        public void Deep_scan_respects_its_directory_budget()
        {
            var m = new FakeMachine();
            for (int n = 0; n < 50; n++)
                m.Fs.AddDirectory(@"D:\Games\Other " + n);
            m.AddGame(@"D:\Games\zz Bannerlord");
            var r = m.Detect(new DetectionOptions { DeepScan = true, DeepScanMaxDirectories = 10 });
            Assert.Contains(r.Hints, h => h.Contains("budget"));
        }

        [Fact]
        public void Uninstall_entry_of_DLC_or_modding_kit_is_not_a_claim()
        {
            var m = new FakeMachine();
            var key = FakeMachine.UninstallKey + @"\Steam App 1393600";
            m.Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "DisplayName", "Mount & Blade II: Bannerlord - Modding Kit");
            m.Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "InstallLocation", @"D:\Deleted\Kit");
            var r = m.Detect();
            Assert.False(r.Found);
            Assert.Empty(r.Rejected);
        }

        [Fact]
        public void Uninstall_entry_with_only_DisplayIcon()
        {
            var m = new FakeMachine();
            m.AddGame(@"D:\Bannerlord");
            var key = FakeMachine.UninstallKey + @"\{SOME-GUID}";
            m.Registry.Set(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, key, "DisplayName", "Mount & Blade II: Bannerlord");
            m.Registry.Set(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, key, "DisplayIcon", @"""D:\Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe"",0");
            var i = Single(m.Detect());
            Assert.Equal(DiscoverySource.UninstallRegistry, i.PrimarySource);
        }

        [Fact]
        public void Lnk_with_environment_variable_target()
        {
            var m = new FakeMachine();
            m.AddGame(@"C:\Program Files\Bannerlord");
            m.Fs.AddFile(m.Desktop("Bannerlord.lnk"), LnkBuilder.Build(null, withLinkInfo: false, envTarget: @"%ProgramFiles%\Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe"));
            Assert.Equal(DiscoverySource.Shortcut, Single(m.Detect()).PrimarySource);
        }
    }
}
