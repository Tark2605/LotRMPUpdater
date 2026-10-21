using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Platform
{
    /// <summary>
    /// "Stays installed" without an installer, a service or admin rights: the exe copies itself to
    /// %LOCALAPPDATA%\LotRMPUpdater\app\ (and restarts from there), adds Start menu and Desktop shortcuts, and registers ONE registry key, the per-user uninstall
    /// entry (HKCU\...\Uninstall\LotRMPUpdater) so it appears in Windows "Installed apps". Nothing else in the registry.
    /// </summary>
    /// <summary>.lnk files through WScript.Shell (what Explorer itself uses); no third-party code.</summary>
    public sealed class ComShortcutWriter : IShortcutWriter
    {
        public void Create(string lnkPath, string targetExe, string arguments, string description) =>
            SelfInstall.CreateShortcut(lnkPath, targetExe, arguments, description);
    }

    public static class SelfInstall
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LotRMPUpdater";
        public const string DisplayName = "LotR MP Event updater";

        public static string InstalledExe(UpdaterPaths p) => p.InstalledExe;

        /// <summary>Name of the Start menu and Desktop shortcuts.</summary>
        public const string ShortcutName = "LotR MP Event Updater";

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutName + ".lnk");

        public static string DesktopShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName + ".lnk");

        public static string CurrentExe => Assembly.GetEntryAssembly().Location;

        public static bool IsRunningInstalledCopy(UpdaterPaths p) =>
            string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe(p)), StringComparison.OrdinalIgnoreCase);

        /// <summary>Where the self-install writes: the real Start menu, Desktop and HKCU uninstall entry, or (dev run with
        /// --dev-self-install) two folders inside the test home and no registry at all.</summary>
        public sealed class Targets
        {
            public string StartMenuLnk { get; set; }
            public string DesktopLnk { get; set; }
            public bool Registry { get; set; }
            /// <summary>The Desktop shortcut is created once; a player who deletes it does not get it back at every start.</summary>
            public string DesktopDoneMarker { get; set; }
        }

        public static Targets TargetsFor(StartOptions o, UpdaterPaths p)
        {
            if (o != null && o.Dev)
            {
                var t = Path.Combine(p.Home, "test-shortcuts");
                return new Targets
                {
                    StartMenuLnk = Path.Combine(t, "Start Menu", ShortcutName + ".lnk"),
                    DesktopLnk = Path.Combine(t, "Desktop", ShortcutName + ".lnk"),
                    Registry = false,
                    DesktopDoneMarker = Path.Combine(p.Home, "desktop-shortcut.done"),
                };
            }
            return new Targets { StartMenuLnk = StartMenuShortcut, DesktopLnk = DesktopShortcut, Registry = true, DesktopDoneMarker = Path.Combine(p.Home, "desktop-shortcut.done") };
        }

        public enum CopyResult { Copied, Failed, PeerKept }

        /// <summary>Result of asking a running copy to free the installed exe: the single-instance lock (held while copying,
        /// then released for the relaunched copy), or PeerKept when the running copy is busy and stays.</summary>
        public sealed class TakeOver
        {
            public IDisposable Lock { get; set; }
            public bool PeerKept { get; set; }
        }

        /// <summary>Copies this exe to app\LotRMPUpdater.exe through a temporary file (never a half-written exe). When the
        /// installed exe is in use by a running older copy, <paramref name="takeOver"/> asks it to exit first.</summary>
        public static CopyResult CopyIn(UpdaterPaths p, UpdaterLog log, Func<TakeOver> takeOver)
        {
            var target = InstalledExe(p);
            var tmp = target + ".new";
            try
            {
                Directory.CreateDirectory(p.AppDir);
                File.Copy(CurrentExe, tmp, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                log.Warn("Could not install the updater in " + p.AppDir + " (" + ex.Message + "). It still works from where it is.");
                return CopyResult.Failed;
            }
            Exception last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (TryPutInPlace(tmp, target, out last)) { log.Info("Installed the updater " + FileVersionOf(target) + " in " + p.AppDir + "."); return CopyResult.Copied; }
                System.Threading.Thread.Sleep(500); // an antivirus scan of the old file, usually
            }
            log.Info("The installed updater is in use (" + last?.Message + "); asking the running copy to close.");
            var t = takeOver();
            if (t.PeerKept) { TryDelete(tmp); return CopyResult.PeerKept; }
            if (t.Lock != null)
                using (t.Lock)
                {
                    // The old copy releases the lock just before its process ends; Windows frees its exe a moment later.
                    for (int attempt = 0; attempt < 30; attempt++)
                    {
                        if (TryPutInPlace(tmp, target, out last)) { log.Info("Installed the updater " + FileVersionOf(target) + " in " + p.AppDir + "."); return CopyResult.Copied; }
                        System.Threading.Thread.Sleep(500);
                    }
                }
            log.Warn("Could not install the updater in " + p.AppDir + " (" + last?.Message + "). It still works from where it is.");
            TryDelete(tmp);
            return CopyResult.Failed;
        }

        private static bool TryPutInPlace(string tmp, string target, out Exception error)
        {
            error = null;
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { error = ex; return false; }
        }

        private static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
        }

        public static string FileVersionOf(string exe)
        {
            try { return Core.SelfUpdate.SelfInstallPolicy.NormalizeFileVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion); }
            catch (Exception) { return null; }
        }

        /// <summary>Copies the exe into place (when run from Downloads) and refreshes the uninstall entry. Returns the installed exe.</summary>
        public static string Ensure(UpdaterPaths p, string version, UpdaterLog log) => Ensure(p, version, log, null);

        /// <summary>Copies the exe into place when needed, refreshes the uninstall entry and the shortcuts (Start menu: always
        /// there; Desktop: created once). Returns the installed exe.</summary>
        public static string Ensure(UpdaterPaths p, string version, UpdaterLog log, Targets targets)
        {
            var target = InstalledExe(p);
            targets = targets ?? TargetsFor(null, p);
            try
            {
                if (!IsRunningInstalledCopy(p) && !InstalledIsNewer(target, version, log))
                {
                    Directory.CreateDirectory(p.AppDir);
                    File.Copy(CurrentExe, target, true);
                    log.Info("Installed the updater in " + p.AppDir);
                }
                if (targets.Registry)
                    using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                    {
                        k.SetValue("DisplayName", DisplayName);
                        k.SetValue("DisplayVersion", version);
                        k.SetValue("Publisher", "LotR MP Event team");
                        k.SetValue("DisplayIcon", target + ",0");
                        k.SetValue("InstallLocation", p.AppDir);
                        k.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        k.SetValue("EstimatedSize", (int)(new FileInfo(target).Length / 1024), RegistryValueKind.DWord);
                    }
                if (!File.Exists(targets.StartMenuLnk))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targets.StartMenuLnk));
                    CreateShortcut(targets.StartMenuLnk, target, null, "Keeps the LotR MP Event mod up to date");
                }
                if (!File.Exists(targets.DesktopDoneMarker))
                {
                    if (!File.Exists(targets.DesktopLnk))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targets.DesktopLnk));
                        CreateShortcut(targets.DesktopLnk, target, null, "Keeps the LotR MP Event mod up to date");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(targets.DesktopDoneMarker));
                    File.WriteAllText(targets.DesktopDoneMarker, "Desktop shortcut created once; delete this file to get it back at the next start.\r\n");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                log.Warn("Could not finish setting up the updater (" + ex.Message + "). It still works from where it is.");
            }
            return target;
        }

        /// <summary>An older download started from Downloads must not replace a newer, self-updated installed copy (nor
        /// copy itself again over the same version).</summary>
        private static bool InstalledIsNewer(string installed, string runningVersion, UpdaterLog log)
        {
            if (!File.Exists(installed)) return false;
            var v = FileVersionOf(installed);
            int c = Core.SelfUpdate.AppVersions.Compare(v, runningVersion);
            if (c < 0) return false;
            if (c > 0) log.Info("The installed updater (" + v + ") is newer than this copy (" + runningVersion + "); it is kept.");
            return true;
        }

        internal static void CreateShortcut(string lnk, string target, string arguments, string description)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                var shell = Activator.CreateInstance(t);
                var sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                if (!string.IsNullOrEmpty(arguments)) st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { arguments });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch (Exception) { /* a missing shortcut is not worth an error */ }
        }

        /// <summary>Removes the registry entry, the shortcut and %LOCALAPPDATA%\LotRMPUpdater (the running exe is deleted
        /// a few seconds after exit by a hidden cmd).</summary>
        public static void Remove(UpdaterPaths p)
        {
            if (!string.Equals(Path.GetFileName(p.Home.TrimEnd('\\')), "LotRMPUpdater", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to remove an unexpected folder: " + p.Home);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); } catch (Exception) { }
            try { if (File.Exists(DesktopShortcut)) File.Delete(DesktopShortcut); } catch (Exception) { }
            try { new StartupRegistration(StartupRegistration.UserStartupFolder, InstalledExe(p), new ComShortcutWriter()).Disable(); } catch (Exception) { }
            foreach (var d in new[] { Path.Combine(p.Home, "cache"), p.BackupRoot, p.LogDir })
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { }
            foreach (var f in Directory.Exists(p.Home) ? Directory.GetFiles(p.Home) : new string[0])
                try { File.Delete(f); } catch (Exception) { }
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"" + p.Home + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            };
            Process.Start(psi)?.Dispose();
        }
    }
}
