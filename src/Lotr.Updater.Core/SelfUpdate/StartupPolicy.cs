using System;
using System.IO;
using System.Text;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Core.SelfUpdate
{
    /// <summary>The exe targets .NET Framework 4.8; on an older 4.x the CLR still starts it, so Main checks the installed
    /// release number (HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full, "Release") before touching anything 4.8-only.</summary>
    public static class NetFrameworkRequirement
    {
        /// <summary>Lowest "Release" value of .NET Framework 4.8 (528040 on Windows 10 1903+, 528049/528372/528449 elsewhere).</summary>
        public const int Release48 = 528040;
        public const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        public enum Verdict { Ok, TooOld, Unknown }

        /// <summary>Unknown (registry unreadable) never blocks the start: the OS would have refused a missing CLR anyway.</summary>
        public static Verdict Check(int? release) => !release.HasValue ? Verdict.Unknown : release.Value >= Release48 ? Verdict.Ok : Verdict.TooOld;

        /// <summary>Human name of a Release value (for the message), e.g. 461808 = "4.7.2".</summary>
        public static string Describe(int? release)
        {
            if (!release.HasValue) return "unknown";
            int r = release.Value;
            if (r >= 533320) return "4.8.1";
            if (r >= 528040) return "4.8";
            if (r >= 461808) return "4.7.2";
            if (r >= 461308) return "4.7.1";
            if (r >= 460798) return "4.7";
            if (r >= 394802) return "4.6.2";
            if (r >= 394254) return "4.6.1";
            if (r >= 393295) return "4.6";
            if (r >= 379893) return "4.5.2";
            if (r >= 378675) return "4.5.1";
            return "4.5";
        }
    }

    public enum SelfInstallAction
    {
        /// <summary>This exe is the installed copy (or self-install is off): carry on.</summary>
        RunHere,
        /// <summary>Copy this exe into %LOCALAPPDATA%\LotRMPUpdater\app\, then start that copy and exit.</summary>
        CopyThenRelaunch,
        /// <summary>The installed copy is the same version or newer: start it and exit (an old download never downgrades).</summary>
        RelaunchInstalled,
    }

    public static class SelfInstallPolicy
    {
        /// <param name="installedVersion">File version of the installed exe; null when missing or unreadable.</param>
        public static SelfInstallAction Decide(bool runningInstalledCopy, string runningVersion, bool installedExists, string installedVersion)
        {
            if (runningInstalledCopy) return SelfInstallAction.RunHere;
            if (!installedExists || installedVersion == null) return SelfInstallAction.CopyThenRelaunch;
            return AppVersions.Compare(installedVersion, runningVersion) >= 0 ? SelfInstallAction.RelaunchInstalled : SelfInstallAction.CopyThenRelaunch;
        }

        /// <summary>"0.2.0.0" (FileVersionInfo) → "0.2.0".</summary>
        public static string NormalizeFileVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            if (v.Split('.').Length == 4 && v.EndsWith(".0", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 2);
            return v;
        }
    }

    /// <summary>
    /// app\probation.json: written by the start that swapped in a new updater; the new exe deletes it once its window has
    /// rendered (or the tray icon is up). A new version that never gets there is rolled back automatically: by the swapping
    /// process, which waits for it (<see cref="HealthWait"/>), or, if that one is gone, by the next start
    /// (<see cref="AtStart"/>).
    /// </summary>
    public sealed class SelfUpdateProbation
    {
        public const int MaxStarts = 2;

        public string Version { get; set; }
        public string PreviousVersion { get; set; }
        public int Starts { get; set; }
        public string SwappedAtUtc { get; set; }

        public static string FileOf(UpdaterPaths p) => Path.Combine(p.AppDir, "probation.json");

        public static SelfUpdateProbation Load(UpdaterPaths p)
        {
            try
            {
                var f = FileOf(p);
                if (!File.Exists(f)) return null;
                var o = Json.Json.ParseObject(File.ReadAllText(f, Encoding.UTF8));
                var r = new SelfUpdateProbation { Version = o.Str("version"), PreviousVersion = o.Str("previousVersion"), Starts = (int)o.Long("starts"), SwappedAtUtc = o.Str("swappedAtUtc") };
                return r.Version == null ? null : r;
            }
            catch (Exception) { return null; }
        }

        public void Save(UpdaterPaths p)
        {
            Directory.CreateDirectory(p.AppDir);
            File.WriteAllText(FileOf(p), Json.Json.Write(new JsonObject
            {
                ["version"] = Version,
                ["previousVersion"] = PreviousVersion,
                ["starts"] = (long)Starts,
                ["swappedAtUtc"] = SwappedAtUtc,
            }), new UTF8Encoding(false));
        }

        public static void Clear(UpdaterPaths p)
        {
            try { File.Delete(FileOf(p)); } catch (Exception) { }
        }

        public enum Verdict { None, OnProbation, RollBack }

        /// <summary>At the start of the installed exe: is this version still on probation, and has it already failed too often?</summary>
        public static Verdict AtStart(SelfUpdateProbation pr, string runningVersion, bool previousExeKept)
        {
            if (pr == null || !string.Equals(pr.Version, runningVersion, StringComparison.OrdinalIgnoreCase)) return Verdict.None;
            if (pr.Starts >= MaxStarts) return previousExeKept ? Verdict.RollBack : Verdict.None;
            return Verdict.OnProbation;
        }
    }

    /// <summary>The swapping process waits for the new exe to report healthy (probation file gone). Pure logic (clock and
    /// sleep injected) so the tests can drive it.</summary>
    public static class HealthWait
    {
        /// <param name="stillOnProbation">true while the new exe has not reported healthy.</param>
        /// <param name="childExited">true once the process we started has exited (it may have restarted itself, e.g. in
        /// software rendering: the wait goes on for <paramref name="graceAfterExit"/>).</param>
        public static bool WaitHealthy(Func<bool> stillOnProbation, Func<bool> childExited, TimeSpan timeout, TimeSpan graceAfterExit,
            Func<TimeSpan> elapsed, Action<TimeSpan> sleep)
        {
            TimeSpan? exitedAt = null;
            var step = TimeSpan.FromMilliseconds(250);
            while (true)
            {
                if (!stillOnProbation()) return true;
                var now = elapsed();
                if (now >= timeout) return false;
                if (exitedAt == null && childExited()) exitedAt = now;
                if (exitedAt != null && now - exitedAt.Value >= graceAfterExit) return false;
                sleep(step);
            }
        }
    }

    /// <summary>What a second start does when another updater already runs for the same home folder.</summary>
    public interface IInstanceChannel
    {
        /// <summary>Asks the running copy to show its window.</summary>
        void RequestShow();
        /// <summary>Asks the running copy to exit (a newer version takes over). It answers Bye (exiting) or Ack (busy: it shows itself instead).</summary>
        void RequestExit();
        /// <summary>Waits for the running copy's answer.</summary>
        PeerReply WaitReply(TimeSpan timeout);
        /// <summary>Waits until the running copy has released the single-instance lock (this start then owns it).</summary>
        bool WaitOwnership(TimeSpan timeout);
    }

    public enum PeerReply { None, Ack, Bye }

    public enum SecondStartOutcome
    {
        /// <summary>The running copy answered and shows itself: this start exits.</summary>
        PeerShown,
        /// <summary>The running copy exited for us: this start carries on as the only instance.</summary>
        TookOver,
        /// <summary>No answer in time: the running copy is hung (or an old version without the answer channel).</summary>
        NoAnswer,
    }

    public static class SecondStartPolicy
    {
        public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Takes over only from an OLDER running copy; an unknown version (no instance file) is an old one.</summary>
        public static bool ShouldTakeOver(string myVersion, string peerVersion) =>
            peerVersion == null || AppVersions.Compare(myVersion, peerVersion) > 0;

        public static SecondStartOutcome Run(IInstanceChannel c, bool takeOver, TimeSpan answerTimeout, TimeSpan exitTimeout)
        {
            if (!takeOver)
            {
                c.RequestShow();
                return c.WaitReply(answerTimeout) == PeerReply.Ack ? SecondStartOutcome.PeerShown : SecondStartOutcome.NoAnswer;
            }
            c.RequestExit();
            switch (c.WaitReply(answerTimeout))
            {
                case PeerReply.Ack: return SecondStartOutcome.PeerShown; // busy (install running): it shows itself, it updates later
                case PeerReply.Bye: return c.WaitOwnership(exitTimeout) ? SecondStartOutcome.TookOver : SecondStartOutcome.NoAnswer;
                default: return SecondStartOutcome.NoAnswer;
            }
        }
    }

    /// <summary>WPF hardware rendering fails on some GPU drivers / overlays (white window). Software rendering is chosen by
    /// --safe-mode, LOTR_SOFTWARE_RENDER=1, a choice saved after a failed start, or a start that never rendered.</summary>
    public static class RenderModePolicy
    {
        public sealed class Decision
        {
            public bool Software { get; set; }
            /// <summary>Save the choice so later starts go straight to software rendering.</summary>
            public bool Persist { get; set; }
            public string Reason { get; set; }
        }

        public static Decision Decide(bool safeModeSwitch, string envValue, bool savedSoftware, bool lastStartNeverRendered)
        {
            if (safeModeSwitch) return new Decision { Software = true, Reason = "--safe-mode" };
            if (envValue == "1") return new Decision { Software = true, Reason = "LOTR_SOFTWARE_RENDER=1" };
            if (savedSoftware) return new Decision { Software = true, Reason = "saved after an earlier start that did not render" };
            if (lastStartNeverRendered) return new Decision { Software = true, Persist = true, Reason = "the last start never showed its window" };
            return new Decision { Software = false, Reason = "hardware" };
        }
    }
}
