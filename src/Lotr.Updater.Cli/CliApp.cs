using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Model;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Game.Linux;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater.Cli
{
    /// <summary>What the CLI uses from the outside world; tests replace parts of it (never the real game, never a real server).</summary>
    public sealed class CliHost
    {
        public TextReader In { get; set; } = Console.In;
        public TextWriter Out { get; set; } = Console.Out;
        public TextWriter Err { get; set; } = Console.Error;
        /// <summary>Both stdin and stdout are a terminal: menu without a command, rewritten progress line.</summary>
        public bool Terminal { get; set; }
        /// <summary>Game search; default <see cref="LinuxSteamLocator"/> on Linux, the Windows detection library on Windows.</summary>
        public Func<IGameLocator> Locator { get; set; }
        /// <summary>Running-game check; default <see cref="LinuxProcessProbe"/> on Linux (none in --dev runs).</summary>
        public IProcessProbe Processes { get; set; }
        public RetryPolicy Retry { get; set; }
        /// <summary>Queue service limits (tests shorten them).</summary>
        public TimeSpan? QueueTimeout { get; set; }
        public TimeSpan? QueueRetryDelay { get; set; }
        /// <summary>Starts the game; default Steam (see <see cref="SteamLaunch"/>). Returns a sentence, or null when it failed.</summary>
        public Func<string> LaunchGame { get; set; }
        public CancellationToken Cancel { get; set; }
        /// <summary>shortcut: where the menu entry goes and which program it starts (tests); default the real ones.</summary>
        public string ApplicationsFolder { get; set; }
        public string ProgramPath { get; set; }
    }

    /// <summary>lotr-updater's commands (docs/LINUX.md). Every action shows its plan first and waits for a yes, like the Windows app.</summary>
    public sealed class CliApp
    {
        private readonly CliHost _host;
        private readonly CliOptions _o;
        private ConsoleUi _ui;
        private UpdaterService _svc;
        private IProcessProbe _processes;

        public CliApp(CliHost host, CliOptions options)
        {
            _host = host;
            _o = options;
            _ui = new ConsoleUi(host.In, host.Out, host.Err, host.Terminal && !options.Plain);
        }

        public ConsoleUi Ui => _ui;

        /// <summary>True while the updater works (Ctrl+C then cancels safely); false while it waits for an answer.</summary>
        public bool Busy { get; private set; }

        public static string Version
        {
            get
            {
                var v = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
                int plus = v.IndexOf('+');
                return plus < 0 ? v : v.Substring(0, plus);
            }
        }

        /// <summary>Sent to the server (User-Agent, support log): the version and the platform, nothing else.</summary>
        public static string AppVersion => Version + (OsPath.IsWindows ? "-cli" : "-linux");

        private CancellationToken Ct => _host.Cancel;

        public static async Task<int> RunAsync(string[] args, CliHost host)
        {
            var o = CliOptions.Parse(args);
            if (o.Error != null)
            {
                host.Err.WriteLine(o.Error);
                host.Err.WriteLine("Run lotr-updater --help for the list of commands.");
                return ExitCodes.Usage;
            }
            return await new CliApp(host, o).RunAsync().ConfigureAwait(false);
        }

        public async Task<int> RunAsync()
        {
            var cmd = _o.Command ?? (_host.Terminal ? "menu" : null);
            if (cmd == null)
            {
                _ui.Line(CliOptions.Usage);
                return ExitCodes.Usage;
            }
            if (cmd == "help") { _ui.Line(CliOptions.Usage); return ExitCodes.Ok; }
            if (cmd == "version") { _ui.Line("lotr-updater " + Version + " (" + System.Runtime.InteropServices.RuntimeInformation.OSDescription + ")"); return ExitCodes.Ok; }
            if (cmd == "detect") return Detect();
            if (cmd == "shortcut") return Shortcut();

            if (!_o.Dev && ServerEndpoints.ProductionBase == ServerEndpoints.PlaceholderBase)
            {
                _ui.Error("This is a test build without a server address: it cannot check or download anything. Get the real lotr-updater from the Install page of the site.");
                return MenuCouldNotStart(ExitCodes.Usage);
            }

            var paths = _o.Dev ? new UpdaterPaths(_o.Home) : UpdaterPaths.Default();
            FileStream instanceLock;
            try
            {
                Directory.CreateDirectory(paths.Home);
                // flock() on Linux, a sharing lock on Windows: one lotr-updater at a time per home folder.
                instanceLock = new FileStream(Path.Combine(paths.Home, "lotr-updater.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                _ui.Error("Another lotr-updater is already running. Let it finish (or close it), then try again.");
                return MenuCouldNotStart(ExitCodes.AlreadyRunning);
            }
            catch (UnauthorizedAccessException ex)
            {
                _ui.Error("The updater cannot write in its own folder " + paths.Home + " (" + ex.Message + ").");
                return MenuCouldNotStart(ExitCodes.InstallFailed);
            }
            using (instanceLock)
            {
                CreateService(paths);
                try
                {
                    return cmd == "menu" ? await MenuAsync().ConfigureAwait(false) : await RunCommandAsync(cmd).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (Ct.IsCancellationRequested)
                {
                    _ui.EndProgress();
                    _ui.Error("Cancelled.");
                    return ExitCodes.Cancelled;
                }
            }
        }

        private void CreateService(UpdaterPaths paths)
        {
            var cfg = new UpdaterConfig
            {
                AppVersion = AppVersion,
                BaseUrl = _o.Dev ? _o.Server : ServerEndpoints.ProductionBase,
                Dev = _o.Dev,
                FixedGamePath = _o.Dev ? _o.Game : null,
            };
            cfg.TrustedKeysPem.AddRange(_o.TrustPem);
            if (_o.Connections.HasValue) cfg.Connections = _o.Connections.Value;
            _processes = _host.Processes ?? (_o.Dev ? new NoGameProcesses() : OsPath.IsWindows ? new SystemProcessProbe() : (IProcessProbe)new LinuxProcessProbe());
            _svc = new UpdaterService(cfg, paths, locator: NewLocator(), processes: _processes, retry: _host.Retry);
            if (_host.QueueTimeout.HasValue) _svc.Queue.RequestTimeout = _host.QueueTimeout.Value;
            if (_host.QueueRetryDelay.HasValue) _svc.Queue.UnavailableRetryDelay = _host.QueueRetryDelay.Value;
            if (_o.Channel != null && _o.Channel != _svc.State.Channel)
            {
                _svc.State.Channel = _o.Channel;
                _svc.SaveState();
            }
            _svc.Log.Info("lotr-updater " + AppVersion + " on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            _svc.Log.Line += l =>
            {
                // Warnings are shown (e.g. "downloading directly"); errors are explained by the command itself (no echo twice).
                if (_o.Verbose || l.Level == LogLevel.Warn) _ui.Line("  log: " + l);
            };
            _svc.Log.Prune();
        }

        private IGameLocator NewLocator() =>
            _host.Locator?.Invoke() ?? (OsPath.IsWindows ? new DetectionGameLocator() : (IGameLocator)new LinuxSteamLocator());

        private async Task<int> RunCommandAsync(string cmd)
        {
            Busy = true;
            try
            {
                switch (cmd)
                {
                    case "status": return await WithLauncherNoticeAsync(StatusAsync()).ConfigureAwait(false);
                    case "install": return await WithLauncherNoticeAsync(InstallAsync()).ConfigureAwait(false);
                    case "verify": return await VerifyAsync().ConfigureAwait(false);
                    case "play": return await PlayAsync().ConfigureAwait(false);
                    case "support": return await SupportAsync().ConfigureAwait(false);
                    default: _ui.Line(CliOptions.Usage); return ExitCodes.Usage;
                }
            }
            finally { Busy = false; }
        }

        // ------------------------------------------------------------------ menu

        private async Task<int> MenuAsync()
        {
            _ui.Line("LotR MP Event updater " + Version);
            int last = ExitCodes.Ok;
            while (true)
            {
                _ui.Line();
                _ui.Line("  1) Status: is an update available?");
                _ui.Line("  2) Install or update the mod");
                _ui.Line("  3) Verify my install");
                _ui.Line("  4) Play (starts Bannerlord through Steam)");
                _ui.Line("  5) Send a support log");
                _ui.Line("  6) Quit");
                var choice = Prompt("Choose 1-6: ");
                if (choice == null) return last;
                switch (choice.Trim())
                {
                    case "1": last = await RunCommandAsync("status").ConfigureAwait(false); break;
                    case "2": last = await RunCommandAsync("install").ConfigureAwait(false); break;
                    case "3": last = await RunCommandAsync("verify").ConfigureAwait(false); break;
                    case "4": last = await RunCommandAsync("play").ConfigureAwait(false); break;
                    case "5": last = await RunCommandAsync("support").ConfigureAwait(false); break;
                    case "6": case "q": case "quit": return last;
                    default: _ui.Line("Type a number from 1 to 6."); break;
                }
            }
        }

        // ------------------------------------------------------------------ detect

        private int Detect()
        {
            var locator = NewLocator();
            var s = _o.Game != null ? locator.Validate(_o.Game) : locator.FindAll();
            var fs = new RealFileSystem();
            if (_o.Json)
            {
                var installs = new List<object>();
                foreach (var g in s.Found)
                    installs.Add(new Core.Json.JsonObject
                    {
                        ["root"] = g.Root,
                        ["modules"] = g.ModulesPath,
                        ["store"] = g.StoreLabel,
                        ["version"] = g.GameVersion,
                        ["writable"] = g.Writable,
                        ["workshop"] = SteamWorkshop.ContentFolders(fs, g).FirstOrDefault(),
                        ["protonPrefix"] = g.ProtonPrefix,
                        ["launcherData"] = g.LauncherDataPath,
                        ["foundBy"] = g.Evidence,
                        ["warnings"] = g.Warnings.ToList(),
                    });
                var o = new Core.Json.JsonObject
                {
                    ["tool"] = "lotr-updater " + Version,
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ["found"] = s.Found.Count,
                    ["message"] = s.Message,
                    ["installs"] = installs,
                };
                if (_o.Verbose) o["report"] = s.ReportForLog;
                _ui.Line(Core.Json.Json.Write(o, indent: true));
                return s.Found.Count > 0 ? ExitCodes.Ok : ExitCodes.GameNotFound;
            }
            if (_o.Verbose && s.ReportForLog != null) _ui.Line(s.ReportForLog.TrimEnd());
            if (s.Found.Count == 0)
            {
                _ui.Error(s.Message);
                return ExitCodes.GameNotFound;
            }
            foreach (var g in s.Found) PrintGame(g, fs);
            return ExitCodes.Ok;
        }

        private void PrintGame(GameTarget g, IFileSystem fs)
        {
            _ui.Line($"Bannerlord {g.GameVersion ?? "(version unknown)"} ({g.StoreLabel})");
            _ui.Line("  Game folder:    " + g.Root);
            _ui.Line("  Modules:        " + g.ModulesPath + (g.Writable == true ? "  (writable)" : g.Writable == false ? "  (NOT writable)" : ""));
            var ws = SteamWorkshop.ContentFolders(fs, g).FirstOrDefault();
            if (ws != null) _ui.Line("  Steam Workshop: " + ws);
            if (g.ProtonPrefix != null) _ui.Line("  Proton prefix:  " + g.ProtonPrefix);
            if (g.LauncherDataPath != null) _ui.Line("  Launcher list:  " + g.LauncherDataPath);
            if (!string.IsNullOrEmpty(g.Evidence)) _ui.Line("  Found by:       " + g.Evidence);
            foreach (var w in g.Warnings) _ui.Line("  ! " + w);
        }

        // ------------------------------------------------------------------ status / install

        /// <summary>Finds the game (the --game folder, else the saved one, else a search). Null when not found (message printed).</summary>
        private bool Locate()
        {
            if (!_o.Dev && _o.Game != null)
            {
                var chosen = _svc.ChooseGame(_o.Game);
                if (chosen.Found.Count == 0)
                {
                    _ui.Error(chosen.Message);
                    return false;
                }
            }
            var s = _svc.LocateGame();
            if (_svc.Game == null)
            {
                _ui.Error(s.Message ?? "Bannerlord was not found.");
                return false;
            }
            _ui.Line($"Game: Bannerlord {_svc.Game.GameVersion ?? "(version unknown)"} ({_svc.Game.StoreLabel}), {_svc.Game.Root}");
            return true;
        }

        private async Task<CheckResult> CheckAsync()
        {
            _ui.Line("Checking for updates on the " + _svc.State.Channel + " channel...");
            return await _svc.CheckAsync(new Progress<string>(_ => { }), Ct).ConfigureAwait(false);
        }

        /// <summary>A failed check: the server's pause notice (exit 8), a refused release (4), else a server or network problem (3).</summary>
        private async Task<int> CheckFailedAsync(CheckResult c)
        {
            if (c.RollbackOffered)
            {
                _ui.Error(c.Error.Replace("Press \"Accept the rollback\"", "Run lotr-updater install --accept-rollback"));
                return ExitCodes.ReleaseRefused;
            }
            if (c.ReleaseRefused)
            {
                _ui.Error(c.Error);
                if (_o.Verbose && c.ErrorDetail != null) _ui.Error("Details: " + c.ErrorDetail);
                return ExitCodes.ReleaseRefused;
            }
            var notice = await _svc.FetchServerNoticeAsync(Ct).ConfigureAwait(false);
            if (notice != null)
            {
                _ui.Error(notice.Title);
                _ui.Error(notice.Message);
                if (notice.UntilText() != null) _ui.Error(notice.UntilText());
                if (notice.InstallUrl != null) _ui.Error("Meanwhile you can get the mod here: " + notice.InstallUrl);
                _ui.Error("Already up to date? Start the game as usual (lotr-updater play).");
                return ExitCodes.DownloadsPaused;
            }
            _ui.Error(c.Error.Replace("Choose its folder in Settings.", "Start the updater with --game <folder>.").Replace("click Check again", "try again").Replace("Click Check again", "Try again"));
            if (_o.Verbose && c.ErrorDetail != null) _ui.Error("Details: " + c.ErrorDetail);
            return ExitCodes.Network;
        }

        private async Task<int> StatusAsync()
        {
            if (!Locate()) return ExitCodes.GameNotFound;
            var c = await CheckAsync().ConfigureAwait(false);
            if (c.Error != null) return await CheckFailedAsync(c).ConfigureAwait(false);
            PrintPlan(c);
            if (c.UpToDate)
            {
                _ui.Line("Your mod files are up to date.");
                return ExitCodes.Ok;
            }
            _ui.Line("An update is available: run lotr-updater install.");
            return ExitCodes.UpdateAvailable;
        }

        private async Task<int> InstallAsync()
        {
            if (!Locate()) return ExitCodes.GameNotFound;
            var c = await CheckAsync().ConfigureAwait(false);
            if (c.Error != null && c.RollbackOffered && _o.AcceptRollback && c.Manifest != null)
            {
                _svc.AcceptRollback(c.Manifest.ReleaseId);
                _ui.Line("Rollback to " + c.Manifest.ReleaseId + " accepted.");
                c = await CheckAsync().ConfigureAwait(false);
            }
            if (c.Error != null) return await CheckFailedAsync(c).ConfigureAwait(false);
            PrintPlan(c);
            if (!c.Plan.HasChanges)
            {
                _ui.Line("Your mod files are up to date. Nothing to do.");
                PrintModulesToTick(c);
                return ExitCodes.Ok;
            }
            return await ApplyAsync(c.Plan, "Install these changes?", c).ConfigureAwait(false);
        }

        /// <summary>Running game, blockers, the player's yes, the install, a quick re-check. Shared by install and verify --repair.</summary>
        private async Task<int> ApplyAsync(InstallPlan plan, string question, CheckResult check)
        {
            var running = _processes.RunningGameProcesses();
            if (running.Count > 0)
            {
                _ui.Error(PlayerErrors.GameRunningText(running.Select(r => r.Name)));
                return ExitCodes.GameRunning;
            }
            if (plan.IsBlocked)
            {
                _ui.Error("The update cannot start:");
                foreach (var b in plan.Issues.Where(i => i.Severity == IssueSeverity.Blocker)) _ui.Error("  x " + b.Text);
                return ExitCodes.InstallFailed;
            }
            if (!_o.Yes && !Confirm(question))
            {
                _ui.Line("Nothing was changed.");
                return ExitCodes.Cancelled;
            }

            string backup;
            try
            {
                backup = await _svc.InstallAsync(plan, new ConsoleProgress(_ui), Ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Ct.IsCancellationRequested)
            {
                _ui.EndProgress();
                _ui.Error("Cancelled. " + PlayerErrors.NothingChanged);
                return ExitCodes.Cancelled;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !Ct.IsCancellationRequested)
            {
                _ui.EndProgress();
                var p = PlayerErrors.ForInstall(ex);
                _svc.Log.Error("Install failed: " + (p.Detail ?? p.Message));
                if (_processes.RunningGameProcesses().Count > 0)
                {
                    _ui.Error(PlayerErrors.GameRunningText(_processes.RunningGameProcesses().Select(r => r.Name)));
                    return ExitCodes.GameRunning;
                }
                _ui.Error(p.Message);
                if (_o.Verbose && p.Detail != null) _ui.Error("Details: " + p.Detail);
                return ExitCodes.InstallFailed;
            }
            _ui.EndProgress();
            _ui.Line("Done: release " + plan.Manifest.ReleaseId + " is installed.");
            _ui.Line("Replaced files were saved in " + backup);

            // Quick re-check with the hash cache (a full re-hash is "verify").
            var again = await _svc.CheckAsync(null, Ct).ConfigureAwait(false);
            if (again.Error == null && again.Plan.HasChanges)
            {
                _ui.Error("Some files still differ from the release. Run lotr-updater verify --repair.");
                return ExitCodes.VerifyDifferences;
            }
            PrintModulesToTick(again.Error == null ? again : check);
            return ExitCodes.Ok;
        }

        private void PrintModulesToTick(CheckResult c)
        {
            var mods = _svc.ModulesToTick(c?.Manifest);
            if (mods.Count > 0) _ui.Line("In the Bannerlord launcher (Multiplayer), tick: " + string.Join(", ", mods) + ".");
        }

        private void PrintPlan(CheckResult c)
        {
            var plan = c.Plan;
            _ui.Line($"Release {c.Manifest.ReleaseId} ({c.Manifest.Channel} channel, published {c.Manifest.PublishedAt}).");
            if (c.Signature != null) _ui.Line("  " + c.Signature.Message);
            var notes = c.Manifest.NotesText();
            if (!string.IsNullOrWhiteSpace(notes)) _ui.Line("  Notes: " + notes.Trim().Replace("\n", "\n         "));
            foreach (var m in plan.Modules)
            {
                var state = m.IsNewFolder ? "new" : m.HasChanges ? "update" : "up to date";
                _ui.Line($"  {m.Module.DisplayName()} {m.Module.Version} ({state}) in {m.TargetFolder}");
                if (m.HasChanges)
                {
                    _ui.Line($"      {m.Count(FileActionKind.Add)} to add, {m.Count(FileActionKind.Replace)} to replace, {m.Count(FileActionKind.Remove)} to remove, {m.UnchangedFiles} unchanged");
                    int limit = _o.ListFiles ? int.MaxValue : 8;
                    foreach (var kind in new[] { FileActionKind.Add, FileActionKind.Replace, FileActionKind.Remove })
                    {
                        var list = m.Actions.Where(a => a.Kind == kind).ToList();
                        var sign = kind == FileActionKind.Add ? "+" : kind == FileActionKind.Replace ? "~" : "-";
                        foreach (var a in list.Take(limit)) _ui.Line($"      {sign} {a.RelPath}{(kind == FileActionKind.Remove ? "" : "  (" + Units.Bytes(a.Size) + ")")}");
                        if (list.Count > limit) _ui.Line($"      {sign} ... and {list.Count - limit} more (--list shows all)");
                    }
                }
                if (m.Extra.Count > 0) _ui.Line($"      {m.Extra.Count} file(s) of yours in this folder are kept as they are.");
            }
            if (plan.HasChanges)
            {
                var dl = $"  Download: {Units.Bytes(plan.DownloadBytes)} in {plan.Downloads.Count} object(s)";
                if (plan.ReusedLocalBytes > 0) dl += $"; reused from your disk: {Units.Bytes(plan.ReusedLocalBytes)}";
                if (plan.WorkshopReusedBytes > 0) dl += $" (Steam Workshop: {Units.Bytes(plan.WorkshopReusedBytes)})";
                _ui.Line(dl + ".");
                _ui.Line("  Replaced and removed files go to " + plan.BackupPath);
            }
            foreach (var i in plan.Issues)
            {
                var mark = i.Severity == IssueSeverity.Blocker ? "x" : i.Severity == IssueSeverity.Warning ? "!" : "i";
                _ui.Line($"  {mark} {i.Text}" + (i.LinkUrl != null ? " (" + i.LinkUrl + ")" : ""));
            }
        }

        // ------------------------------------------------------------------ newer lotr-updater

        private bool _launcherChecked;

        /// <summary>
        /// After a status or install the server answered (exit 0 or 10), once per run: tells the player when a newer
        /// lotr-updater is on the Install page. Reads files/updater/linux/latest.json only (never the Windows updater's);
        /// downloads and replaces nothing (docs/LINUX.md, "Updating lotr-updater itself"). Never changes the exit code.
        /// </summary>
        private async Task<int> WithLauncherNoticeAsync(Task<int> command)
        {
            int code = await command.ConfigureAwait(false);
            if (_launcherChecked || (code != ExitCodes.Ok && code != ExitCodes.UpdateAvailable)) return code;
            _launcherChecked = true;
            var n = await new LinuxLauncherCheck(_svc.Endpoints, _svc.Fetcher, _svc.Config.TrustedKeysPem, _svc.Log, Version).CheckAsync(Ct).ConfigureAwait(false);
            if (n != null)
            {
                _ui.Line();
                _ui.Line($"A new version of lotr-updater is available: {n.Version} (you have {n.CurrentVersion}).");
                _ui.Line($"Get it from {n.DownloadPage} (Linux / Steam Deck), extract it and run ./lotr-updater from the new folder");
                _ui.Line("(then ./lotr-updater shortcut again if you use the menu entry). Your settings and the installed mod stay as they are.");
            }
            return code;
        }

        // ------------------------------------------------------------------ verify

        private async Task<int> VerifyAsync()
        {
            if (!Locate()) return ExitCodes.GameNotFound;
            _ui.Line("Verifying every installed file (this reads all of them; it can take a few minutes)...");
            VerifyReport rep;
            try
            {
                rep = await _svc.VerifyAsync(null, Ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !Ct.IsCancellationRequested)
            {
                var p = PlayerErrors.ForVerify(ex);
                _ui.Error(p.Message.Replace("Click Verify now again", "Try again").Replace("click Verify now again", "try again"));
                if (_o.Verbose && p.Detail != null) _ui.Error("Details: " + p.Detail);
                bool refused = ex is ManifestException || p.Kind == ProblemKind.ReleaseRefused;
                return refused ? ExitCodes.ReleaseRefused : ex is InstallException ? ExitCodes.InstallFailed : ExitCodes.Network;
            }
            _ui.Line($"Checked {rep.Checked} files: {rep.Ok} OK, {rep.Missing.Count} missing, {rep.Modified.Count} different, {rep.Extra.Count} extra (yours, kept).");
            int limit = _o.ListFiles ? int.MaxValue : 20;
            foreach (var x in rep.Missing.Take(limit)) _ui.Line("  missing:   " + x);
            foreach (var x in rep.Modified.Take(limit)) _ui.Line("  different: " + x);
            if (_o.ListFiles) foreach (var x in rep.Extra) _ui.Line("  extra:     " + x);
            foreach (var d in rep.Duplicates) _ui.Line("  ! " + d.Advice);
            if (rep.Healthy)
            {
                _ui.Line("Your install is healthy.");
                return ExitCodes.Ok;
            }
            if (!_o.Repair)
            {
                _ui.Line("Run lotr-updater verify --repair to download and put back what differs.");
                return ExitCodes.VerifyDifferences;
            }
            var check = new CheckResult { Manifest = rep.RepairPlan.Manifest, Plan = rep.RepairPlan };
            PrintPlan(check);
            var code = await ApplyAsync(rep.RepairPlan, "Repair these files?", check).ConfigureAwait(false);
            if (code != ExitCodes.Ok) return code;
            var after = await _svc.VerifyAsync(null, Ct).ConfigureAwait(false);
            _ui.Line(after.Healthy ? "Repaired: your install is healthy." : "Some files still differ after the repair. Send a support log (lotr-updater support).");
            return after.Healthy ? ExitCodes.Ok : ExitCodes.VerifyDifferences;
        }

        // ------------------------------------------------------------------ play

        private async Task<int> PlayAsync()
        {
            if (!Locate()) return ExitCodes.GameNotFound;
            var c = await CheckAsync().ConfigureAwait(false);
            if (c.Error != null) _ui.Line("Could not check for updates: " + c.Error);
            else if (c.Plan.HasChanges)
            {
                _ui.Line("An update of the mod is available: run lotr-updater install first, the event server only accepts the current version.");
                if (!_o.Yes && !Confirm("Start the game anyway?")) return ExitCodes.UpdateAvailable;
            }
            PrintModulesToTick(c.Error == null ? c : null);
            var launch = _host.LaunchGame ?? DefaultLaunch;
            var said = launch();
            if (said == null)
            {
                _ui.Error("Steam could not be started from here. Start Bannerlord from your Steam library.");
                return ExitCodes.GameNotFound;
            }
            _ui.Line(said);
            return ExitCodes.Ok;
        }

        private static string DefaultLaunch()
        {
            if (OsPath.IsWindows) return SteamLaunch.StartWindows() ? "Starting Bannerlord through Steam..." : null;
            var cmd = SteamLaunch.Choose(SteamLaunch.OnPath, SteamLaunch.FlatpakHasSteam);
            if (cmd == null || !SteamLaunch.Start(SteamLaunch.Detached(cmd, SteamLaunch.OnPath))) return null;
            return "Starting Bannerlord through Steam (" + cmd + ")...";
        }

        // ------------------------------------------------------------------ support

        private async Task<int> SupportAsync()
        {
            try { _svc.LocateGame(); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            var log = _svc.BuildSupportLog();
            _ui.Line("----- support log (your user name is masked) -----");
            _ui.Line(log.TrimEnd());
            _ui.Line("----- end of the support log -----");
            if (!_o.Yes && !Confirm("Send this log to the LotR support team?"))
            {
                _ui.Line("Not sent. The log files are in " + _svc.Paths.LogDir);
                return ExitCodes.Cancelled;
            }
            try
            {
                var code = await _svc.SendSupportAsync(log, Ct).ConfigureAwait(false);
                _ui.Line("Your support code: " + code.Code + " (valid until " + code.ExpiresAt + "). Paste it in your Discord ticket.");
                return ExitCodes.Ok;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !Ct.IsCancellationRequested)
            {
                _svc.Log.Warn("Support upload failed: " + PlayerErrors.Technical(ex));
                _ui.Error("The report could not be sent (the LotR server did not take it). Attach the newest file of " + _svc.Paths.LogDir + " to your Discord ticket instead.");
                return ExitCodes.Network;
            }
        }

        // ------------------------------------------------------------------ shortcut

        private int Shortcut()
        {
            if (OsPath.IsWindows && _host.ApplicationsFolder == null)
            {
                _ui.Error("The shortcut command is for Linux desktops. On Windows, use LotRMPUpdater.exe.");
                return ExitCodes.Usage;
            }
            var program = _host.ProgramPath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(program) || string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                _ui.Error("Run the lotr-updater program itself (not through dotnet) to add the shortcut.");
                return ExitCodes.Usage;
            }
            var folder = _host.ApplicationsFolder ??
                DesktopShortcut.ApplicationsFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable);
            string path;
            try
            {
                path = DesktopShortcut.Write(folder, Path.GetFullPath(program));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _ui.Error("The shortcut could not be written in " + folder + " (" + ex.Message + ").");
                return ExitCodes.InstallFailed;
            }
            _ui.Line("Added \"LotR MP Event updater\" to your application menu (" + path + ").");
            _ui.Line("It starts this copy: " + Path.GetFullPath(program));
            _ui.Line("If you move this folder, run lotr-updater shortcut again from the new place.");
            return ExitCodes.Ok;
        }

        // ------------------------------------------------------------------ prompts

        /// <summary>"menu" asked for by name is what the menu shortcut runs (a terminal window that closes when the program
        /// ends): when the menu cannot even start, keep the window open until the player has read why.</summary>
        private int MenuCouldNotStart(int code)
        {
            if (_o.Command == "menu" && _host.Terminal) Prompt("Press Enter to close this window.");
            return code;
        }

        private bool Confirm(string question)
        {
            Busy = false;
            try { return _ui.Confirm(question); }
            finally { Busy = true; }
        }

        private string Prompt(string question)
        {
            bool was = Busy;
            Busy = false;
            try { return _ui.Ask(question); }
            finally { Busy = was; }
        }

        /// <summary>Dev runs install into a FAKE game folder: the real game running on the dev PC must not block them.</summary>
        private sealed class NoGameProcesses : IProcessProbe
        {
            public IReadOnlyList<RunningProcessInfo> RunningGameProcesses() => new RunningProcessInfo[0];
        }
    }
}
