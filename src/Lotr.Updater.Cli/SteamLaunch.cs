using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LotR.GameDetection;
using LotR.Releases.Model;

namespace Lotr.Updater.Cli
{
    /// <summary>A program to run and its arguments (never a shell string).</summary>
    public sealed class LaunchCommand
    {
        public LaunchCommand(string file, params string[] args)
        {
            File = file;
            Args = args;
        }

        public string File { get; }
        public IReadOnlyList<string> Args { get; }
        public override string ToString() => File + " " + string.Join(" ", Args);
    }

    /// <summary>
    /// Starts Bannerlord the way Steam does it (Proton, overlay, the player's launch options): steam://rungameid/261550.
    /// Linux: the native or Debian "steam" command, else the Flatpak Steam, else xdg-open. Windows: the URL through the shell.
    /// </summary>
    public static class SteamLaunch
    {
        public static readonly string RunGameUrl = "steam://rungameid/" + BannerlordConstants.SteamAppId;

        /// <param name="onPath">Is this program on PATH?</param>
        /// <param name="flatpakHasSteam">Is the Flatpak Steam installed (flatpak info com.valvesoftware.Steam)?</param>
        public static LaunchCommand Choose(Func<string, bool> onPath, Func<bool> flatpakHasSteam)
        {
            if (onPath("steam")) return new LaunchCommand("steam", RunGameUrl);
            if (onPath("flatpak") && flatpakHasSteam()) return new LaunchCommand("flatpak", "run", Lotr.Updater.Core.Game.Linux.LinuxSteamLocator.FlatpakId, RunGameUrl);
            if (onPath("xdg-open")) return new LaunchCommand("xdg-open", RunGameUrl);
            return null;
        }

        /// <summary>
        /// The same command in a session of its own (setsid), when setsid exists (util-linux: every desktop distribution and
        /// SteamOS). When Steam is not running yet, the command we start BECOMES the Steam client; as our child it would sit in
        /// the terminal's session and be killed (SIGHUP) when that terminal closes, e.g. when the player quits the menu opened
        /// from the menu shortcut, taking the game with it.
        /// </summary>
        public static LaunchCommand Detached(LaunchCommand c, Func<string, bool> onPath)
        {
            if (c == null || !onPath("setsid")) return c;
            var args = new string[c.Args.Count + 1];
            args[0] = c.File;
            for (int i = 0; i < c.Args.Count; i++) args[i + 1] = c.Args[i];
            return new LaunchCommand("setsid", args);
        }

        public static bool OnPath(string program)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                try { if (File.Exists(Path.Combine(dir, program))) return true; }
                catch (ArgumentException) { }
            }
            return false;
        }

        public static bool FlatpakHasSteam()
        {
            try
            {
                var psi = new ProcessStartInfo("flatpak") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("info");
                psi.ArgumentList.Add(Lotr.Updater.Core.Game.Linux.LinuxSteamLocator.FlatpakId);
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (InvalidOperationException) { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { return false; }
        }

        /// <summary>Starts it detached (the updater may exit right away). False when it could not be started.</summary>
        public static bool Start(LaunchCommand c)
        {
            try
            {
                var psi = new ProcessStartInfo(c.File) { UseShellExecute = false };
                foreach (var a in c.Args) psi.ArgumentList.Add(a);
                using (Process.Start(psi)) { }
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { return false; }
        }

        /// <summary>Windows (tests, dev runs): opens the steam:// URL through the shell, like the Windows app.</summary>
        public static bool StartWindows()
        {
            if (!OsPath.IsWindows) return false;
            try { using (Process.Start(new ProcessStartInfo(RunGameUrl) { UseShellExecute = true })) { } return true; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { return false; }
        }
    }
}
