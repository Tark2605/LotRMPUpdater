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
    /// finishes a staged self-update (swap + restart), and keeps one instance per home folder.
    /// </summary>
    public static class Program
    {
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbedded;
            return Run(args);
        }

        public static string Version => typeof(Program).Assembly.GetName().Version.ToString(3);

        public static string ExePath => Assembly.GetEntryAssembly().Location;

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
            if (options.Error == null && !options.Uninstall && options.TourDir == null)
            {
                var code = SelfUpdateAtStart(options, args);
                if (code.HasValue) return code.Value;
                if (options.Headless) return RunHeadless(options);
            }

            using (var instance = options.Error == null && options.TourDir == null && !options.Uninstall ? SingleInstance.TryAcquire(options.PathsForInstance()) : null)
            {
                if (instance != null && !instance.IsFirst)
                {
                    instance.SignalFirst(); // the running copy shows its window; this one leaves
                    return 0;
                }
                var app = new App(options, instance);
                app.InitializeComponent();
                return app.Run();
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

        /// <summary>--rollback-updater, or a staged update: swap, start the installed exe again with the same arguments, exit.
        /// Returns an exit code when this process must stop here.</summary>
        private static int? SelfUpdateAtStart(StartOptions o, string[] args)
        {
            var paths = o.PathsForInstance();
            var log = new UpdaterLog(paths.LogDir);
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
                var state = UpdaterState.Load(paths.StateFile);
                state.SkippedUpdaterVersion = Version;
                try { state.Save(paths.StateFile); } catch (IOException) { }
            }
            if (!swap.Swapped) return null;
            var rest = args.Where(a => !string.Equals(a, "--rollback-updater", StringComparison.OrdinalIgnoreCase)).ToArray();
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(swap.RelaunchExe, JoinArgs(rest)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(swap.RelaunchExe) }))
                {
                    if (!o.Headless || p == null) return 0;
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException)
            {
                // The new exe does not start: put this one back and carry on.
                log.Error("The new updater did not start (" + ex.Message + "); rolling back.");
                if (!o.RollbackUpdater) SelfUpdateSwap.Rollback(paths, log);
                return null;
            }
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

    /// <summary>One updater per home folder: a second start (Start menu while it sits in the tray) shows the first one.</summary>
    public sealed class SingleInstance : IDisposable
    {
        private readonly Mutex _mutex;
        private readonly EventWaitHandle _show;

        private SingleInstance(Mutex m, EventWaitHandle show, bool first)
        {
            _mutex = m;
            _show = show;
            IsFirst = first;
        }

        public bool IsFirst { get; }

        public static SingleInstance TryAcquire(UpdaterPaths p)
        {
            var id = LotR.Releases.Hashing.Sha256Util.Of(Encoding.UTF8.GetBytes(p.Home.ToLowerInvariant())).Substring(0, 16);
            var m = new Mutex(true, @"Local\LotRMPUpdater-" + id, out bool created);
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LotRMPUpdater-show-" + id);
            return new SingleInstance(m, ev, created);
        }

        public void SignalFirst() => _show.Set();

        /// <summary>Calls <paramref name="onShow"/> (on a background thread) each time another start asks for the window.</summary>
        public void ListenForShow(Action onShow)
        {
            var t = new Thread(() =>
            {
                try { while (_show.WaitOne()) onShow(); }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true, Name = "single-instance" };
            t.Start();
        }

        public void Dispose()
        {
            if (IsFirst) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
            _mutex.Dispose();
            _show.Dispose();
        }
    }

    /// <summary>Command line. Everything except --uninstall, --minimized and --rollback-updater is for development against a LOCAL test server.</summary>
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
                o.Headless = false; o.Report = null; o.CheckOnly = false; o.SelfUpdateCheck = false; o.SendSupport = false;
            }
            return o;
        }
    }
}
