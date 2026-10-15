using System.Collections.Generic;
using System.Linq;
using LotR.GameDetection.Parsing;
using LotR.GameDetection.Sources;
using LotR.GameDetection.Tests.Bench;
using Xunit;

namespace LotR.GameDetection.Tests
{
    public class ParserTests
    {
        [Theory]
        [InlineData(@"C:\Games\", @"C:\Games")]
        [InlineData(@"c:/games//bannerlord/", @"C:\games\bannerlord")]
        [InlineData(@"""D:\A B\C""", @"D:\A B\C")]
        [InlineData(@"\\?\D:\Very\Long", @"D:\Very\Long")]
        [InlineData(@"D:\A\.\B\..\C", @"D:\A\C")]
        [InlineData(@"D:\..\..", @"D:\")]
        [InlineData(@"d:", @"D:\")]
        [InlineData(@"\\server\share\x\..\..", @"\\server\share")]
        [InlineData(@"\\?\UNC\server\share\dir", @"\\server\share\dir")]
        [InlineData("   ", null)]
        public void PathUtil_Normalize(string input, string expected) => Assert.Equal(expected, PathUtil.Normalize(input));

        [Fact]
        public void PathUtil_helpers()
        {
            Assert.Null(PathUtil.GetParent(@"C:\"));
            Assert.Equal(@"C:\", PathUtil.GetParent(@"C:\Games"));
            Assert.Null(PathUtil.GetParent(@"\\server\share"));
            Assert.True(PathUtil.IsUnder(@"C:\A\B", @"c:\a"));
            Assert.False(PathUtil.IsUnder(@"C:\AB", @"C:\A"));
            Assert.True(PathUtil.ContainsSegment(@"C:\Program Files\WindowsApps\X", "windowsapps"));
            Assert.Equal(@"\\?\C:\x", PathUtil.ToExtended(@"C:\x"));
            Assert.Equal(@"\\?\UNC\srv\sh\x", PathUtil.ToExtended(@"\\srv\sh\x"));
            Assert.Equal(@"D:\a\b\c", PathUtil.Combine(@"D:\a\", @"\b", "c"));
        }

        [Fact]
        public void LibraryFolders_new_format()
        {
            const string vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"228980\"\t\t\"1\"\n\t\t}\n\t}\n"
                               + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"apps\"\n\t\t{\n\t\t\t\"261550\"\t\t\"5\"\n\t\t}\n\t}\n}\n";
            var libs = SteamSource.ParseLibraryFolders(vdf, out var withGame);
            Assert.Equal(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }, libs);
            Assert.Equal(new[] { @"D:\SteamLibrary" }, withGame.ToArray());
        }

        [Fact]
        public void LibraryFolders_pre_2021_format()
        {
            const string vdf = "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1600000000\"\n\t\"ContentStatsID\"\t\t\"-123\"\n\t\"1\"\t\t\"E:\\\\Steam Games\"\n}\n";
            Assert.Equal(new[] { @"E:\Steam Games" }, SteamSource.ParseLibraryFolders(vdf, out _));
        }

        [Fact]
        public void Acf_installdir_and_comments()
        {
            var node = Vdf.Parse("// comment\n\"AppState\"\n{\n\t\"appid\" \"261550\"\n\t\"installdir\"\t\"Mount & Blade II Bannerlord\"\n\t\"UserConfig\" { \"language\" \"french\" }\n}");
            Assert.Equal("Mount & Blade II Bannerlord", node["appstate"].GetString("INSTALLDIR"));
            Assert.Equal("french", node["AppState"]["UserConfig"].GetString("language"));
        }

        [Fact]
        public void GamingRoot_real_bytes_from_a_PC()
        {
            // bytes of C:\.GamingRoot on the maintainer's PC (Xbox app, default folder)
            var bytes = new byte[] { 0x52, 0x47, 0x42, 0x58, 0x01, 0, 0, 0, 0x58, 0, 0x62, 0, 0x6f, 0, 0x78, 0, 0x47, 0, 0x61, 0, 0x6d, 0, 0x65, 0, 0x73, 0, 0, 0 };
            Assert.Equal(new[] { "XboxGames" }, GamingRootFile.Parse(bytes));
            Assert.Equal(bytes, GamingRootFile.Build("XboxGames"));
            Assert.Empty(GamingRootFile.Parse(new byte[] { 1, 2, 3 }));
            Assert.Equal(new[] { "A", "Jeux Xbox" }, GamingRootFile.Parse(GamingRootFile.Build("A", "Jeux Xbox")));
        }

        [Fact]
        public void Version_files()
        {
            Assert.Equal("v1.4.8", VersionFiles.ReadGameVersionXml("<Version>\n\t<Singleplayer Value=\"v1.4.8\"/>\n</Version>"));
            Assert.Equal("v1.2.12", VersionFiles.ReadGameVersionXml("\uFEFF<Version><Multiplayer Value=\"v1.2.12\"/></Version>"));
            Assert.Null(VersionFiles.ReadGameVersionXml("not xml"));
            Assert.Equal("v1.4.8", VersionFiles.ReadSubModuleVersion("<?xml version='1.0' encoding='utf-8'?><Module><Version value = \"v1.4.8\"/></Module>"));
            // no DTD / external entities
            Assert.Null(VersionFiles.ReadGameVersionXml("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><Version><Singleplayer Value=\"&e;\"/></Version>"));
        }

        [Fact]
        public void Url_shortcut_and_store_urls()
        {
            Assert.Equal("steam://rungameid/261550", UrlShortcutFile.ReadUrl("[InternetShortcut]\r\nIDList=\r\nURL=steam://rungameid/261550\r\n"));
            Assert.Equal(GameStore.Steam, ShortcutSource.StoreFromUrl("steam://rungameid/261550", "x.url"));
            Assert.Equal(GameStore.Unknown, ShortcutSource.StoreFromUrl("steam://rungameid/2615500", "x.url"));
            Assert.Equal(GameStore.Epic, ShortcutSource.StoreFromUrl("com.epicgames.launcher://apps/ns%3Aid%3AChickadee?action=launch", "x.url"));
            Assert.Equal(GameStore.Epic, ShortcutSource.StoreFromUrl("com.epicgames.launcher://apps/abc?action=launch", "Mount & Blade II Bannerlord.url"));
            Assert.Equal(GameStore.Unknown, ShortcutSource.StoreFromUrl("com.epicgames.launcher://apps/Fortnite?action=launch", "Fortnite.url"));
            Assert.Equal(GameStore.Gog, ShortcutSource.StoreFromUrl("goggalaxy://openGameView/1564781494", "x.url"));
            Assert.Equal(GameStore.Steam, ShortcutSource.StoreFromLauncherShortcut("steam.exe", "-applaunch 261550 -nosplash"));
            Assert.Equal(GameStore.Gog, ShortcutSource.StoreFromLauncherShortcut("GalaxyClient.exe", "/command=runGame /gameId=1564781494"));
            Assert.Equal(GameStore.Unknown, ShortcutSource.StoreFromLauncherShortcut("steam.exe", "-applaunch 730"));
        }

        [Fact]
        public void Lnk_round_trip()
        {
            var t = LnkShortcutResolverParse(LnkBuilder.Build(@"D:\Jeux é\Bannerlord.exe", workingDir: @"D:\Jeux é", arguments: "-x"));
            Assert.Equal(@"D:\Jeux é\Bannerlord.exe", t.TargetPath);
            Assert.Equal(@"D:\Jeux é", t.WorkingDirectory);
            Assert.Equal("-x", t.Arguments);
            Assert.NotNull(LnkShortcutResolverParse(new byte[10]).Error);
        }

        [Fact]
        public void Lnk_relative_path_only()
        {
            var m = new FakeMachine();
            m.Fs.AddFile(@"D:\G\link.lnk", LnkBuilder.Build(null, withLinkInfo: false, relativePath: @".\bin\Win64_Shipping_Client\Bannerlord.exe"));
            var t = new LnkShortcutResolver(m.Fs, m.Env).Resolve(@"D:\G\link.lnk");
            Assert.Equal(@"D:\G\bin\Win64_Shipping_Client\Bannerlord.exe", t.TargetPath);
        }

        private static Abstractions.ShortcutTarget LnkShortcutResolverParse(byte[] b) => LnkShortcutResolver.Parse(b);

        [Fact]
        public void MiniJson_parses_epic_manifest()
        {
            var o = MiniJson.Parse("{\"AppName\":\"Chickadee\",\"InstallLocation\":\"C:\\\\Epic\\\\MB2\",\"n\":1.5,\"b\":true,\"x\":null,\"a\":[1,\"\\u00e9\"]}");
            Assert.Equal("Chickadee", MiniJson.GetString(o, "appname"));
            Assert.Equal(@"C:\Epic\MB2", MiniJson.GetString(o, "InstallLocation"));
            Assert.Equal("é", ((List<object>)((Dictionary<string, object>)o)["a"])[1]);
            Assert.Throws<System.FormatException>(() => MiniJson.Parse("{\"a\":}"));
        }

        [Fact]
        public void Report_json_is_valid_and_masks_the_user_name()
        {
            var m = new FakeMachine();
            m.AddGame(@"C:\Users\" + System.Environment.UserName + @"\Games\Bannerlord");
            var r = m.Detector.ValidateManualSelection(@"C:\Users\" + System.Environment.UserName + @"\Games\Bannerlord");
            var json = ReportJson.Serialize(r, maskUserName: true, toolVersion: "test");
            var parsed = (Dictionary<string, object>)MiniJson.Parse(json);
            Assert.Equal(true, parsed["found"]);
            Assert.Contains(@"C:\\Users\\<user>\\Games", json);
            Assert.DoesNotContain(@"\\" + System.Environment.UserName + @"\\", json);
            var inst = (Dictionary<string, object>)((List<object>)parsed["installations"])[0];
            Assert.Equal(@"C:\Users\<user>\Games\Bannerlord\Modules", inst["modulesPath"]);
        }

        [Fact]
        public void Mask_only_replaces_a_whole_segment()
        {
            Assert.Equal(@"C:\Users\<user>\x", ReportJson.Mask(@"C:\Users\bob\x", "bob"));
            Assert.Equal(@"C:\Users\bobby\x", ReportJson.Mask(@"C:\Users\bobby\x", "bob"));
        }

        [Fact]
        public void Name_matching()
        {
            Assert.True(BannerlordConstants.MatchesName("Mount & Blade II- Bannerlord"));
            Assert.True(BannerlordConstants.MatchesName("TaleWorldsEntertainment.MountBladeIIBannerlord_1.0.0.0_x64__abc"));
            Assert.False(BannerlordConstants.MatchesName("Halo- Campaign Evolved"));
            Assert.True(UninstallRegistrySource.IsGameDisplayName("Mount & Blade II: Bannerlord"));
            Assert.False(UninstallRegistrySource.IsGameDisplayName("Mount & Blade II: Bannerlord - Modding Kit"));
        }
    }
}

namespace LotR.GameDetection.Tests
{
    public class GameLocatorContractTests
    {
        [Xunit.Fact]
        public void Facade_exposes_the_contract_fields()
        {
            var m = new Bench.FakeMachine().InstallSteam();
            m.AddSteamLibrary(@"D:\SteamLibrary", game: true);
            var r = GameLocator.FindAll(m.Access);
            Xunit.Assert.True(r.Found);
            var g = Xunit.Assert.Single(r.Installs);
            Xunit.Assert.Equal(@"D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord", g.Root);
            Xunit.Assert.Equal(g.Root + @"\Modules", g.ModulesPath);
            Xunit.Assert.Equal(GameStore.Steam, g.Store);
            Xunit.Assert.Equal("v1.4.8", g.GameVersion);
            Xunit.Assert.True(g.Writable);
            Xunit.Assert.Equal(DiscoverySource.Steam, g.Source);

            var bad = GameLocator.Validate(m.Access, @"D:\SteamLibrary");
            Xunit.Assert.False(bad.Found);
            Xunit.Assert.Contains("not found", bad.Message);
        }
    }
}
