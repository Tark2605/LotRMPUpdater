using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LotR.GameDetection.Abstractions;
using LotR.GameDetection.Parsing;

namespace LotR.GameDetection.Tests.Bench
{
    /// <summary>
    /// A simulated Windows PC: in-memory disks, registry, processes and known folders, plus helpers that lay out
    /// Bannerlord the way each store does. Every scenario of the test bench is built from these helpers.
    /// </summary>
    public sealed class FakeMachine
    {
        public const string UserName = "Player";
        public const string SteamDefault = @"C:\Program Files (x86)\Steam";
        public const string GameFolderName = "Mount & Blade II Bannerlord";
        public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        private readonly List<KeyValuePair<string, bool>> _steamLibraries = new List<KeyValuePair<string, bool>>();
        private string _steamRoot;

        public FakeMachine()
        {
            Fs.AddDirectory(@"C:\Windows\System32");
            Fs.AddDirectory(@"C:\Users\" + UserName + @"\Desktop");
            Fs.AddDirectory(@"C:\Users\Public\Desktop");
            Env.Folders[KnownFolderKind.UserDesktop] = @"C:\Users\" + UserName + @"\Desktop";
            Env.Folders[KnownFolderKind.CommonDesktop] = @"C:\Users\Public\Desktop";
            Env.Folders[KnownFolderKind.UserStartMenu] = @"C:\Users\" + UserName + @"\AppData\Roaming\Microsoft\Windows\Start Menu";
            Env.Folders[KnownFolderKind.CommonStartMenu] = @"C:\ProgramData\Microsoft\Windows\Start Menu";
            Env.Folders[KnownFolderKind.ProgramData] = @"C:\ProgramData";
            Env.Folders[KnownFolderKind.ProgramFiles] = @"C:\Program Files";
            Env.Folders[KnownFolderKind.ProgramFilesX86] = @"C:\Program Files (x86)";
            Env.Folders[KnownFolderKind.LocalAppData] = @"C:\Users\" + UserName + @"\AppData\Local";
            Env.Folders[KnownFolderKind.Documents] = @"C:\Users\" + UserName + @"\Documents";
            Env.Variables["ProgramFiles"] = @"C:\Program Files";
            Env.Variables["ProgramFiles(x86)"] = @"C:\Program Files (x86)";
            Env.Variables["USERPROFILE"] = @"C:\Users\" + UserName;
        }

        public FakeFileSystem Fs { get; } = new FakeFileSystem();
        public FakeRegistry Registry { get; } = new FakeRegistry();
        public FakeProcessList Processes { get; } = new FakeProcessList();
        public FakeEnvironment Env { get; } = new FakeEnvironment();

        public SystemAccess Access => new SystemAccess(Fs, Registry, Processes, Env, new LnkShortcutResolver(Fs, Env));

        public GameDetector Detector => new GameDetector(Access);

        public DetectionReport Detect(DetectionOptions options = null) => Detector.Detect(options);

        // ---------------- game layouts ----------------

        /// <summary>
        /// Lays out a client: &lt;root&gt;\bin\&lt;platform&gt;\Bannerlord.exe + Version.xml, Modules\Native\SubModule.xml, Modules\Multiplayer.
        /// </summary>
        public FakeMachine AddGame(string root, bool xbox = false, string version = "v1.4.8", bool native = true, bool nativeXml = true,
            string storeTarget = null, bool versionXml = true, string exeName = BannerlordConstants.GameExecutable)
        {
            var bin = PathUtil.Combine(root, "bin", xbox ? BannerlordConstants.BinXbox : BannerlordConstants.BinWin64);
            Fs.AddFile(PathUtil.Combine(bin, exeName), new byte[] { (byte)'M', (byte)'Z' });
            Fs.AddFile(PathUtil.Combine(bin, "TaleWorlds.Library.dll"), new byte[] { (byte)'M', (byte)'Z' });
            if (versionXml)
                Fs.AddFile(PathUtil.Combine(bin, "Version.xml"), "<Version>\r\n\t<Singleplayer Value=\"" + version + "\"/>\r\n</Version>");
            if (native)
            {
                var nativeDir = PathUtil.Combine(root, "Modules", "Native");
                Fs.AddDirectory(nativeDir);
                if (nativeXml)
                    Fs.AddFile(PathUtil.Combine(nativeDir, "SubModule.xml"), "\uFEFF<?xml version='1.0' encoding='utf-8'?>\r\n<Module>\r\n\t<Name value = \"Native\"/>\r\n\t<Id value = \"Native\"/>\r\n\t<Version value = \"" + version + "\"/>\r\n</Module>");
                if (storeTarget != null)
                    Fs.AddFile(PathUtil.Combine(nativeDir, storeTarget), string.Empty);
            }
            Fs.AddFile(PathUtil.Combine(root, "Modules", "Multiplayer", "SubModule.xml"), "<Module><Id value=\"Multiplayer\"/></Module>");
            return this;
        }

        /// <summary>The Bannerlord Dedicated Server layout (bin\Win64_Shipping_Server, Modules\Native).</summary>
        public FakeMachine AddDedicatedServer(string root)
        {
            Fs.AddFile(PathUtil.Combine(root, "bin", BannerlordConstants.BinServer, "DedicatedCustomServer.Starter.exe"), "MZ");
            Fs.AddFile(PathUtil.Combine(root, "Modules", "Native", "SubModule.xml"), "<Module><Version value=\"v1.4.8\"/></Module>");
            return this;
        }

        // ---------------- Steam ----------------

        /// <summary>Installs the Steam client (registry like the real one: forward slashes, lower case in HKCU).</summary>
        public FakeMachine InstallSteam(string steamRoot = SteamDefault)
        {
            _steamRoot = steamRoot;
            Fs.AddFile(PathUtil.Combine(steamRoot, "steam.exe"), "MZ");
            Registry.Set(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, @"Software\Valve\Steam", "SteamPath", steamRoot.ToLowerInvariant().Replace('\\', '/'));
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, @"SOFTWARE\Valve\Steam", "InstallPath", steamRoot);
            AddSteamLibrary(steamRoot, game: false);
            return this;
        }

