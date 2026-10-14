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

        /// <summary>GOG product id (gogdb.org/product/1564781494).</summary>
        public const string GogProductId = "1564781494";

        /// <summary>
        /// Epic AppName. Community reports name both the Epic install folder and the Legendary/Heroic id "Chickadee";
        /// not confirmed from an official source, so the Epic source also matches on DisplayName / executable.
        /// </summary>
        public const string EpicAppName = "Chickadee";

        /// <summary>Process names (without .exe) whose main module lives in bin\&lt;platform&gt;.</summary>
        public static readonly string[] ProcessNames =
        {
            "Bannerlord",
            "Bannerlord.Native",
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
