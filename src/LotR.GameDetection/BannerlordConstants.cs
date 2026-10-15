using System;

namespace LotR.GameDetection
{
    /// <summary>Store identifiers and file names of Mount &amp; Blade II: Bannerlord. Sources are listed in docs/DETECTION.md.</summary>
    public static class BannerlordConstants
    {
        public const string GameExecutable = "Bannerlord.exe";

        /// <summary>Steam/Epic/GOG client binaries.</summary>
        public const string BinWin64 = "Win64_Shipping_Client";

        /// <summary>Xbox PC (Microsoft Store / Game Pass) client binaries.</summary>
        public const string BinXbox = "Gaming.Desktop.x64_Shipping_Client";

        /// <summary>Dedicated server binaries (a server folder is not a client install).</summary>
        public const string BinServer = "Win64_Shipping_Server";

        /// <summary>Modding Kit editor binaries (Steam app 1393600); not a client install on its own.</summary>
        public const string BinEditor = "Win64_Shipping_wEditor";

        public const string ModulesFolder = "Modules";
        public const string NativeModule = "Native";
        public const string SubModuleXml = "SubModule.xml";

        /// <summary>bin\&lt;platform&gt;\Version.xml: &lt;Version&gt;&lt;Singleplayer Value="v1.4.8"/&gt;&lt;/Version&gt;.</summary>
        public const string VersionXml = "Version.xml";

        public const string SteamAppId = "261550";
        public const string SteamDefaultInstallDir = "Mount & Blade II Bannerlord";
        public const string SteamUninstallKey = "Steam App 261550";

        /// <summary>GOG product id of the game (https://www.gogdb.org/product/1564781494).</summary>
        public const string GogProductId = "1564781494";

        /// <summary>GOG "package" id of the same game (https://www.gogdb.org/product/1802539526); also listed by BUTR's Vortex extension.</summary>
        public const string GogPackageId = "1802539526";

        public static readonly string[] GogIds = { GogProductId, GogPackageId };

        /// <summary>
        /// Epic AppName (catalog codename), as used by BUTR's Vortex extension (EPICAPP_ID) and by Legendary/Heroic.
        /// Not published by Epic or TaleWorlds, so the Epic source also matches on DisplayName / executable.
        /// </summary>
        public const string EpicAppName = "Chickadee";

        /// <summary>
        /// Xbox PC / Microsoft Store package name (start of the package full name), from BUTR's Vortex extension
        /// (https://github.com/BUTR/game-mount-and-blade2/blob/master/src/common.ts, XBOX_ID).
        /// </summary>
        public const string XboxPackageName = "TaleWorldsEntertainment.MountBladeIIBannerlord";

        /// <summary>
        /// Xbox builds are launched through Launcher.Native.exe (BUTR's Vortex extension, BANNERLORD_EXE_XBOX). Accepted as an
        /// anchor in bin\Gaming.Desktop.x64_Shipping_Client only, and only when Bannerlord.exe is absent there.
        /// </summary>
        public const string XboxFallbackExecutable = "Launcher.Native.exe";

        /// <summary>
        /// Store marker files TaleWorlds ships in Modules\Native, as tested by BLSE's PlatformHelper
        /// (https://github.com/BUTR/Bannerlord.BLSE/blob/master/src/Bannerlord.BLSE.Shared/Utils/PlatformHelper.cs).
        /// Xbox (GDK) builds have appxmanifest.xml in the game root instead.
        /// </summary>
        public const string SteamTargetFile = "steam.target";
        public const string GogTargetFile = "gog.target";
        public const string EpicTargetFile = "epic.target";

        public static bool IsGogId(string id)
        {
            foreach (var g in GogIds)
            {
                if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Process names (without .exe) whose main module lives in bin\&lt;platform&gt;.</summary>
        public static readonly string[] ProcessNames =
        {
            "Bannerlord",
            "Bannerlord.Native",
            "Launcher.Native",
            "TaleWorlds.MountAndBlade.Launcher",
            "Bannerlord.BLSE.Standalone",
            "Bannerlord.BLSE.Launcher",
            "Bannerlord.BLSE.LauncherEx",
        };

        /// <summary>Words that make a shortcut, uninstall entry or package name relevant.</summary>
        public static readonly string[] NameKeywords =
        {
            "Bannerlord",
            "Mount & Blade II",
            "Mount and Blade II",
            "Mount & Blade 2",
            "Mount and Blade 2",
            "MountBlade",
            "TaleWorlds",
            EpicAppName,
        };

        public static bool MatchesName(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (var k in NameKeywords)
            {
                if (text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static bool IsGameExecutableName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            foreach (var p in ProcessNames)
            {
                if (string.Equals(fileName, p + ".exe", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
