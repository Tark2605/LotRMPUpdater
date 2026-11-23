using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Lotr.Updater.Core.Install;

namespace Lotr.Updater.Cli
{
    /// <summary>Exit codes (docs/LINUX.md lists them for players and scripts).</summary>
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int GameNotFound = 1;
        public const int Usage = 2;
        public const int Network = 3;
        public const int ReleaseRefused = 4;
        public const int InstallFailed = 5;
        public const int VerifyDifferences = 6;
        public const int GameRunning = 7;
        public const int DownloadsPaused = 8;
        public const int AlreadyRunning = 9;
        public const int UpdateAvailable = 10;
        public const int Cancelled = 130;
    }

    /// <summary>Command line of lotr-updater. Same rule as the Windows app: everything that points the updater somewhere else
    /// than the real server (--server, --home, --trust) is for development only and needs --dev with a loopback server.</summary>
    public sealed class CliOptions
    {
        public static readonly string[] Commands = { "detect", "status", "install", "update", "verify", "play", "support", "shortcut", "version", "help", "menu" };

        public string Command { get; private set; }
        public string Game { get; private set; }
        public string Channel { get; private set; }
        public bool Yes { get; private set; }
        public bool Plain { get; private set; }
        public bool Json { get; private set; }
        public bool Repair { get; private set; }
        public bool AcceptRollback { get; private set; }
        public bool ListFiles { get; private set; }
        public bool Verbose { get; private set; }
        public int? Connections { get; private set; }
        public bool Dev { get; private set; }
        public string Server { get; private set; }
        public string Home { get; private set; }
        public List<string> TrustPem { get; } = new List<string>();
        public string Error { get; private set; }

        public static CliOptions Parse(IReadOnlyList<string> args)
        {
            var o = new CliOptions();
            var trustFiles = new List<string>();
            for (int i = 0; i < args.Count && o.Error == null; i++)
            {
                var a = args[i];
                string Next()
                {
                    if (i + 1 < args.Count) return args[++i];
                    o.Error = a + " needs a value.";
                    return null;
                }
                switch (a)
                {
                    case "--game": o.Game = Next(); break;
                    case "--channel": o.Channel = Next(); break;
                    case "--yes": case "-y": o.Yes = true; break;
                    case "--plain": o.Plain = true; break;
                    case "--json": o.Json = true; break;
                    case "--repair": o.Repair = true; break;
                    case "--accept-rollback": o.AcceptRollback = true; break;
                    case "--list": o.ListFiles = true; break;
                    case "--verbose": case "-v": o.Verbose = true; break;
                    case "--connections":
                        var n = Next();
                        if (n == null) break;
                        if (!int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) || c < 1 || c > EngineOptions.MaxConnections) o.Error = $"--connections takes a number from 1 to {EngineOptions.MaxConnections}.";
                        else o.Connections = c;
                        break;
                    case "--dev": o.Dev = true; break;
                    case "--server": o.Server = Next(); break;
                    case "--home": o.Home = Next(); break;
                    case "--trust": var t = Next(); if (t != null) trustFiles.Add(t); break;
                    case "--help": case "-h": o.Command = "help"; break;
                    case "--version": o.Command = "version"; break;
                    default:
                        if (a.StartsWith("-", StringComparison.Ordinal)) { o.Error = "Unknown option " + a + "."; break; }
                        if (o.Command != null) { o.Error = "Only one command at a time (" + o.Command + ", " + a + ")."; break; }
                        if (Array.IndexOf(Commands, a) < 0) { o.Error = "Unknown command " + a + "."; break; }
                        o.Command = a == "update" ? "install" : a;
                        break;
                }
            }
            if (o.Error != null) return o;
            if (o.Channel != null && !IsChannelName(o.Channel)) return o.Fail("--channel takes a channel name such as stable or beta.");
            if (o.Dev)
            {
                // Dev mode only against a local server and a non-default home: it can never reach players' data.
                if (o.Server == null || !Uri.TryCreate(o.Server, UriKind.Absolute, out var u) || !u.IsLoopback || (u.Scheme != "http" && u.Scheme != "https"))
                    return o.Fail("--dev needs --server http://localhost:<port>/");
                if (o.Home == null) return o.Fail("--dev needs --home <folder>");
                if (o.Game == null) return o.Fail("--dev needs --game <fake game folder>");
                foreach (var f in trustFiles)
                {
                    if (!File.Exists(f)) return o.Fail("--trust: file not found: " + f);
                    o.TrustPem.Add(File.ReadAllText(f));
                }
            }
            else if (o.Server != null || o.Home != null || trustFiles.Count > 0)
            {
                return o.Fail("--server, --home and --trust are development options: they need --dev.");
            }
            return o;
        }

        private CliOptions Fail(string error)
        {
            Error = error;
            return this;
        }

        private static bool IsChannelName(string s)
        {
            if (s.Length == 0 || s.Length > 32) return false;
            foreach (var ch in s)
                if (!(ch >= 'a' && ch <= 'z') && !(ch >= '0' && ch <= '9') && ch != '-') return false;
            return true;
        }

        public const string Usage =
@"lotr-updater - installs and updates the LotR MP Event mod for Bannerlord (Linux / Steam Deck)

Usage: lotr-updater [command] [options]
  (no command in a terminal: a small menu)

Commands:
  detect            where Bannerlord is (Steam libraries, Proton prefix)   --json for a machine-readable report
  status            is an update available? (downloads nothing, changes nothing)
  install           shows the plan, asks, then installs or updates the mod   (alias: update)
  verify            re-checks every installed file                        --repair to fix what differs
  play              starts Bannerlord through Steam
  support           shows the support log, then sends it if you agree (you get a code for your Discord ticket)
  shortcut          adds ""LotR MP Event updater"" to your application menu (it opens a terminal)
  version           prints the version

Options:
  --game <folder>   the game folder (the one with bin and Modules); remembered for next time
  --channel <name>  release channel (stable by default; remembered)
  --yes, -y         do not ask for confirmation
  --list            print every file of the plan
  --plain           progress as plain lines (no rewritten line)
  --connections N   parallel downloads, 1 to 32 (default 24)
  --accept-rollback install an OLDER release the team rolled back to (only when they announced it)
  --verbose, -v     print every log line

Exit codes: 0 ok, 1 game not found, 2 usage, 3 server or network, 4 release refused, 5 install failed,
6 verify found differences, 7 game running, 8 downloads paused by the server, 9 already running,
10 update available (status), 130 cancelled.
Log files: ~/.local/share/LotRMPUpdater/logs/ . Help: docs/LINUX.md or the Install page of the site.";
    }
}
