using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Headless;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater
{
    /// <summary>
    /// Entry point. Loads the embedded DLLs (Core, GameDetection, ZstdSharp + System.* helpers) from the exe's resources
    /// before any of their types is touched, so LotRMPUpdater.exe is one self-contained file. Before any window:
    /// checks .NET Framework 4.8, finishes a staged self-update (swap + restart, with automatic rollback when the new
    /// version does not start), installs itself in %LOCALAPPDATA%\LotRMPUpdater\app\ and restarts from there, and keeps
    /// one instance per home folder (a newer exe takes over from an older running one; a hung one can be closed).
    /// Every step is traced in the daily log; anything unhandled leaves a crash report and a message, never a zombie.
    /// </summary>
    public static class Program
    {
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a swapped-in updater has to report healthy (window rendered / tray up) before it is rolled back.</summary>
        public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(90);
        /// <summary>The new exe may restart itself once (software rendering): the wait goes on this long after it exits.</summary>
        public static readonly TimeSpan HealthGraceAfterExit = TimeSpan.FromSeconds(20);

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            Args = args ?? new string[0];
            if (!NetFrameworkGate()) return 3;
            return Run(Args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool NetFrameworkGate() => Platform.StartupGuard.NetFrameworkIsSupported();

        /// <summary>The command line of this start (restarts reuse it).</summary>
        public static string[] Args { get; private set; } = new string[0];

        public static string Version => typeof(Program).Assembly.GetName().Version.ToString(3);

        public static string ExePath => Assembly.GetEntryAssembly().Location;

        /// <summary>Arguments for a restart of this app: the same start options, without the one-shot ones, then
        /// "--after &lt;this PID&gt;" so the new process waits for this one to be gone.</summary>
        public static string RestartArguments(bool minimized)
        {
            var keep = new List<string>();
            for (int i = 0; i < Args.Length; i++)
            {
                var a = Args[i].ToLowerInvariant();
                if (a == "--after") { i++; continue; }
                if (a == "--rollback-updater" || a == "--minimized") continue;
                keep.Add(Args[i]);
            }
            if (minimized) keep.Add("--minimized");
            keep.Add("--after");
            keep.Add(Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return JoinArgs(keep);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            var options = StartOptions.Parse(args);
            if (options.AfterPid > 0) WaitForExit(options.AfterPid);
            if (options.Headless && options.Error != null)
            {
                if (options.Report != null) File.WriteAllText(options.Report, "error=" + options.Error + "\nresult=FAIL\n");
                return 2;
            }
            var paths = options.PathsForInstance();
            var log = new UpdaterLog(paths.LogDir);
            Platform.StartupGuard.Init(log, paths, Version);
            Platform.StartupGuard.Trace("Main: LotRMPUpdater " + Version + " from " + ExePath + (args.Length > 0 ? " · " + JoinArgs(args) : ""));
            try
            {
                if (options.Error == null && !options.Uninstall && options.TourDir == null)
                {
                    var code = SelfUpdateAtStart(options, args, paths, log);
                    if (code.HasValue) return code.Value;
                    if (options.Headless) return RunHeadless(options);
                    code = SelfInstallAtStart(options, paths, log);
                    if (code.HasValue) return code.Value;
                }

                using (var instance = options.Error == null && options.TourDir == null && !options.Uninstall ? SingleInstance.TryAcquire(paths, Version) : null)
                {
                    if (instance != null && !instance.IsFirst)
                    {
                        var code = SecondStart(instance, options, paths, log);
                        if (code.HasValue) return code.Value;
                    }
                    Platform.StartupGuard.ChooseRenderMode(options);
                    var app = new App(options, instance);
                    app.InitializeComponent();
                    Platform.StartupGuard.Trace("application created");
                    return app.Run();
                }
            }
            catch (Exception ex)
            {
                // Nothing may keep the single-instance lock after a failed start: the using above has released it.
                return Platform.StartupGuard.Fatal(ex, "start-up");
            }
        }

        private static void WaitForExit(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid)) p.WaitForExit(30000);
            }
            catch (ArgumentException) { } // already gone
            catch (InvalidOperationException) { }
        }

        private static bool SameFile(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static Process StartExe(string exe, string arguments) =>
            Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });

        /// <summary>--rollback-updater, a version on probation that keeps failing, or a staged update: swap, start the
        /// installed exe again with the same arguments, wait until it reports healthy (else roll back), exit.
        /// Returns an exit code when this process must stop here.</summary>
        private static int? SelfUpdateAtStart(StartOptions o, string[] args, UpdaterPaths paths, UpdaterLog log)
        {
            var rest = StripAfter(args.Where(a => !string.Equals(a, "--rollback-updater", StringComparison.OrdinalIgnoreCase)).ToArray());

            // This version was swapped in earlier and has not started properly since: go back (the swapping process normally
            // does this itself; this covers a PC switched off meanwhile).
            if (!o.Headless && !o.RollbackUpdater && File.Exists(paths.InstalledExe) && SameFile(ExePath, paths.InstalledExe))
            {
                var pr = SelfUpdateProbation.Load(paths);
                switch (SelfUpdateProbation.AtStart(pr, Version, File.Exists(paths.PreviousExe)))
                {
                    case SelfUpdateProbation.Verdict.RollBack:
                        log.Error("Updater " + Version + " did not start properly after its update (" + pr.Starts + " tries); going back to " + (pr.PreviousVersion ?? "the previous version") + ".");
                        return RollBackAndRelaunch(paths, log, rest, Version, null);
                    case SelfUpdateProbation.Verdict.OnProbation:
                        pr.Starts++;
                        try { pr.Save(paths); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                        Platform.StartupGuard.Trace("new updater on probation, start " + pr.Starts);
                        break;
                }
            }

            var pendingVersion = PendingSelfUpdate.Load(paths)?.Version;
            SwapOutcome swap;
            try
            {
                swap = o.RollbackUpdater ? SelfUpdateSwap.Rollback(paths, log) : SelfUpdateSwap.ApplyPending(paths, ExePath, Version, log);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Error("Self-update at start-up failed: " + ex.Message);
                return null;
            }
            if (o.RollbackUpdater && swap.Swapped)
            {
                // Do not offer the version we just left again.
                SkipUpdaterVersion(paths, Version);
                SelfUpdateProbation.Clear(paths);
            }
            if (!swap.Swapped) return null;
            bool watch = !o.RollbackUpdater && !o.Headless && pendingVersion != null;
            if (watch)
            {
                try { new SelfUpdateProbation { Version = pendingVersion, PreviousVersion = Version, Starts = 0, SwappedAtUtc = DateTime.UtcNow.ToString("o") }.Save(paths); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { watch = false; }
            }
            Platform.StartupGuard.Trace(swap.Message + "; starting " + swap.RelaunchExe);
            Process p;
            try
            {
                p = StartExe(swap.RelaunchExe, JoinArgs(rest));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                // The new exe does not start: put this one back and carry on.
                log.Error("The new updater did not start (" + ex.Message + "); rolling back.");
                if (!o.RollbackUpdater)
                {
                    SelfUpdateSwap.Rollback(paths, log);
                    SelfUpdateProbation.Clear(paths);
                    if (pendingVersion != null) SkipUpdaterVersion(paths, pendingVersion);
                }
                return null;
            }
            using (p)
            {
                if (p == null) return 0;
                if (o.Headless)
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
                if (!watch) return 0;
                // Stay (invisible) until the new version says it is healthy; else put this one back.
                var sw = Stopwatch.StartNew();
                bool healthy = HealthWait.WaitHealthy(() => File.Exists(SelfUpdateProbation.FileOf(paths)), () => p.HasExited,
                    HealthTimeout, HealthGraceAfterExit, () => sw.Elapsed, d => Thread.Sleep(d));
                if (healthy) return 0;
                log.Error("Updater " + pendingVersion + " did not start properly " +
                          (p.HasExited ? "(it exited with code " + SafeExitCode(p) + " and did not come back)" : "(no window within " + HealthTimeout.TotalSeconds + " s)") +
                          "; going back to " + Version + ".");
                return RollBackAndRelaunch(paths, log, rest, pendingVersion, p.Id) ?? 0;
            }
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return "?"; }
        }

        private static string[] StripAfter(string[] args)
        {
            var r = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--after", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
                r.Add(args[i]);
            }
            return r.ToArray();
        }

        private static void SkipUpdaterVersion(UpdaterPaths paths, string version)
        {
            var state = UpdaterState.Load(paths.StateFile);
            state.SkippedUpdaterVersion = version;
            try { state.Save(paths.StateFile); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Closes whatever still runs the bad exe, puts LotRMPUpdater.old.exe back, never offers the bad version
        /// again and starts the previous one. Null when nothing could be rolled back (this process carries on).</summary>
        private static int? RollBackAndRelaunch(UpdaterPaths paths, UpdaterLog log, string[] args, string badVersion, int? childPid)
        {
            var stuck = new List<Process>();
            if (childPid.HasValue) { try { stuck.Add(Process.GetProcessById(childPid.Value)); } catch (Exception) { } }
            stuck.AddRange(PeerProcesses.Running(paths.InstalledExe).Where(x => !childPid.HasValue || x.Id != childPid.Value));
            PeerProcesses.Kill(stuck, log.Warn);
            SwapOutcome r;
            try { r = SelfUpdateSwap.Rollback(paths, log); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { r = SwapOutcome.None(ex.Message); }
            SelfUpdateProbation.Clear(paths);
            SkipUpdaterVersion(paths, badVersion);
            if (!r.Swapped)
            {
                log.Error("Automatic rollback of the updater failed (" + r.Message + ").");
                return null;
            }
            log.Warn("Updater " + badVersion + " was rolled back automatically; it will not be offered again (a later version will).");
            try
            {
                using (StartExe(paths.InstalledExe, JoinArgs(args))) { }
                return 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                log.Error("Could not start the previous updater: " + ex.Message);
                return null;
            }
        }

        /// <summary>First start from Downloads (or anywhere else): copy into %LOCALAPPDATA%\LotRMPUpdater\app\, add the
        /// shortcuts, start the installed copy and leave. An installed copy that is the same or newer is simply started.
        /// Off for dev runs unless --dev-self-install (loopback server, test home, shortcuts inside the test home).</summary>
        private static int? SelfInstallAtStart(StartOptions o, UpdaterPaths paths, UpdaterLog log)
        {
            if (o.Error != null || o.Uninstall || o.TourDir != null || o.Headless) return null;
            if (o.Dev && !o.DevSelfInstall) return null;
            bool installedExists = File.Exists(paths.InstalledExe);
            bool installedCopy = installedExists && SameFile(ExePath, paths.InstalledExe);
            var installedVersion = installedExists && !installedCopy ? PeerProcesses.VersionOf(paths.InstalledExe) : null;
            var action = SelfInstallPolicy.Decide(installedCopy, Version, installedExists, installedVersion);
            Platform.StartupGuard.Trace("self-install: " + action + (installedVersion != null ? " (installed copy " + installedVersion + ")" : ""));
            if (action == SelfInstallAction.RunHere) return null;
            if (action == SelfInstallAction.CopyThenRelaunch)
            {
                var copied = Platform.SelfInstall.CopyIn(paths, log, () => TakeOverForCopy(paths, o, log));
                if (copied == Platform.SelfInstall.CopyResult.PeerKept) return 0; // the running copy is busy and shows itself
                if (copied != Platform.SelfInstall.CopyResult.Copied) return null; // keeps working from here
                Platform.SelfInstall.Ensure(paths, Version, log, Platform.SelfInstall.TargetsFor(o, paths));
            }
            try
            {
                using (StartExe(paths.InstalledExe, RestartArguments(o.Minimized))) { }
                log.Info("Started the installed updater (" + paths.InstalledExe + "); this copy (" + ExePath + ") can be deleted.");
                return 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                log.Warn("Could not start the installed updater (" + ex.Message + "); running from here.");
                return null;
            }
        }

        /// <summary>The installed exe is locked by a running (older) copy: ask it to exit, or close it if it does not answer.</summary>
        private static Platform.SelfInstall.TakeOver TakeOverForCopy(UpdaterPaths paths, StartOptions o, UpdaterLog log)
        {
            var instance = SingleInstance.TryAcquire(paths, Version);
            if (instance.IsFirst) return new Platform.SelfInstall.TakeOver { Lock = instance };
            var code = SecondStart(instance, o, paths, log, forceTakeOver: true);
            if (code.HasValue) { instance.Dispose(); return new Platform.SelfInstall.TakeOver { PeerKept = true }; }
            return new Platform.SelfInstall.TakeOver { Lock = instance };
        }

        /// <summary>Another updater holds the lock for this home. Returns an exit code (this start stops), or null when this
        /// start now owns the lock (it took over from an older copy, or closed a hung one).</summary>
        private static int? SecondStart(SingleInstance instance, StartOptions o, UpdaterPaths paths, UpdaterLog log, bool forceTakeOver = false)
        {
            var peer = instance.ReadPeer();
            bool takeOver = forceTakeOver || SecondStartPolicy.ShouldTakeOver(Version, peer?.Version);
            if (!takeOver && o.Minimized)
            {
                Platform.StartupGuard.Trace("already running (PID " + peer?.Pid + "); a start with Windows has nothing to do");
                return 0;
            }
            var outcome = SecondStartPolicy.Run(instance, takeOver, SecondStartPolicy.AnswerTimeout, SecondStartPolicy.ExitTimeout);
            Platform.StartupGuard.Trace("another updater runs (" + (peer != null ? "PID " + peer.Pid + ", " + peer.Version : "older version") + "): " +
                                        (takeOver ? "asked it to exit" : "asked it to show itself") + " -> " + outcome);
            if (outcome == SecondStartOutcome.PeerShown) return 0;
            if (outcome == SecondStartOutcome.TookOver)
            {
                log.Info("Took over from the updater " + (peer?.Version ?? "0.1.x") + " that was running.");
                return null;
            }

            // No answer: hung, or a 0.1.x copy that cannot answer.
            bool testHome = o.Dev || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOTR_UPDATER_HOME"));
            var procs = PeerProcesses.Find(paths, testHome, peer?.Pid);
            if (procs.Count == 0 && instance.WaitOwnership(TimeSpan.FromSeconds(5))) return null;
            bool legacyOlder = peer == null && takeOver && procs.Count > 0 &&
                               procs.All(x => AppVersions.Compare(PeerProcesses.VersionOf(PeerProcesses.PathOf(x)), Version) < 0);
            if (!legacyOlder)
            {
                log.Warn("The updater already running (" + (peer != null ? "PID " + peer.Pid : "unknown PID") + ") does not answer.");
                var answer = Platform.StartupGuard.Message("The updater is already running but not responding.\n\nClose it and start again?\n\n" +
                                                           "OK: close it and restart.     Cancel: leave it as it is.",
                    Platform.StartupGuard.MB_OKCANCEL | Platform.StartupGuard.MB_ICONWARNING);
                if (answer != Platform.StartupGuard.IDOK)
                {
                    foreach (var x in procs) x.Dispose();
                    return 0;
                }
            }
            else log.Info("An older updater (0.1.x) is running; it is closed so this version takes over.");
            PeerProcesses.Kill(procs, log.Warn);
            if (instance.WaitOwnership(TimeSpan.FromSeconds(10))) return null;
            log.Error("The other updater could not be closed.");
            Platform.StartupGuard.Message("The other updater could not be closed. Restart your PC, then start the updater again.", Platform.StartupGuard.MB_ICONERROR);
            return 1;
        }

        /// <summary>Dev / rehearsal: the player's steps without a window (key=value report, exit code 0 = PASS).</summary>
        private static int RunHeadless(StartOptions o)
        {
            var cfg = new UpdaterConfig { AppVersion = Version, Dev = true, BaseUrl = o.Server, FixedGamePath = o.Game, CurrentExe = ExePath };
            cfg.TrustedKeysPem.AddRange(o.TrustPem);
            var svc = new UpdaterService(cfg, new UpdaterPaths(o.Home), processes: new NoGameProcesses());
            var sb = new StringBuilder();
            int code;
            using (var w = new StringWriter(sb))
            {
                try
                {
                    code = HeadlessRunner.RunAsync(svc, new HeadlessOptions { CheckOnly = o.CheckOnly, SelfUpdate = o.SelfUpdateCheck, SendSupport = o.SendSupport }, w, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    w.WriteLine("exception=" + ex.GetType().Name + ": " + ex.Message.Replace('\n', ' '));
                    w.WriteLine("result=FAIL");
                    code = 1;
                }
            }
            var report = o.Report ?? Path.Combine(o.Home, "headless-report.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
            File.WriteAllText(report, sb.ToString(), new UTF8Encoding(false));
            return code;
        }

        /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
        internal static string JoinArgs(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var c in a)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }

        private static Assembly ResolveEmbedded(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var a)) return a;
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("lib." + name + ".dll"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);
                    a = Assembly.Load(bytes);
                    Loaded[name] = a;
                    return a;
                }
            }
        }
    }

    /// <summary>The dev PC may be running the REAL game; a headless run installs into a FAKE folder and must not be blocked by it.</summary>
    internal sealed class NoGameProcesses : IProcessProbe
    {
        public IReadOnlyList<RunningProcessInfo> RunningGameProcesses() => new RunningProcessInfo[0];
    }

    /// <summary>Command line. Everything except --uninstall, --minimized, --safe-mode and --rollback-updater is for development against a LOCAL test server.</summary>
    public sealed class StartOptions
    {
        public bool Dev { get; private set; }
        public string Server { get; private set; }
        public string Game { get; private set; }
        public string Home { get; private set; }
        public List<string> TrustPem { get; } = new List<string>();
        public string TourDir { get; private set; }
        public bool Uninstall { get; private set; }
        public bool Minimized { get; private set; }
        /// <summary>--safe-mode: software rendering (no graphics card), for PCs where the window stays white.</summary>
        public bool SafeMode { get; private set; }
        /// <summary>Test only, with --dev and a loopback --server: self-install into the test home (shortcuts inside it, no registry).</summary>
        public bool DevSelfInstall { get; private set; }
        public bool RollbackUpdater { get; private set; }
        public int AfterPid { get; private set; }
        public bool Headless { get; private set; }
        public string Report { get; private set; }
        public bool CheckOnly { get; private set; }
        public bool SelfUpdateCheck { get; private set; }
        public bool SendSupport { get; private set; }
        public string Error { get; private set; }

        public UpdaterPaths PathsForInstance() => Dev && Home != null ? new UpdaterPaths(Home) : UpdaterPaths.Default();

        public static StartOptions Parse(string[] args)
        {
            var o = new StartOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "--dev": o.Dev = true; break;
                    case "--server": o.Server = Next(); break;
                    case "--game": o.Game = Next(); break;
                    case "--home": o.Home = Next(); break;
                    case "--trust": var p = Next(); if (p != null && File.Exists(p)) o.TrustPem.Add(File.ReadAllText(p)); break;
                    case "--tour": o.TourDir = Next(); break;
                    case "--uninstall": o.Uninstall = true; break;
                    case "--minimized": o.Minimized = true; break;
                    case "--safe-mode": o.SafeMode = true; break;
                    case "--dev-self-install": o.DevSelfInstall = true; break;
                    case "--rollback-updater": o.RollbackUpdater = true; break;
                    case "--after": int.TryParse(Next(), out var pid); o.AfterPid = pid; break;
                    case "--headless": o.Headless = true; break;
                    case "--report": o.Report = Next(); break;
                    case "--check-only": o.CheckOnly = true; break;
                    case "--self-update": o.SelfUpdateCheck = true; break;
                    case "--send-support": o.SendSupport = true; break;
                }
            }
            if (o.Dev)
            {
                // Dev mode is only allowed against a local server and a non-default home: it can never reach players' data.
                if (o.Server == null || !Uri.TryCreate(o.Server, UriKind.Absolute, out var u) || !u.IsLoopback)
                    o.Error = "--dev needs --server http://localhost:<port>/";
                if (o.Home == null) o.Error = "--dev needs --home <folder>";
                if (o.Game == null) o.Error = "--dev needs --game <fake game folder>";
            }
            else
            {
                o.Server = null; o.Game = null; o.Home = null; o.TrustPem.Clear(); o.TourDir = null;
                if (o.Headless) o.Error = "--headless is a development option: it needs --dev";
                o.Headless = false; o.Report = null; o.CheckOnly = false; o.SelfUpdateCheck = false; o.SendSupport = false; o.DevSelfInstall = false;
            }
            return o;
        }
    }
}