        /// <summary>Adds a library to libraryfolders.vdf; with <paramref name="game"/> also the appmanifest and the game folder.</summary>
        public FakeMachine AddSteamLibrary(string library, bool game, string installDir = GameFolderName, bool layGame = true, Action<string> customizeGame = null)
        {
            _steamLibraries.RemoveAll(l => PathUtil.PathEquals(l.Key, library));
            _steamLibraries.Add(new KeyValuePair<string, bool>(library, game));
            WriteLibraryFolders();
            if (game)
            {
                var acf = "\"AppState\"\n{\n\t\"appid\"\t\t\"261550\"\n\t\"name\"\t\t\"Mount & Blade II: Bannerlord\"\n\t\"installdir\"\t\t\"" + installDir + "\"\n\t\"StateFlags\"\t\t\"4\"\n}\n";
                Fs.AddFile(PathUtil.Combine(library, "steamapps", "appmanifest_261550.acf"), acf);
                var root = SteamGameRoot(library, installDir);
                if (layGame)
                {
                    AddGame(root, storeTarget: BannerlordConstants.SteamTargetFile);
                    customizeGame?.Invoke(root);
                }
            }
            else
            {
                Fs.AddDirectory(PathUtil.Combine(library, "steamapps", "common"));
            }
            return this;
        }

        public static string SteamGameRoot(string library, string installDir = GameFolderName) => PathUtil.Combine(library, "steamapps", "common", installDir);

