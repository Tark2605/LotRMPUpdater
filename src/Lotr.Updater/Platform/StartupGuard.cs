using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater.Platform
{
    /// <summary>
    /// Start-up safety net: a trace of every start-up step in the daily log, crash reports (logs\crash-*.txt) for anything
    /// unhandled, a friendly message instead of a silent death, the software-rendering fallback for GPU drivers / overlays
    /// that leave the window white, and the "healthy" signal that ends a self-update's probation.
    /// </summary>
    public static class StartupGuard
    {
        private const string Caption = "LotR MP Event updater";
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly List<string> Steps = new List<string>();
        private static IUpdaterLog _log;
        private static UpdaterPaths _paths;
        private static string _version = "?";
        private static int _handlersHooked;
        private static int _fatalShown;
        private static Timer _watchdog;
        private static bool _rendered;

        public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(15); // generous: antivirus scanning a first start can delay the first frame

        /// <summary>The window has rendered (or the tray icon is up): start-up is over.</summary>
        public static bool Ready { get; private set; }
        public static bool SoftwareRendering { get; private set; }

        public static void Init(IUpdaterLog log, UpdaterPaths paths, string version)
        {
            _log = log;
            _paths = paths;
            _version = version;
            HookGlobalHandlers();
        }

        /// <summary>From the main window on: write through the app's own log (one writer per file).</summary>
        public static void UseLog(IUpdaterLog log) { if (log != null) _log = log; }

        public static void Trace(string step)
        {
            var line = "Start-up: " + step + " (+" + Clock.ElapsedMilliseconds + " ms)";
            lock (Steps) Steps.Add(line);
            try { _log?.Write(LogLevel.Detail, line); } catch (Exception) { }
        }

        // ------------------------------------------------------------ crashes

        public static void HookGlobalHandlers()
        {
            if (Interlocked.Exchange(ref _handlersHooked, 1) == 1) return;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
                var report = WriteCrashReport(ex, "unhandled exception" + (e.IsTerminating ? " (the app stops)" : ""));
                if (e.IsTerminating) ShowFatal(report);
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogException(e.Exception, "background task (ignored)");
                e.SetObserved();
            };
        }

        public static void LogException(Exception ex, string where)
        {
            try { _log?.Write(LogLevel.Error, "Unexpected error in " + where + ": " + ex); } catch (Exception) { }
        }

        /// <summary>Logs the full exception and writes logs\crash-yyyyMMdd-HHmmss.txt. Returns the report path (null if nothing could be written).</summary>
        public static string WriteCrashReport(Exception ex, string where)
        {
            LogException(ex, where);
            try
            {
                var dir = _paths?.LogDir ?? Path.Combine(Path.GetTempPath(), "LotRMPUpdater");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                var sb = new StringBuilder();
                sb.AppendLine("LotR MP Event updater crash report");
                sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
                sb.AppendLine("Version: " + _version);
                sb.AppendLine("Exe: " + SafeExePath());
                sb.AppendLine("Where: " + where);
                sb.AppendLine("Start-up finished: " + Ready + " · software rendering: " + SoftwareRendering);
                sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit"));
                sb.AppendLine(".NET Framework release: " + NetFrameworkRelease()?.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("Start-up steps:");
                lock (Steps) foreach (var s in Steps) sb.AppendLine("  " + s);
                sb.AppendLine();
                sb.AppendLine(ex.ToString());
                File.WriteAllText(file, LogMasker.Mask(sb.ToString()), new UTF8Encoding(false));
                try { _log?.Write(LogLevel.Error, "Crash report saved: " + file); } catch (Exception) { }
                return file;
            }
            catch (Exception) { return null; }
        }

        /// <summary>One friendly message per process (never a cascade of dialogs).</summary>
        public static void ShowFatal(string reportPath)
        {
            if (Interlocked.Exchange(ref _fatalShown, 1) == 1) return;
            var head = Ready ? "The updater stopped because of an unexpected error." : "The updater could not start.";
            var where = reportPath != null ? " A report was saved:\n" + reportPath : " (The report could not be saved.)";
            Message(head + where + "\n\nYou can send it to the team from the Support page next time, or attach it in a Discord ticket.", MB_ICONERROR);
        }

        /// <summary>Fatal error at start-up: log, report, message. Returns the exit code.</summary>
        public static int Fatal(Exception ex, string where)
        {
            ShowFatal(WriteCrashReport(ex, where));
            return 1;
        }

        private static string SafeExePath()
        {
            try { return System.Reflection.Assembly.GetEntryAssembly()?.Location; } catch (Exception) { return null; }
        }

        // ------------------------------------------------------------ native message box (works even when WPF does not)

        public const uint MB_OK = 0x0, MB_OKCANCEL = 0x1, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30;
        public const int IDOK = 1, IDYES = 6;

        public static int Message(string text, uint flags)
        {
            try { return MessageBoxW(IntPtr.Zero, text, Caption, flags | 0x10000 /*MB_SETFOREGROUND*/ | 0x40000 /*MB_TOPMOST*/); }
            catch (Exception) { return 0; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        // ------------------------------------------------------------ .NET Framework 4.8 gate

        public static int? NetFrameworkRelease()
        {
            var fake = Environment.GetEnvironmentVariable("LOTR_FAKE_NETFX_RELEASE"); // to see the message on a 4.8 PC
            if (!string.IsNullOrEmpty(fake) && int.TryParse(fake, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f)) return f;
            try
            {
                using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return k?.GetValue("Release") is int r ? r : (int?)null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>False (after telling the player what to install) when .NET Framework is older than 4.8.</summary>
        public static bool NetFrameworkIsSupported()
        {
            var release = NetFrameworkRelease();
            if (NetFrameworkRequirement.Check(release) != NetFrameworkRequirement.Verdict.TooOld) return true;
            var answer = Message("This updater needs Microsoft .NET Framework 4.8. This PC has version " + NetFrameworkRequirement.Describe(release) + ".\n\n" +
                                 "Open the Microsoft download page now? Install \".NET Framework 4.8 Runtime\", restart the PC if Windows asks, then start the updater again.\n\n" +
                                 NetFrameworkRequirement.DownloadUrl, MB_YESNO | MB_ICONWARNING);
            if (answer == IDYES)
            {
                try { Process.Start(new ProcessStartInfo(NetFrameworkRequirement.DownloadUrl) { UseShellExecute = true })?.Dispose(); } catch (Exception) { }
            }
            return false;
        }

        // ------------------------------------------------------------ rendering

        private static string SoftwareChoiceFile => Path.Combine(_paths.Home, "render-software.txt");
        private static string PendingRenderFile => Path.Combine(_paths.Home, "render-pending.txt");

        /// <summary>Before any window: hardware (default) or software rendering.</summary>
        public static void ChooseRenderMode(StartOptions o)
        {
            if (_paths == null) return;
            var d = RenderModePolicy.Decide(o.SafeMode, Environment.GetEnvironmentVariable("LOTR_SOFTWARE_RENDER"), File.Exists(SoftwareChoiceFile), File.Exists(PendingRenderFile));
            if (d.Persist) SaveSoftwareChoice(d.Reason);
            if (d.Software)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
                SoftwareRendering = true;
            }
            Trace("rendering: " + (d.Software ? "software (" + d.Reason + ")" : "hardware (tier " + (RenderCapability.Tier >> 16) + ")"));
        }

        private static void SaveSoftwareChoice(string reason)
        {
            try
            {
                Directory.CreateDirectory(_paths.Home);
                File.WriteAllText(SoftwareChoiceFile, "software rendering chosen automatically " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                                                     ": " + reason + "\r\nDelete this file to try hardware rendering again.\r\n");
                _log?.Write(LogLevel.Warn, "Software rendering from now on (" + reason + "). Delete " + SoftwareChoiceFile + " to try the graphics card again.");
            }
            catch (Exception) { }
        }

        /// <summary>The window is being shown for the first time: if it has not rendered within <see cref="RenderTimeout"/>,
        /// this start is given up and the app restarts in software rendering (the choice is saved).</summary>
        public static void ArmRenderWatchdog()
        {
            if (_paths == null || _watchdog != null || _rendered) return;
            try { Directory.CreateDirectory(_paths.Home); File.WriteAllText(PendingRenderFile, DateTime.Now.ToString("o", CultureInfo.InvariantCulture)); } catch (Exception) { }
            Trace("window shown");
            // A thread-pool timer: it still fires when the UI thread is stuck inside the graphics driver.
            _watchdog = new Timer(_ => OnRenderTimeout(), null, RenderTimeout, Timeout.InfiniteTimeSpan);
        }

        public static void Rendered()
        {
            if (_rendered) return;
            _rendered = true;
            _watchdog?.Dispose();
            try { if (_paths != null) File.Delete(PendingRenderFile); } catch (Exception) { }
            Trace("first frame rendered (" + (SoftwareRendering ? "software" : "hardware") + ")");
            MarkReady();
        }

        private static void OnRenderTimeout()
        {
            if (_rendered) return;
            if (SoftwareRendering)
            {
                _log?.Write(LogLevel.Warn, "The window has not rendered after " + RenderTimeout.TotalSeconds + " s, even in software rendering. Waiting.");
                return;
            }
            SaveSoftwareChoice("the window did not render within " + RenderTimeout.TotalSeconds + " s (graphics driver or overlay)");
            try
            {
                var exe = Program.ExePath;
                using (Process.Start(new ProcessStartInfo(exe, Program.RestartArguments(minimized: false)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) })) { }
                _log?.Write(LogLevel.Warn, "Restarting in software rendering.");
            }
            catch (Exception ex)
            {
                _log?.Write(LogLevel.Error, "Could not restart in software rendering (" + ex.Message + "). Start the updater again: it will use software rendering.");
            }
            // The UI thread may be stuck in the driver: end the process from here (the new one waits for this PID).
            try { Process.GetCurrentProcess().Kill(); } catch (Exception) { Environment.Exit(3); }
        }

        // ------------------------------------------------------------ healthy

        /// <summary>Start-up is over (window rendered, or tray icon up): ends a self-update's probation.</summary>
        public static void MarkReady(string how = null)
        {
            if (Ready) return;
            Ready = true;
            if (how != null) Trace(how);
            if (_paths == null) return;
            var pr = SelfUpdateProbation.Load(_paths);
            if (pr != null && string.Equals(pr.Version, _version, StringComparison.OrdinalIgnoreCase))
            {
                SelfUpdateProbation.Clear(_paths);
                _log?.Write(LogLevel.Info, "Updater " + _version + " started fine after its update (start " + pr.Starts + ").");
            }
        }
    }
}
