using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LotR.GameDetection;
using Lotr.Updater.Core.Game;

namespace Lotr.Updater.Platform
{
    /// <summary>Names the processes holding files open, with the Windows Restart Manager (rstrtmgr.dll). Read-only: it never
    /// asks anything to close or restart.</summary>
    public sealed class RestartManagerLockFinder : ILockingProcessFinder
    {
        public IReadOnlyList<RunningProcessInfo> WhoLocks(IReadOnlyList<string> paths)
        {
            var result = new List<RunningProcessInfo>();
            var key = Guid.NewGuid().ToString();
            if (RmStartSession(out uint handle, 0, key) != 0) return result;
            try
            {
                var files = new string[paths.Count];
                for (int i = 0; i < paths.Count; i++) files[i] = paths[i];
                if (RmRegisterResources(handle, (uint)files.Length, files, 0, null, 0, null) != 0) return result;
                uint needed = 0, count = 0, reasons = 0;
                int rc = RmGetList(handle, out needed, ref count, null, ref reasons);
                if (rc == 234 /*ERROR_MORE_DATA*/ && needed > 0)
                {
                    var infos = new RM_PROCESS_INFO[needed];
                    count = needed;
                    if (RmGetList(handle, out needed, ref count, infos, ref reasons) == 0)
                        for (int i = 0; i < count; i++)
                            result.Add(new RunningProcessInfo { Name = string.IsNullOrEmpty(infos[i].strAppName) ? "PID " + infos[i].Process.dwProcessId : infos[i].strAppName, Pid = infos[i].Process.dwProcessId });
                }
            }
            catch (Exception) { /* best effort: the error message stays generic */ }
            finally { RmEndSession(handle); }
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, RM_UNIQUE_PROCESS[] rgApplications, uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
    }

    /// <summary>Opens the OFFICIAL launcher of the player's store. Never edits launcher settings or module selection.</summary>
    public static class GameLauncher
    {
        // REVIEW-AFTER(launch): URIs to be confirmed on a real PC per store (Steam/Epic tested by BUTR tools; Xbox/GOG = launcher exe).
        public static string Describe(GameTarget g)
        {
            if (g == null) return null;
            switch (g.Store)
            {
                case GameStore.Steam: return "steam://rungameid/" + BannerlordConstants.SteamAppId;
                case GameStore.Epic: return "com.epicgames.launcher://apps/" + BannerlordConstants.EpicAppName + "?action=launch&silent=true";
                case GameStore.Xbox:
                    return Path.Combine(g.Root, "bin", BannerlordConstants.BinXbox, BannerlordConstants.XboxFallbackExecutable);
                default:
                    var launcher = Path.Combine(g.Root, "bin", BannerlordConstants.BinWin64, "TaleWorlds.MountAndBlade.Launcher.exe");
                    return File.Exists(launcher) ? launcher : Path.Combine(g.Root, "bin", g.BinFolderName ?? BannerlordConstants.BinWin64, BannerlordConstants.GameExecutable);
            }
        }

        public static void Launch(GameTarget g)
        {
            var target = Describe(g);
            if (target == null) throw new InvalidOperationException("Bannerlord was not found.");
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target);
            Process.Start(psi)?.Dispose();
        }
    }

    public static class WindowChrome
    {
        /// <summary>Dark title bar on Windows 10 2004+ / 11 (DWMWA_USE_IMMERSIVE_DARK_MODE).</summary>
        public static void UseDarkTitleBar(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
                int caption = 0x00080A0B; // COLORREF 0x00BBGGRR of #0B0A08
                DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));
            }
            catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
