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
    /// %LOCALAPPDATA%\LotRMPUpdater\app\, adds a Start menu shortcut, and registers ONE registry key, the per-user uninstall
    /// entry (HKCU\...\Uninstall\LotRMPUpdater) so it appears in Windows "Installed apps". Nothing else in the registry.
    /// </summary>
    public static class SelfInstall
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LotRMPUpdater";
        public const string DisplayName = "LotR MP Event updater";

        public static string InstalledExe(UpdaterPaths p) => Path.Combine(p.AppDir, "LotRMPUpdater.exe");

        public static string StartMenuShortcut =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

        public static string CurrentExe => Assembly.GetEntryAssembly().Location;

        public static bool IsRunningInstalledCopy(UpdaterPaths p) =>
            string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe(p)), StringComparison.OrdinalIgnoreCase);

        /// <summary>Copies the exe into place (when run from Downloads) and refreshes the uninstall entry. Returns the installed exe.</summary>
        public static string Ensure(UpdaterPaths p, string version, UpdaterLog log)
        {
            var target = InstalledExe(p);
            try
            {
                if (!IsRunningInstalledCopy(p))
                {
                    Directory.CreateDirectory(p.AppDir);
                    File.Copy(CurrentExe, target, true);
                    log.Info("Installed the updater in " + p.AppDir);
                }
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
                if (!File.Exists(StartMenuShortcut)) CreateShortcut(StartMenuShortcut, target, "Keeps the LotR MP Event mod up to date");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                log.Warn("Could not finish setting up the updater (" + ex.Message + "). It still works from where it is.");
            }
            return target;
        }

        private static void CreateShortcut(string lnk, string target, string description)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return;
                var shell = Activator.CreateInstance(t);
                var sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
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