        /// <summary>The Steam "Apps &amp; features" entry (HKLM 64-bit view, like Steam writes it).</summary>
        public FakeMachine AddSteamUninstallEntry(string installLocation)
        {
            var key = UninstallKey + @"\" + BannerlordConstants.SteamUninstallKey;
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "DisplayName", "Mount & Blade II: Bannerlord");
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, key, "InstallLocation", installLocation);
            return this;
        }

        private void WriteLibraryFolders()
        {
            if (_steamRoot == null)
                throw new InvalidOperationException("InstallSteam first");
            var sb = new StringBuilder("\"libraryfolders\"\n{\n");
            for (int i = 0; i < _steamLibraries.Count; i++)
            {
                var lib = _steamLibraries[i];
                sb.Append("\t\"").Append(i).Append("\"\n\t{\n");
                sb.Append("\t\t\"path\"\t\t\"").Append(lib.Key.Replace(@"\", @"\\")).Append("\"\n");
                sb.Append("\t\t\"label\"\t\t\"\"\n\t\t\"apps\"\n\t\t{\n");
                if (lib.Value)
                    sb.Append("\t\t\t\"261550\"\t\t\"52000000000\"\n");
                sb.Append("\t\t\t\"228980\"\t\t\"500000\"\n\t\t}\n\t}\n");
            }
            sb.Append("}\n");
            Fs.AddFile(PathUtil.Combine(_steamRoot, "steamapps", "libraryfolders.vdf"), sb.ToString());
        }

        // ---------------- Epic ----------------

        public FakeMachine InstallEpicGame(string location, string appName = BannerlordConstants.EpicAppName, bool layGame = true, string manifestId = "4E1A0C2F9B8D4E7A")
        {
            if (layGame)
            {
                AddGame(location, storeTarget: BannerlordConstants.EpicTargetFile);
                Fs.AddDirectory(PathUtil.Combine(location, ".egstore"));
            }
            var esc = location.Replace(@"\", @"\\");
            var item = "{\n\t\"FormatVersion\": 0,\n\t\"bIsIncompleteInstall\": false,\n\t\"LaunchExecutable\": \"bin/Win64_Shipping_Client/TaleWorlds.MountAndBlade.Launcher.exe\",\n"
                       + "\t\"DisplayName\": \"" + (appName == BannerlordConstants.EpicAppName ? "Mount & Blade II: Bannerlord" : appName) + "\",\n\t\"InstallLocation\": \"" + esc + "\",\n\t\"AppName\": \"" + appName + "\",\n\t\"CatalogNamespace\": \"aeac94c7a11048758064b82f8f8d20ed\"\n}";
            Fs.AddFile(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\" + manifestId + ".item", item);
            var dat = "{\n\t\"InstallationList\": [\n\t\t{\n\t\t\t\"InstallLocation\": \"" + esc + "\",\n\t\t\t\"NamespaceId\": \"aeac94c7a11048758064b82f8f8d20ed\",\n\t\t\t\"ItemId\": \"x\",\n\t\t\t\"ArtifactId\": \"" + appName + "\",\n\t\t\t\"AppVersion\": \"1.4.8\",\n\t\t\t\"AppName\": \"" + appName + "\"\n\t\t}\n\t]\n}";
            Fs.AddFile(@"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat", dat);
            return this;
        }

        // ---------------- GOG ----------------

        public FakeMachine InstallGogGame(string path, bool layGame = true, string id = BannerlordConstants.GogProductId)
        {
            if (layGame)
            {
                AddGame(path, storeTarget: BannerlordConstants.GogTargetFile);
                Fs.AddFile(PathUtil.Combine(path, "goggame-" + id + ".info"), "{}");
            }
            var key = @"SOFTWARE\GOG.com\Games\" + id;
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, key, "gameName", "Mount & Blade II: Bannerlord");
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, key, "path", path);
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, key, "exe", PathUtil.Combine(path, @"bin\Win64_Shipping_Client\TaleWorlds.MountAndBlade.Launcher.exe"));
            var un = UninstallKey + @"\" + id + "_is1";
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, un, "DisplayName", "Mount & Blade II: Bannerlord");
            Registry.Set(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, un, "InstallLocation", path);
            return this;
        }

        // ---------------- Xbox ----------------

        /// <summary>Xbox app install: &lt;drive&gt;\.GamingRoot + &lt;drive&gt;\XboxGames\&lt;name&gt;\Content\... (the layout seen on real PCs).</summary>
        public string InstallXboxGame(string drive, string folderName = "Mount & Blade II- Bannerlord", string gamingFolder = "XboxGames", bool writeGamingRoot = true,
            string exeName = BannerlordConstants.GameExecutable)
        {
            var d = PathUtil.Normalize(drive);
            if (writeGamingRoot)
                Fs.AddFile(PathUtil.Combine(d, ".GamingRoot"), GamingRootFile.Build(gamingFolder));
            var outer = PathUtil.Combine(d, gamingFolder, folderName);
            var content = PathUtil.Combine(outer, "Content");
            AddGame(content, xbox: true, exeName: exeName);
            Fs.AddFile(PathUtil.Combine(content, "MicrosoftGame.config"), "<Game/>");
            Fs.AddFile(PathUtil.Combine(content, "appxmanifest.xml"), "<Package/>");
            Fs.AddFile(PathUtil.Combine(outer, "C7D69349-36C0-45A2-B72B-8DE96007332E.xvs"), "x");
            return content;
        }

        /// <summary>Microsoft Store install inside the protected WindowsApps folder, registered in the AppModel repository.</summary>
        public string InstallXboxWindowsApps()
        {
            var pkg = BannerlordConstants.XboxPackageName + "_1.4.8.0_x64__4ad1fy6bfmgy2";
            var root = @"C:\Program Files\WindowsApps\" + pkg;
            AddGame(root, xbox: true);
            Fs.AddFile(PathUtil.Combine(root, "appxmanifest.xml"), "<Package/>");
            Fs.DenyWrite(@"C:\Program Files\WindowsApps");
            Registry.Set(RegistryHiveKind.CurrentUser, RegistryViewKind.Default,
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\" + pkg, "PackageRootFolder", root);
            return root;
        }

        // ---------------- shortcuts ----------------

        public FakeMachine AddLnk(string lnkPath, string target, string arguments = null)
        {
            Fs.AddFile(lnkPath, LnkBuilder.Build(target, arguments: arguments));
            return this;
        }

        public FakeMachine AddUrl(string urlPath, string url)
        {
            Fs.AddFile(urlPath, "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,0\r\n[InternetShortcut]\r\nIDList=\r\nIconIndex=0\r\nURL=" + url + "\r\nIconFile=C:\\Program Files (x86)\\Steam\\steam\\games\\x.ico\r\n");
            return this;
        }

        public string Desktop(string name) => @"C:\Users\" + UserName + @"\Desktop\" + name;
        public string PublicDesktop(string name) => @"C:\Users\Public\Desktop\" + name;
        public string StartMenuPrograms(string name) => @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\" + name;

        public static DetectionOptions NoProcess()
        {
            var o = new DetectionOptions();
            o.DisabledSources.Add(DiscoverySource.RunningProcess);
            return o;
        }

        public static IEnumerable<string> Lines(DetectionReport r) =>
            r.Installations.Select(i => i.ToString()).Concat(r.Rejected.Select(x => x.ToString())).Concat(r.Hints);
    }
}
