using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LotR.GameDetection;

namespace Lotr.Updater.Core.Game.Linux
{
    /// <summary>
    /// "Is Bannerlord running?" on Linux. Under Proton the game is a Wine process: /proc/&lt;pid&gt;/comm holds its Windows file
    /// name ("Bannerlord.exe", cut to 15 characters by the kernel) and argv[0] in /proc/&lt;pid&gt;/cmdline its Windows path
    /// ("Z:\home\deck\...\Bannerlord.exe"), so .NET's Process.GetProcessesByName("Bannerlord") never sees it.
    /// Linux has no mandatory file locks: replacing a file the game has open silently succeeds, so this check is the only
    /// guard against updating under a running game. Read-only; unreadable processes are skipped.
    /// </summary>
    public sealed class LinuxProcessProbe : IProcessProbe
    {
        private readonly string _proc;

        public LinuxProcessProbe(string procRoot = "/proc")
        {
            _proc = procRoot;
        }

        public IReadOnlyList<RunningProcessInfo> RunningGameProcesses()
        {
            var list = new List<RunningProcessInfo>();
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(_proc).ToList(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return list; }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (name.Length == 0 || !name.All(char.IsDigit) || !int.TryParse(name, out var pid)) continue;
                string comm = null, argv0 = null;
                try { comm = File.ReadAllText(Path.Combine(d, "comm")); } catch (Exception ex) when (IsGone(ex)) { }
                try { argv0 = FirstArgument(File.ReadAllBytes(Path.Combine(d, "cmdline"))); } catch (Exception ex) when (IsGone(ex)) { }
                var match = Match(comm, argv0);
                if (match != null) list.Add(new RunningProcessInfo { Name = match, Pid = pid });
            }
            return list;
        }

        private static bool IsGone(Exception ex) => ex is IOException || ex is UnauthorizedAccessException;

        /// <summary>argv[0] of a /proc cmdline (NUL-separated); null when empty (kernel threads).</summary>
        public static string FirstArgument(byte[] cmdline)
        {
            if (cmdline == null || cmdline.Length == 0) return null;
            int end = Array.IndexOf(cmdline, (byte)0);
            var s = Encoding.UTF8.GetString(cmdline, 0, end < 0 ? cmdline.Length : end);
            return s.Length == 0 ? null : s;
        }

        /// <summary>The game process name ("Bannerlord.exe") when <paramref name="comm"/> or the file name of <paramref name="argv0"/>
        /// (after the last '\' or '/') is one of <see cref="BannerlordConstants.ProcessNames"/>, with or without ".exe",
        /// letter case ignored; a 15-character comm also matches as the kernel's cut of a longer name. Else null.</summary>
        public static string Match(string comm, string argv0)
        {
            comm = comm?.Trim();
            string file = null;
            if (!string.IsNullOrEmpty(argv0))
            {
                int cut = Math.Max(argv0.LastIndexOf('\\'), argv0.LastIndexOf('/'));
                file = argv0.Substring(cut + 1).Trim();
            }
            foreach (var n in BannerlordConstants.ProcessNames)
            {
                var exe = n + ".exe";
                if (Same(file, n) || Same(file, exe) || Same(comm, n) || Same(comm, exe)) return exe;
                if (comm != null && comm.Length == 15 && exe.StartsWith(comm, StringComparison.OrdinalIgnoreCase)) return exe;
            }
            return null;
        }

        private static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
