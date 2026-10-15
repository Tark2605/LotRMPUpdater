using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.Logging;
using LotR.Releases.Model;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Core.Schedule;
using Lotr.Updater.Core.Security;
using Lotr.Updater.Platform;

namespace Lotr.Updater.ViewModels
{
    public sealed class ModuleRow
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Detail { get; set; }
    }

    public sealed class FileRow
    {
        public string Kind { get; set; }
        public string Path { get; set; }
        public string Size { get; set; }
    }

    public sealed class PlanModuleRow
    {
        public string Title { get; set; }
        public string Folder { get; set; }
        public string Summary { get; set; }
        public bool Expanded { get; set; }
        public List<FileRow> Files { get; set; }
    }

    public sealed class Fact
    {
        public Fact(string label, string value) { Label = label; Value = value; }
        public string Label { get; }
        public string Value { get; }
    }

    public sealed class IssueRow
    {
        public string Text { get; set; }
        public string Severity { get; set; }
    }

    public sealed class MainViewModel : ObservableObject
    {
        private readonly UpdaterService _svc;
        private readonly Dispatcher _ui;
        private readonly StartOptions _options;
        private readonly DispatcherTimer _clock;
        private CancellationTokenSource _cts;
        private CheckResult _latest;
        private InstallPlan _shownPlan;
        private DateTime _nextAutoCheckUtc = DateTime.MaxValue;

        public MainViewModel(StartOptions options)
        {
            _options = options;
            _ui = Dispatcher.CurrentDispatcher;
            AppVersion = typeof(MainViewModel).Assembly.GetName().Version.ToString(3);
            var cfg = new UpdaterConfig { AppVersion = AppVersion };
            UpdaterPaths paths;
            if (options.Dev)
            {
                cfg.Dev = true;
                cfg.BaseUrl = options.Server;
                cfg.FixedGamePath = options.Game;
                cfg.TrustedKeysPem.AddRange(options.TrustPem);
                paths = new UpdaterPaths(options.Home);
            }
            else paths = UpdaterPaths.Default();
            _svc = new UpdaterService(cfg, paths, lockFinder: new RestartManagerLockFinder());
            _svc.Log.Line += l => _ui.BeginInvoke(new Action(() => AppendLog(l)));
            IsDev = options.Dev;

            NavigateCommand = new RelayCommand(p => Page = (Page)Enum.Parse(typeof(Page), p.ToString()));
            CheckCommand = new RelayCommand(() => _ = CheckAsync(true), () => !Busy && HasGame);
            ReviewCommand = new RelayCommand(() => { if (_latest?.Plan != null) ShowPlan(_latest.Plan, "Update to " + ReleaseLabel(_latest.Manifest)); }, () => !Busy && _latest?.Plan != null && _latest.Plan.HasChanges);
            InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !Busy && _shownPlan != null && _shownPlan.HasChanges && !_shownPlan.IsBlocked);
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Busy && _cts != null);
            LaunchCommand = new RelayCommand(Launch, () => HasGame && !Busy);
            VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), () => !Busy && HasGame);
            RepairCommand = new RelayCommand(() => { if (_repairPlan != null) ShowPlan(_repairPlan, "Repair my install"); }, () => !Busy && _repairPlan != null && _repairPlan.HasChanges);
            SendSupportCommand = new RelayCommand(() => _ = SendSupportAsync(), () => !Busy && !string.IsNullOrEmpty(SupportLog));
            CopyLogCommand = new RelayCommand(() => { try { Clipboard.SetText(SupportLog ?? ""); SupportStatus = "The log is in your clipboard."; } catch (Exception ex) { SupportStatus = ex.Message; } });
            RefreshLogCommand = new RelayCommand(RefreshSupportLog);
            OpenLogsCommand = new RelayCommand(() => OpenFolder(_svc.Paths.LogDir));
            OpenBackupCommand = new RelayCommand(() => OpenFolder(_svc.Paths.BackupRoot));
            OpenModulesCommand = new RelayCommand(() => { if (_svc.Game != null) OpenFolder(_svc.Game.ModulesPath); });
            BrowseGameCommand = new RelayCommand(BrowseGame, () => !Busy && !IsDev);
            AcceptRollbackCommand = new RelayCommand(() =>
            {
                if (_latest?.Manifest == null) return;
                _svc.AcceptRollback(_latest.Manifest.ReleaseId);
                RollbackOffered = false;
                _ = CheckAsync(true);
            }, () => !Busy && RollbackOffered);
            UninstallCommand = new RelayCommand(() => new UninstallWindow(_options) { Owner = Application.Current.MainWindow }.ShowDialog());

            _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => Tick(), _ui);
            UpdateCountdown();
            FillAbout();
        }

        public UpdaterService Service => _svc;

        // ------------------------------------------------------------ shell
        private Page _page = Page.Home;
        public Page Page { get => _page; set { if (Set(ref _page, value) && value == Page.Support) RefreshSupportLog(); } }
        public bool IsDev { get; }
        public string AppVersion { get; }
        public string DevBanner => IsDev ? "DEV MODE — local test server " + _options.Server + " · fake game " + _options.Game : null;

        private bool _busy;
        public bool Busy { get => _busy; set { if (Set(ref _busy, value)) CommandRequery(); } }
        private string _busyText;
        public string BusyText { get => _busyText; set => Set(ref _busyText, value); }

        public RelayCommand NavigateCommand { get; }
        public RelayCommand CheckCommand { get; }
        public RelayCommand ReviewCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand LaunchCommand { get; }
        public RelayCommand VerifyCommand { get; }
        public RelayCommand RepairCommand { get; }
        public RelayCommand SendSupportCommand { get; }
        public RelayCommand CopyLogCommand { get; }
        public RelayCommand RefreshLogCommand { get; }
        public RelayCommand OpenLogsCommand { get; }
        public RelayCommand OpenBackupCommand { get; }
        public RelayCommand OpenModulesCommand { get; }
        public RelayCommand BrowseGameCommand { get; }
        public RelayCommand UninstallCommand { get; }
        public RelayCommand AcceptRollbackCommand { get; }
        private bool _rollbackOffered;
        public bool RollbackOffered { get => _rollbackOffered; set => Set(ref _rollbackOffered, value); }

        private static void CommandRequery() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        // ------------------------------------------------------------ home: game
        private bool _hasGame;
        public bool HasGame { get => _hasGame; set => Set(ref _hasGame, value); }
        private string _gameTitle = "Looking for Bannerlord…";
        public string GameTitle { get => _gameTitle; set => Set(ref _gameTitle, value); }
        private string _gameStore;
        public string GameStore { get => _gameStore; set => Set(ref _gameStore, value); }
        private string _gamePath;
        public string GamePath { get => _gamePath; set => Set(ref _gamePath, value); }
        private string _gameNote;
        public string GameNote { get => _gameNote; set => Set(ref _gameNote, value); }

        // ------------------------------------------------------------ home: mod status
        private string _statusKind = "checking";
        /// <summary>checking | ok | update | missing | error</summary>
        public string StatusKind { get => _statusKind; set => Set(ref _statusKind, value); }
        private string _statusTitle = "Checking…";
        public string StatusTitle { get => _statusTitle; set => Set(ref _statusTitle, value); }
        private string _statusDetail;
        public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }
        private string _releaseNotes;
        public string ReleaseNotes { get => _releaseNotes; set => Set(ref _releaseNotes, value); }
        private string _signatureText;
        public string SignatureText { get => _signatureText; set => Set(ref _signatureText, value); }
        public ObservableCollection<ModuleRow> Modules { get; } = new ObservableCollection<ModuleRow>();
        private string _tickHint;
        public string TickHint { get => _tickHint; set => Set(ref _tickHint, value); }
        private string _launchHint;
        public string LaunchHint { get => _launchHint; set => Set(ref _launchHint, value); }
        private string _lastCheck;
        public string LastCheck { get => _lastCheck; set => Set(ref _lastCheck, value); }

        // ------------------------------------------------------------ home: next battle
        private string _countdown;
        public string Countdown { get => _countdown; set => Set(ref _countdown, value); }
        private string _nextBattle;
        public string NextBattle { get => _nextBattle; set => Set(ref _nextBattle, value); }
        private bool _battleSoon;
        public bool BattleSoon { get => _battleSoon; set => Set(ref _battleSoon, value); }

        private void Tick()
        {
            UpdateCountdown();
            if (!Busy && HasGame && DateTime.UtcNow >= _nextAutoCheckUtc) _ = CheckAsync(false);
        }

        private void UpdateCountdown()
        {
            var now = DateTime.UtcNow;
            var start = EventSchedule.NextStartUtc(now);
            var paris = TimeZoneInfo.ConvertTimeFromUtc(start, EventSchedule.Paris);
            var local = start.ToLocalTime();
            var en = CultureInfo.GetCultureInfo("en-GB");
            NextBattle = paris.ToString("dddd d MMMM", en) + " · " + paris.ToString("HH:mm", en) + " Paris time" +
                         (Math.Abs((local - paris).TotalMinutes) > 1 ? " (" + local.ToString("HH:mm", en) + " on your clock)" : "");
            Countdown = EventSchedule.Countdown(now);
            BattleSoon = start - now < TimeSpan.FromHours(6);
        }

        // ------------------------------------------------------------ start-up and check
        public async Task StartAsync()
        {
            _svc.Log.Info($"LotRMPUpdater {AppVersion} started{(IsDev ? " in DEV mode against " + _options.Server : "")}.");
            _svc.Log.Prune();
            if (!IsDev && _options.TourDir == null)
                await Task.Run(() => SelfInstall.Ensure(_svc.Paths, AppVersion, _svc.Log));
            Busy = true;
            BusyText = "Looking for Bannerlord…";
            try { await Task.Run(() => _svc.LocateGame()); }
            finally { Busy = false; }
            RefreshGame();
            ShowInstalled();
            _clock.Start();
            if (HasGame) await CheckAsync(false);
        }

        private void RefreshGame()
        {
            var g = _svc.Game;
            HasGame = g != null;
            if (g == null)
            {
                GameTitle = "Bannerlord not found";
                GameStore = null;
                GamePath = null;
                GameNote = (_svc.LastSearch?.Message ?? "") + " Use Settings › Game folder to pick it.";
                StatusKind = "error";
                StatusTitle = "Waiting for the game folder";
                StatusDetail = null;
                return;
            }
            GameTitle = "Mount & Blade II: Bannerlord " + (g.GameVersion ?? "(version unknown)");
            GameStore = g.StoreLabel;
            GamePath = g.Root;
            GameNote = g.Writable == false ? "Windows does not let this app write in the Modules folder. See Update plan for what to do." : null;
            GamePathSetting = g.Root;
            var target = GameLauncher.Describe(g);
            LaunchHint = g.Store == LotR.GameDetection.GameStore.Steam ? "Opens Bannerlord through Steam (official launcher)."
                : g.Store == LotR.GameDetection.GameStore.Epic ? "Opens Bannerlord through the Epic Games Launcher."
                : "Opens the official Bannerlord launcher.";
        }

        private void ShowInstalled()
        {
            Modules.Clear();
            var m = _svc.InstalledManifest;
            if (m != null)
                foreach (var x in m.Modules)
                    Modules.Add(new ModuleRow { Name = x.DisplayName(), Version = x.Version, Detail = "Modules\\" + x.Folder + " · " + Units.Bytes(x.TotalSize) });
            var tick = _svc.ModulesToTick(_latest?.Manifest ?? m);
            TickHint = tick.Count == 0 ? null : "In the launcher's Multiplayer tab, tick " + Join(tick) + " (this app never changes the launcher's settings).";
        }

        private static string Plural(int n, string word) => n + " " + word + (n == 1 ? "" : "s");

        private static string Join(IReadOnlyList<string> xs) =>
            xs.Count == 1 ? xs[0] : string.Join(", ", xs.Take(xs.Count - 1)) + " and " + xs[xs.Count - 1];

        private static string ReleaseLabel(ReleaseManifest m) =>
            m == null ? "" : string.Join(" + ", m.Modules.Select(x => x.DisplayName() + " " + x.Version));

        public async Task CheckAsync(bool userAsked)
        {
            if (Busy || !HasGame) return;
            Busy = true;
            StatusKind = "checking";
            StatusTitle = "Checking for updates…";
            StatusDetail = null;
            var status = new Progress<string>(s => BusyText = s);
            try
            {
                _latest = await Task.Run(() => _svc.CheckAsync(status, CancellationToken.None));
                ApplyCheck(_latest);
                if (_latest.Error == null && _latest.Plan.HasChanges && !userAsked)
                    await PreDownloadAsync();
            }
            finally
            {
                Busy = false;
                BusyText = null;
                // Weekly rhythm: every 2 h, every 20 min on battle day before the start.
                var now = DateTime.UtcNow;
                var start = EventSchedule.NextStartUtc(now);
                _nextAutoCheckUtc = now.AddMinutes(start - now < TimeSpan.FromHours(12) ? 20 : 120);
            }
        }

        private void ApplyCheck(CheckResult r)
        {
            LastCheck = "Last check " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            RollbackOffered = r.RollbackOffered;
            if (r.RollbackOffered)
            {
                StatusKind = "error";
                StatusTitle = "The server offers an older release";
                StatusDetail = r.Error;
                return;
            }
            if (r.Error != null)
            {
                StatusKind = "error";
                StatusTitle = "Could not check for updates";
                StatusDetail = r.Error;
                return;
            }
            SignatureText = r.Signature?.Message;
            ReleaseNotes = r.Manifest.NotesText();
            var p = r.Plan;
            if (!p.HasChanges)
            {
                StatusKind = "ok";
                StatusTitle = "Ready for battle";
                StatusDetail = "Your mod files match the current release (" + ReleaseLabel(r.Manifest) + ").";
            }
            else
            {
                bool fresh = p.Modules.All(m => m.IsNewFolder);
                StatusKind = fresh ? "missing" : "update";
                StatusTitle = fresh ? "The mod is not installed yet" : "An update is available";
                var changes = p.Modules.Sum(m => m.Actions.Count);
                StatusDetail = ReleaseLabel(r.Manifest) + " — " + changes + " file" + (changes == 1 ? "" : "s") + " to change, " +
                               (p.DownloadBytes > 0 ? Units.Bytes(p.DownloadBytes) + " to download." : "already downloaded.") +
                               (p.IsBlocked ? " Something needs your attention first: open the plan." : "");
            }
            ShowInstalled();
            CommandRequery();
        }

        private async Task PreDownloadAsync()
        {
            if (!_svc.State.PreDownload) return;
            var due = PreDownloadPolicy.DueUtc(_svc.State);
            if (due.HasValue && due.Value > DateTime.UtcNow)
            {
                _svc.Log.Detail("Background pre-download planned at " + due.Value.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture) + " (random delay to spread the load).");
                return;
            }
            Busy = true;
            _cts = new CancellationTokenSource();
            var prog = new Progress<EngineProgress>(e => BusyText = "Pre-downloading in the background… " + (e.Total > 0 ? (e.Fraction * 100).ToString("0") + " %" : ""));
            try
            {
                if (await Task.Run(() => _svc.PreDownloadIfDueAsync(_latest, prog, _cts.Token)))
                    StatusDetail += " Already downloaded in the background: installing takes seconds.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _svc.Log.Warn("Pre-download paused: " + ex.Message); }
            finally { Busy = false; BusyText = null; _cts = null; }
        }

        // ------------------------------------------------------------ plan
        private string _planTitle;
        public string PlanTitle { get => _planTitle; set => Set(ref _planTitle, value); }
        public ObservableCollection<PlanModuleRow> PlanModules { get; } = new ObservableCollection<PlanModuleRow>();
        public ObservableCollection<Fact> PlanFacts { get; } = new ObservableCollection<Fact>();
        public ObservableCollection<IssueRow> PlanIssues { get; } = new ObservableCollection<IssueRow>();
        private bool _hasPlan;
        public bool HasPlan { get => _hasPlan; set => Set(ref _hasPlan, value); }
        private string _planNote;
        public string PlanNote { get => _planNote; set => Set(ref _planNote, value); }

        public void ShowPlan(InstallPlan p, string title)
        {
            _shownPlan = p;
            HasPlan = true;
            PlanTitle = title;
            PlanModules.Clear();
            foreach (var m in p.Modules)
            {
                var rows = m.Actions.OrderBy(a => a.Kind).ThenBy(a => a.RelPath, StringComparer.OrdinalIgnoreCase).Select(a => new FileRow
                {
                    Kind = a.Kind == FileActionKind.Add ? "add" : a.Kind == FileActionKind.Replace ? "replace" : "remove",
                    Path = a.RelPath,
                    Size = a.Kind == FileActionKind.Remove ? Units.Bytes(a.OldSize)
                         : a.Source == LotR.Releases.Install.FileAction.CopyLocal ? Units.Bytes(a.Size) + " · copied from your PC"
                         : a.Source == LotR.Releases.Install.FileAction.Rebuild ? Units.Bytes(a.Size) + $" · {a.Chunks.Count(c => c.Local != null)}/{a.Chunks.Count} parts reused"
                         : Units.Bytes(a.Size),
                }).ToList();
                PlanModules.Add(new PlanModuleRow
                {
                    Title = m.Module.DisplayName() + " " + m.Module.Version + (m.IsNewFolder ? " (new)" : ""),
                    Folder = m.TargetFolder,
                    Summary = m.HasChanges
                        ? $"{m.Count(FileActionKind.Add)} to add · {m.Count(FileActionKind.Replace)} to replace · {m.Count(FileActionKind.Remove)} to remove · {m.UnchangedFiles} unchanged"
                        : $"Nothing to do · {m.UnchangedFiles} files already correct",
                    Expanded = m.HasChanges && rows.Count <= 40,
                    Files = rows,
                });
            }
            PlanFacts.Clear();
            PlanFacts.Add(new Fact("Download", p.DownloadBytes > 0 ? Units.Bytes(p.DownloadBytes) + " in " + Plural(p.Downloads.Count, "piece") + (p.ReusedLocalChunks > 0 ? $" ({Units.Bytes(p.ReusedLocalBytes)} reused from your current files)" : "") : "Nothing — already on this PC"));
            PlanFacts.Add(new Fact("Temporary space", Units.Bytes(p.StagingBytes) + " inside each module folder (.lotr-staging), removed at the end"));
            PlanFacts.Add(new Fact("Backup of replaced files", p.BackupBytes > 0 ? Units.Bytes(p.BackupBytes) + " → " + p.BackupPath : "None needed (nothing is replaced)"));
            PlanFacts.Add(new Fact("Game folder", p.Game.ModulesPath));
            PlanFacts.Add(new Fact("Release", p.Manifest.ReleaseId + " · " + (p.SignatureStatus ?? "")));
            PlanIssues.Clear();
            foreach (var i in p.Issues.OrderByDescending(x => x.Severity))
                PlanIssues.Add(new IssueRow { Text = i.Text, Severity = i.Severity.ToString().ToLowerInvariant() });
            PlanNote = p.HasChanges
                ? "Nothing has been changed yet. Files are downloaded and checked first; your game folder is only touched at the very end, in a few seconds, and everything is put back if anything goes wrong."
                : "Everything already matches. Nothing to do.";
            Page = Page.Plan;
            CommandRequery();
        }

        // ------------------------------------------------------------ progress
        private double _progress;
        public double ProgressValue { get => _progress; set => Set(ref _progress, value); }
        private string _progressTitle = "Nothing running";
        public string ProgressTitle { get => _progressTitle; set => Set(ref _progressTitle, value); }
        private string _progressDetail;
        public string ProgressDetail { get => _progressDetail; set => Set(ref _progressDetail, value); }
        private string _progressResult;
        public string ProgressResult { get => _progressResult; set => Set(ref _progressResult, value); }
        private string _progressResultKind;
        public string ProgressResultKind { get => _progressResultKind; set => Set(ref _progressResultKind, value); }
        private readonly StringBuilder _log = new StringBuilder();
        private string _logText = "";
        public string LogText { get => _logText; set => Set(ref _logText, value); }
        public event Action LogAppended;

        private void AppendLog(LogLine l)
        {
            if (l.Level == LogLevel.Detail && !IsDev) { }
            _log.AppendLine(l.ToString());
            if (_log.Length > 400_000) _log.Remove(0, _log.Length - 300_000);
            LogText = _log.ToString();
            LogAppended?.Invoke();
        }

        public async Task InstallAsync()
        {
            var plan = _shownPlan;
            if (plan == null || Busy) return;
            Page = Page.Progress;
            Busy = true;
            BusyText = "Installing…";
            ProgressResult = null;
            ProgressResultKind = null;
            ProgressValue = 0;
            ProgressTitle = "Starting…";
            _cts = new CancellationTokenSource();
            var sw = Stopwatch.StartNew();
            var prog = new Progress<EngineProgress>(OnProgress);
            try
            {
                var backup = await Task.Run(() => _svc.InstallAsync(plan, prog, _cts.Token));
                ProgressValue = 1;
                ProgressTitle = "Installed";
                ProgressResultKind = "ok";
                ProgressResult = $"Done in {sw.Elapsed.TotalSeconds:0} s. " + (plan.BackupBytes > 0 ? "The replaced files are kept in " + backup + " until the next update." : "") +
                                 " " + (TickHint ?? "") + " You can launch the game.";
                _shownPlan = null;
                HasPlan = false;
            }
            catch (OperationCanceledException)
            {
                ProgressTitle = "Cancelled";
                ProgressResultKind = "warn";
                ProgressResult = "Cancelled. Your game folder was not changed. What was already downloaded is kept, so the next try continues where it stopped.";
            }
            catch (Exception ex) when (ex is InstallException || ex is IOException || ex is UnauthorizedAccessException)
            {
                ProgressTitle = "The update did not complete";
                ProgressResultKind = "error";
                ProgressResult = ex.Message + " If it keeps happening, open Support and send the log.";
                _svc.Log.Error(ex.Message);
            }
            finally
            {
                Busy = false;
                BusyText = null;
                _cts = null;
            }
            await CheckAsync(true);
        }

        private void OnProgress(EngineProgress e)
        {
            ProgressValue = e.Fraction;
            switch (e.Phase)
            {
                case EnginePhase.Queue: ProgressTitle = "Waiting for a download slot"; ProgressDetail = e.Message; break;
                case EnginePhase.Downloading:
                    ProgressTitle = "Downloading";
                    var eta = e.BytesPerSecond > 1 ? TimeSpan.FromSeconds((e.Total - e.Done) / e.BytesPerSecond) : (TimeSpan?)null;
                    ProgressDetail = $"{Units.Bytes(e.Done)} of {Units.Bytes(e.Total)}" + (e.BytesPerSecond > 1 ? $" · {Units.Bytes((long)e.BytesPerSecond)}/s" : "") +
                                     (eta.HasValue && e.Done < e.Total ? $" · about {(eta.Value.TotalMinutes >= 1 ? ((int)eta.Value.TotalMinutes) + " min " : "")}{eta.Value.Seconds} s left" : "");
                    break;
                case EnginePhase.Preparing: ProgressTitle = "Preparing the new files (checked one by one)"; ProgressDetail = e.Message; break;
                case EnginePhase.Swapping: ProgressTitle = "Putting the new files in place"; ProgressDetail = e.Message; break;
                case EnginePhase.Finishing: ProgressTitle = "Finishing"; ProgressDetail = e.Message; break;
                case EnginePhase.Done: ProgressTitle = "Installed"; ProgressDetail = null; break;
            }
        }

        // ------------------------------------------------------------ verify
        private string _verifySummary = "Re-reads every mod file, compares it with the release, removes Windows' \"downloaded from the internet\" mark (which can stop the game from loading a mod DLL) and looks for duplicate copies of the mod.";
        public string VerifySummary { get => _verifySummary; set => Set(ref _verifySummary, value); }
        private string _verifyKind;
        public string VerifyKind { get => _verifyKind; set => Set(ref _verifyKind, value); }
        public ObservableCollection<IssueRow> VerifyRows { get; } = new ObservableCollection<IssueRow>();
        private InstallPlan _repairPlan;

        public async Task VerifyAsync()
        {
            if (Busy) return;
            Busy = true;
            BusyText = "Verifying every file…";
            VerifyRows.Clear();
            VerifyKind = "checking";
            VerifySummary = "Checking every file…";
            var status = new Progress<string>(s => VerifySummary = s);
            try
            {
                var r = await Task.Run(() => _svc.VerifyAsync(status, CancellationToken.None));
                _repairPlan = r.RepairPlan;
                VerifyKind = r.Healthy && r.Duplicates.Count == 0 ? "ok" : "update";
                VerifySummary = r.Healthy
                    ? $"All {r.Checked} files are correct." + (r.MarkOfTheWebCleared > 0 ? $" Removed the internet mark from {Plural(r.MarkOfTheWebCleared, "file")}." : "")
                    : $"{r.Ok} of {r.Checked} files are correct. {r.Missing.Count} missing, {r.Modified.Count} different. \"Repair\" shows exactly what it will fix before doing anything.";
                foreach (var x in r.Missing.Take(200)) VerifyRows.Add(new IssueRow { Severity = "missing", Text = x });
                foreach (var x in r.Modified.Take(200)) VerifyRows.Add(new IssueRow { Severity = "different", Text = x });
                foreach (var x in r.Extra.Take(200)) VerifyRows.Add(new IssueRow { Severity = "extra", Text = x + "  (not part of the release; Repair removes it, with a backup)" });
                foreach (var d in r.Duplicates) VerifyRows.Add(new IssueRow { Severity = "duplicate", Text = d.Id + " is also in " + d.Path + " (" + d.Where + "). Tick only one copy in the launcher, or remove the other one." });
                if (r.MarkOfTheWebCleared > 0) VerifyRows.Add(new IssueRow { Severity = "fixed", Text = $"Removed the \"downloaded from the internet\" mark from {Plural(r.MarkOfTheWebCleared, "file")} (Windows can refuse to load a mod DLL that carries it)." });
            }
            catch (Exception ex) when (ex is InstallException || ex is IOException || ex is Core.Net.HttpFetchException || ex is UnauthorizedAccessException)
            {
                VerifyKind = "error";
                VerifySummary = ex.Message;
            }
            finally { Busy = false; BusyText = null; }
        }

        // ------------------------------------------------------------ support
        private string _supportLog;
        public string SupportLog { get => _supportLog; set => Set(ref _supportLog, value); }
        private string _supportCode;
        public string SupportCode { get => _supportCode; set => Set(ref _supportCode, value); }
        private string _supportStatus;
        public string SupportStatus { get => _supportStatus; set => Set(ref _supportStatus, value); }

        private void RefreshSupportLog()
        {
            try { SupportLog = _svc.BuildSupportLog(); }
            catch (Exception ex) { SupportLog = "(could not build the log: " + ex.Message + ")"; }
        }

        public async Task SendSupportAsync()
        {
            Busy = true;
            SupportStatus = "Sending the log above (and nothing else)…";
            try
            {
                RefreshSupportLog();
                var log = SupportLog;
                var code = await Task.Run(() => _svc.SendSupportAsync(log, CancellationToken.None));
                SupportCode = code.Code;
                DateTime exp;
                SupportStatus = "Valid for one use" + (DateTime.TryParse(code.ExpiresAt, null, DateTimeStyles.RoundtripKind, out exp) ? ", until " + exp.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.GetCultureInfo("en-GB")) : "") + ".";
                _svc.Log.Info("Support log sent, code " + code.Code);
            }
            catch (Exception ex) when (ex is Core.Net.HttpFetchException || ex is System.Net.Http.HttpRequestException || ex is FormatException)
            {
                SupportStatus = ex.Message + " You can also press \"Copy log\" and paste it in your ticket.";
            }
            finally { Busy = false; }
        }

        // ------------------------------------------------------------ settings
        private string _gamePathSetting;
        public string GamePathSetting { get => _gamePathSetting; set => Set(ref _gamePathSetting, value); }
        private string _settingsMessage;
        public string SettingsMessage { get => _settingsMessage; set => Set(ref _settingsMessage, value); }

        public bool IsStable
        {
            get => _svc.State.Channel != "beta";
            set { if (value) SetChannel("stable"); }
        }

        public bool IsBeta
        {
            get => _svc.State.Channel == "beta";
            set { if (value) SetChannel("beta"); }
        }

        private void SetChannel(string c)
        {
            if (_svc.State.Channel == c) return;
            _svc.State.Channel = c;
            _svc.State.PendingReleaseId = null;
            _svc.SaveState();
            _svc.Log.Info("Channel changed to " + c);
            Raise(nameof(IsStable));
            Raise(nameof(IsBeta));
            _ = CheckAsync(true);
        }

        public bool PreDownload
        {
            get => _svc.State.PreDownload;
            set
            {
                if (_svc.State.PreDownload == value) return;
                _svc.State.PreDownload = value;
                _svc.SaveState();
                _svc.Log.Info("Background pre-download " + (value ? "on" : "off"));
                Raise();
            }
        }

        private void BrowseGame()
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the Bannerlord folder (the one that contains 'bin' and 'Modules')", ShowNewFolderButton = false })
            {
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var r = _svc.ChooseGame(d.SelectedPath);
                SettingsMessage = r.Found.Count > 0 ? "Using " + r.Found[0].Root : r.Message;
                if (r.Found.Count > 0) { RefreshGame(); _ = CheckAsync(true); }
            }
        }

        // ------------------------------------------------------------ about
        public ObservableCollection<string> Endpoints { get; } = new ObservableCollection<string>();
        public ObservableCollection<Fact> Folders { get; } = new ObservableCollection<Fact>();
        public ObservableCollection<string> Fingerprints { get; } = new ObservableCollection<string>();
        public ObservableCollection<IssueRow> Scans { get; } = new ObservableCollection<IssueRow>();

        private void FillAbout()
        {
            foreach (var e in _svc.Endpoints.Describe()) Endpoints.Add(e);
            Folders.Add(new Fact("Mod files", "<game>\\Modules\\<module folder> only — for example LotRMPEvent and LotRMPCore"));
            Folders.Add(new Fact("Its own data", _svc.Paths.Home + "  (app, download cache, logs, settings, one backup)"));
            Folders.Add(new Fact("Registry", "HKCU\\" + SelfInstall.UninstallKey + "  (only the \"Installed apps\" entry)"));
            Folders.Add(new Fact("Start menu", "one shortcut: " + SelfInstall.DisplayName));
            if (_svc.Verifier.TrustedFingerprints.Count == 0) Fingerprints.Add("No release key embedded yet (placeholder): every release will be refused.");
            foreach (var f in _svc.Verifier.TrustedFingerprints) Fingerprints.Add(f);
        }

        public async Task LoadScansAsync()
        {
            var id = _latest?.Manifest?.ReleaseId ?? _svc.State.InstalledReleaseId;
            if (id == null) return;
            var list = await _svc.FetchSecurityAsync(id, CancellationToken.None);
            Scans.Clear();
            foreach (var s in list) Scans.Add(new IssueRow { Severity = s.Kind, Text = s.Verdict + (s.ScannedAt != null ? " · " + s.ScannedAt : "") + (s.Url != null ? " · " + s.Url : "") });
        }

        // ------------------------------------------------------------ launch
        private void Launch()
        {
            var g = _svc.Game;
            var target = GameLauncher.Describe(g);
            if (_latest?.Plan != null && _latest.Plan.HasChanges)
            {
                var r = MessageBox.Show("An update of the mod is available and not installed yet. You may not be able to join the event server with the old version.\n\nLaunch anyway?",
                    "LotR MP Event", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
            }
            if (IsDev)
            {
                _svc.Log.Info("DEV MODE: would open " + target + " (not launched: fake game).");
                MessageBox.Show("Dev mode: the real launcher is not opened.\nWould open: " + target, "LotR MP Event", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                GameLauncher.Launch(g);
                _svc.Log.Info("Opened the official launcher: " + target);
            }
            catch (Exception ex)
            {
                _svc.Log.Error("Could not open the launcher: " + ex.Message);
                MessageBox.Show("Could not open the launcher (" + ex.Message + "). Start Bannerlord from your store as usual.", "LotR MP Event", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) { _svc.Log.Warn(ex.Message); }
        }
    }
}
