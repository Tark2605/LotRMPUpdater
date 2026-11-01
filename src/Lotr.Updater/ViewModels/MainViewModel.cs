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

    /// <summary>One module on the Home banner: name, version (old → new when it changes) and its status pill.</summary>
    public sealed class BannerModuleRow
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Pill { get; set; }
        /// <summary>ok | up | wait</summary>
        public string PillKind { get; set; }
    }

    /// <summary>One station of the road on Home (Download, Verify, Install, Ready).</summary>
    public sealed class StationRow : ObservableObject
    {
        public string Label { get; set; }
        public bool ShowLeft { get; set; }
        public bool ShowRight { get; set; }
        private string _state = "todo";
        /// <summary>todo | active | done</summary>
        public string State { get => _state; set => Set(ref _state, value); }
        private string _note;
        public string Note { get => _note; set => Set(ref _note, value); }
        private bool _leftLit;
        public bool LeftLit { get => _leftLit; set => Set(ref _leftLit, value); }
        private bool _rightLit;
        public bool RightLit { get => _rightLit; set => Set(ref _rightLit, value); }
    }

    public sealed class IssueRow
    {
        public string Text { get; set; }
        public string Severity { get; set; }
        public string LinkUrl { get; set; }
        public string LinkText { get; set; }
        public bool HasLink => !string.IsNullOrEmpty(LinkUrl);
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
            var cfg = new UpdaterConfig { AppVersion = AppVersion, CurrentExe = Program.ExePath };
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
            // Dev runs never touch the real Startup folder: a test folder inside the dev home stands in for it.
            _startup = new StartupRegistration(options.Dev ? Path.Combine(paths.Home, "startup-folder-test") : StartupRegistration.UserStartupFolder,
                paths.InstalledExe, new ComShortcutWriter());
            // Log lines reach the screen in batches (4 per second, below input and rendering): a busy install never floods the UI thread.
            _svc.Log.Line += l => _pendingLog.Enqueue(l);
            _logFlush = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (s, e) => FlushLog(), _ui);
            _logFlush.Start();
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
            CopyLogCommand = new RelayCommand(() => { try { Clipboard.SetText(SupportLog ?? ""); SupportStatus = "The log is in your clipboard."; } catch (Exception ex) { _svc.Log.Warn("Clipboard: " + ex.Message); SupportStatus = "The clipboard is busy (another program is using it). Try again in a moment."; } });
            // --- error reports (begin) ---
            ReportProblemCommand = new RelayCommand(p => OpenReport(p as string), _ => !Busy);
            CopySupportCodeCommand = new RelayCommand(() => { try { Clipboard.SetText(SupportCode ?? ""); SupportStatus = "Code copied. Paste it in your Discord ticket."; } catch (Exception ex) { _svc.Log.Warn("Clipboard: " + ex.Message); SupportStatus = "The clipboard is busy. Select the code and copy it by hand."; } }, () => !string.IsNullOrEmpty(SupportCode));
            // --- error reports (end) ---
            RefreshLogCommand = new RelayCommand(RefreshSupportLog);
            OpenLogsCommand = new RelayCommand(() => OpenFolder(_svc.Paths.LogDir));
            OpenBackupCommand = new RelayCommand(() => OpenFolder(_svc.Paths.BackupRoot));
            OpenLinkCommand = new RelayCommand(p => OpenWorkshopPage(p as string));
            OpenModulesCommand = new RelayCommand(() => { if (_svc.Game != null) OpenFolder(_svc.Game.ModulesPath); });
            BrowseGameCommand = new RelayCommand(BrowseGame, () => !Busy && !IsDev);
            AcceptRollbackCommand = new RelayCommand(() =>
            {
                if (_latest?.Manifest == null) return;
                _svc.AcceptRollback(_latest.Manifest.ReleaseId);
                RollbackOffered = false;
                _ = CheckAsync(true);
            }, () => !Busy && RollbackOffered);
            RestartToUpdateCommand = new RelayCommand(() => RestartToUpdate(false), () => !Busy && CanRestartToUpdate);
            RollbackUpdaterCommand = new RelayCommand(RollbackUpdater, () => !Busy && PreviousUpdaterVersion != null);
            UninstallCommand = new RelayCommand(() => new UninstallWindow(_options) { Owner = Application.Current.MainWindow }.ShowDialog());

            _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => Tick(), _ui);
            UpdateCountdown();
            FillAbout();
        }

        public UpdaterService Service => _svc;
        private readonly StartupRegistration _startup;
        private string _notifiedRelease;

        /// <summary>Balloon text for the tray icon (shown only while the window is hidden).</summary>
        public event Action<string, string> TrayNotification;
        /// <summary>The window must close for real (restart into the new updater).</summary>
        public event Action RestartRequested;
        /// <summary>The window is hidden and the tray icon shows.</summary>
        public bool InTray { get; set; }

        // ------------------------------------------------------------ shell
        private Page _page = Page.Home;
        public Page Page { get => _page; set { if (Set(ref _page, value) && value == Page.Support) RefreshSupportLog(); } }
        public bool IsDev { get; }
        public string AppVersion { get; }
        public string DevBanner => IsDev ? "DEV MODE — local test server " + _options.Server + " · fake game " + _options.Game : null;

        private bool _busy;
        public bool Busy { get => _busy; set { if (Set(ref _busy, value)) { CommandRequery(); RefreshLook(); } } }
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
        /// <summary>"Send a report to the team" in every error box: opens the Support page on this problem (nothing is sent yet).</summary>
        public RelayCommand ReportProblemCommand { get; }
        public RelayCommand CopySupportCodeCommand { get; }
        public RelayCommand CopyLogCommand { get; }
        public RelayCommand RefreshLogCommand { get; }
        public RelayCommand OpenLogsCommand { get; }
        public RelayCommand OpenBackupCommand { get; }
        public RelayCommand OpenModulesCommand { get; }
        public RelayCommand BrowseGameCommand { get; }
        public RelayCommand UninstallCommand { get; }
        public RelayCommand OpenLinkCommand { get; }
        public RelayCommand AcceptRollbackCommand { get; }
        public RelayCommand RestartToUpdateCommand { get; }
        public RelayCommand RollbackUpdaterCommand { get; }
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
        public string GameNote { get => _gameNote; set { if (Set(ref _gameNote, value)) RefreshLook(); } }

        // ------------------------------------------------------------ home: mod status
        private string _statusKind = "checking";
        /// <summary>checking | ok | update | missing | error</summary>
        public string StatusKind { get => _statusKind; set { if (Set(ref _statusKind, value)) RefreshLook(); } }
        private string _statusTitle = "Checking…";
        public string StatusTitle { get => _statusTitle; set { if (Set(ref _statusTitle, value)) RefreshLook(); } }
        private string _statusDetail;
        public string StatusDetail { get => _statusDetail; set { if (Set(ref _statusDetail, value)) RefreshLook(); } }
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
            TickSelfUpdate();
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
            if ((!IsDev || _options.DevSelfInstall) && _options.TourDir == null)
                await Task.Run(() => SelfInstall.Ensure(_svc.Paths, AppVersion, _svc.Log, SelfInstall.TargetsFor(_options, _svc.Paths)));
            Busy = true;
            BusyText = "Looking for Bannerlord…";
            try { await Task.Run(() => _svc.LocateGame()); }
            finally { Busy = false; }
            RefreshGame();
            ShowInstalled();
            _clock.Start();
            _ = CheckSelfUpdateAsync(true); // the updater updates itself even when the game is not found
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
            // Named in the launcher's load order (Core first), as the player sees them there.
            var shown = _latest?.Manifest ?? m;
            var tick = shown == null ? new List<string>()
                : shown.Modules.Where(x => !string.Equals(x.Role, ModuleRoles.AdminClient, StringComparison.Ordinal))
                               .OrderBy(x => x.IsCore() ? 0 : 1).Select(x => x.DisplayName()).ToList();
            TickHint = tick.Count == 0 ? null : "In the Bannerlord launcher's Multiplayer tab, tick " + Join(tick) + " (or click \"Tick them in the launcher for me\" on Play: it changes the launcher's list only when you ask).";
            RefreshLauncherCard();
            RefreshNews();
            RefreshLook();
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
                if (_latest.Error == null) _ = LoadSiteNewsAsync();
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
            if (_latest?.Plan != null && _latest.Plan.HasChanges && InTray && !_svc.State.PreDownload && _notifiedRelease != _latest.Manifest.ReleaseId)
            {
                _notifiedRelease = _latest.Manifest.ReleaseId;
                TrayNotification?.Invoke("LotR MP Event: update available", ReleaseLabel(_latest.Manifest) + ". Click to review it (nothing is installed without your click).");
            }
            await CheckSelfUpdateAsync(userAsked);
        }

        // ------------------------------------------------------------ self-update of this app
        private string _updaterUpdateText;
        /// <summary>Banner on Home when a newer updater is downloaded and verified (null hides it).</summary>
        public string UpdaterUpdateText { get => _updaterUpdateText; set { if (Set(ref _updaterUpdateText, value)) Raise(nameof(UpdaterUpdateReady)); } }
        public bool UpdaterUpdateReady => _updaterUpdateText != null;
        private bool _canRestartToUpdate;
        /// <summary>"Restart now" is shown only when it works: a verified update is staged and this is the installed copy.</summary>
        public bool CanRestartToUpdate { get => _canRestartToUpdate; set { if (Set(ref _canRestartToUpdate, value)) CommandRequery(); } }
        private bool _selfUpdateRunning;
        private string _stagedUpdaterVersion;
        private DateTime _nextSelfUpdateCheckUtc = DateTime.MaxValue;
        private DateTime? _autoRestartAtUtc;
        /// <summary>Notice shown this long before the automatic restart (window visible); in the tray it restarts at once.</summary>
        private static readonly TimeSpan AutoRestartNotice = TimeSpan.FromSeconds(8);

        /// <summary>Reads files/updater/latest.json; a newer updater is downloaded, checked (SHA-256, release-key signature,
        /// Authenticode signer) and staged. It is applied automatically as soon as the app is idle (<see cref="TickSelfUpdate"/>),
        /// or at the next start. Runs at start-up and every 2 hours, with or without the game.</summary>
        private async Task CheckSelfUpdateAsync(bool force = false)
        {
            if (_selfUpdateRunning || _options.TourDir != null) return;
            if (!force && DateTime.UtcNow < _nextSelfUpdateCheckUtc && _nextSelfUpdateCheckUtc != DateTime.MaxValue) return;
            _selfUpdateRunning = true;
            bool failed = false;
            try
            {
                var su = _svc.SelfUpdate;
                var pending = su.Pending;
                if (pending == null)
                {
                    var offer = await Task.Run(() => su.CheckAsync(_svc.State.Channel, _svc.State.SkippedUpdaterVersion, CancellationToken.None));
                    if (offer != null) pending = await Task.Run(() => su.DownloadAsync(offer, null, CancellationToken.None));
                }
                if (pending != null)
                {
                    bool installedCopy = SelfInstall.IsRunningInstalledCopy(_svc.Paths);
                    _stagedUpdaterVersion = pending.Version;
                    CanRestartToUpdate = installedCopy;
                    if (_autoRestartAtUtc == null)
                        UpdaterUpdateText = "A new version of this updater (" + pending.Version + ") is downloaded and checked. " +
                                            (installedCopy ? "It installs itself as soon as nothing is running; the current one is kept so you can go back."
                                                           : "It is used the next time you start the app from the Start menu or the Desktop.");
                }
            }
            catch (Exception ex) when (ex is Core.SelfUpdate.SelfUpdateException || ex is Core.Net.HttpFetchException || ex is System.Net.Http.HttpRequestException ||
                                       ex is IOException || ex is UnauthorizedAccessException || ex is TaskCanceledException)
            {
                failed = true;
                _svc.Log.Warn("Updater self-update: " + ex.Message);
            }
            finally
            {
                _selfUpdateRunning = false;
                _nextSelfUpdateCheckUtc = DateTime.UtcNow.Add(failed ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(2));
                CommandRequery();
            }
        }

        /// <summary>Every second: the periodic self-update check, and the automatic restart into a staged updater when the
        /// app is idle (no install, verify or download running, the update plan not open). Also runs in the tray.</summary>
        private void TickSelfUpdate()
        {
            if (_options.TourDir != null) return;
            var now = DateTime.UtcNow;
            if (!_selfUpdateRunning && _nextSelfUpdateCheckUtc != DateTime.MaxValue && now >= _nextSelfUpdateCheckUtc) _ = CheckSelfUpdateAsync();
            bool idle = !Busy && !_selfUpdateRunning && Page != Page.Plan;
            if (CanRestartToUpdate && idle && AutoRestartAllowed(_stagedUpdaterVersion))
            {
                if (_autoRestartAtUtc == null)
                {
                    _autoRestartAtUtc = now.Add(InTray ? TimeSpan.Zero : AutoRestartNotice);
                    UpdaterUpdateText = "Updating the updater to " + _stagedUpdaterVersion + "… it restarts in a few seconds.";
                    _svc.Log.Info("Updater " + _stagedUpdaterVersion + " is ready; restarting into it" + (InTray ? " (tray)." : " in " + AutoRestartNotice.TotalSeconds + " s."));
                }
                else if (now >= _autoRestartAtUtc.Value) RestartToUpdate(true);
            }
            else if (_autoRestartAtUtc != null && !(CanRestartToUpdate && idle))
            {
                // Something started meanwhile (install, verify, plan): wait for the next idle moment.
                _autoRestartAtUtc = null;
                UpdaterUpdateText = "A new version of this updater (" + _stagedUpdaterVersion + ") is ready. It installs itself as soon as nothing is running.";
            }
        }

        private string AutoRestartFile => Path.Combine(_svc.Paths.UpdateDir, "auto-restart.txt");

        /// <summary>At most two automatic restarts per staged version: if the swap keeps failing (file locked), the app does
        /// not restart in a loop; the next manual start or "Restart now" still applies it.</summary>
        private bool AutoRestartAllowed(string version)
        {
            try
            {
                if (!File.Exists(AutoRestartFile)) return true;
                var parts = File.ReadAllText(AutoRestartFile).Trim().Split(' ');
                return !(parts.Length == 2 && parts[0] == version && int.TryParse(parts[1], out var n) && n >= 2);
            }
            catch (Exception) { return true; }
        }

        private void CountAutoRestart(string version)
        {
            try
            {
                int n = 0;
                if (File.Exists(AutoRestartFile))
                {
                    var parts = File.ReadAllText(AutoRestartFile).Trim().Split(' ');
                    if (parts.Length == 2 && parts[0] == version) int.TryParse(parts[1], out n);
                }
                Directory.CreateDirectory(_svc.Paths.UpdateDir);
                File.WriteAllText(AutoRestartFile, version + " " + (n + 1));
            }
            catch (Exception) { }
        }

        private void RestartToUpdate(bool automatic)
        {
            var exe = _svc.Paths.InstalledExe;
            if (!CanRestartToUpdate || !File.Exists(exe)) return;
            try
            {
                if (automatic) CountAutoRestart(_stagedUpdaterVersion);
                // The new process waits for this one to exit, swaps the exe, starts the new version and watches that it
                // starts properly (else it rolls back). Same options; back in the tray if the window was hidden.
                Process.Start(new ProcessStartInfo(exe, Program.RestartArguments(minimized: InTray)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) })?.Dispose();
                _svc.Log.Info("Restarting to install updater " + _stagedUpdaterVersion + (automatic ? " (automatic, the app was idle)." : "."));
                RestartRequested?.Invoke();
            }
            catch (Exception ex)
            {
                _svc.Log.Error("Could not restart: " + ex.Message);
                CanRestartToUpdate = false;
                _autoRestartAtUtc = null;
                UpdaterUpdateText = "A new version of this updater (" + _stagedUpdaterVersion + ") is ready. It is used the next time the app starts.";
            }
        }

        /// <summary>Version kept as LotRMPUpdater.old.exe after a self-update (null: nothing to go back to).</summary>
        public string PreviousUpdaterVersion => SelfInstall.IsRunningInstalledCopy(_svc.Paths) ? Core.SelfUpdate.SelfUpdateSwap.PreviousVersion(_svc.Paths) : null;

        private void RollbackUpdater()
        {
            var r = MessageBox.Show("Go back to the previous version of this updater (" + PreviousUpdaterVersion + ")?\n\nThe current version will not be offered again; a later one will.",
                "LotR MP Event updater", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(_svc.Paths.InstalledExe, "--rollback-updater " + Program.RestartArguments(minimized: false)) { UseShellExecute = false })?.Dispose();
                RestartRequested?.Invoke();
            }
            catch (Exception ex) { _svc.Log.Error("Could not roll back the updater: " + ex.Message); }
        }

        private void ApplyCheck(CheckResult r)
        {
            LastCheck = "Last check " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            RollbackOffered = r.RollbackOffered;
            StatusErrorDetail = null;
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
                StatusErrorDetail = r.ErrorDetail;
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
                {
                    StatusDetail += " Already downloaded in the background: installing takes seconds.";
                    _notifiedRelease = _latest.Manifest.ReleaseId;
                    TrayNotification?.Invoke("LotR MP Event: update ready", ReleaseLabel(_latest.Manifest) + " is downloaded. Click to review and install it (takes seconds).");
                }
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
                         : a.Source == LotR.Releases.Install.FileAction.CopyLocal ? Units.Bytes(a.Size) + (a.FromWorkshop ? " · copied from your Steam Workshop copy" : " · copied from your PC")
                         : a.Source == LotR.Releases.Install.FileAction.Rebuild ? Units.Bytes(a.Size) + $" · {a.Chunks.Count(c => c.Local != null)}/{a.Chunks.Count} parts reused" + (a.FromWorkshop ? " (Steam Workshop copy)" : "")
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
            if (p.WorkshopReusedBytes > 0)
                PlanFacts.Add(new Fact("Steam Workshop", Units.Bytes(p.WorkshopReusedBytes) + " copied from your Steam Workshop copy instead of downloaded (the Workshop folder is only read, never changed)"));
            PlanFacts.Add(new Fact("Temporary space", Units.Bytes(p.StagingBytes) + " inside each module folder (.lotr-staging), removed at the end"));
            PlanFacts.Add(new Fact("Backup of replaced files", p.BackupBytes > 0 ? Units.Bytes(p.BackupBytes) + " → " + p.BackupPath : "None needed (nothing is replaced)"));
            PlanFacts.Add(new Fact("Game folder", p.Game.ModulesPath));
            PlanFacts.Add(new Fact("Release", p.Manifest.ReleaseId + " · " + (p.SignatureStatus ?? "")));
            PlanIssues.Clear();
            foreach (var i in p.Issues.OrderByDescending(x => x.Severity))
                PlanIssues.Add(new IssueRow { Text = i.Text, Severity = i.Severity.ToString().ToLowerInvariant(), LinkUrl = i.LinkUrl, LinkText = i.LinkText });
            PlanNote = p.HasChanges
                ? "Nothing has been changed yet. Files are downloaded and checked first; your game folder is only touched at the very end, in a few seconds, and everything is put back if anything goes wrong."
                : "Everything already matches. Nothing to do.";
            Page = Page.Plan;
            CommandRequery();
        }

        // ------------------------------------------------------------ progress
        private double _progress;
        public double ProgressValue { get => _progress; set { if (Set(ref _progress, value)) RefreshLook(); } }
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

        private readonly System.Collections.Concurrent.ConcurrentQueue<LogLine> _pendingLog = new System.Collections.Concurrent.ConcurrentQueue<LogLine>();
        private readonly DispatcherTimer _logFlush;

        /// <summary>UI thread, every 250 ms: appends the waiting lines in one go. The on-screen log keeps the last ~150 KB
        /// (the full log is in the file).</summary>
        private void FlushLog()
        {
            if (_pendingLog.IsEmpty) return;
            while (_pendingLog.TryDequeue(out var l)) _log.AppendLine(l.ToString());
            if (_log.Length > 200_000) _log.Remove(0, _log.Length - 150_000);
            LogText = _log.ToString();
            LogAppended?.Invoke();
        }

        public async Task InstallAsync()
        {
            var plan = _shownPlan;
            if (plan == null || Busy) return;
            _installStage = 0;
            _installing = true;
            Page = Page.Home; // the road on Home shows the progress; "Show details" opens the live log
            Busy = true;
            BusyText = "Installing…";
            ProgressResult = null;
            ProgressErrorDetail = null;
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
                var autoTick = await AfterInstallLauncherTickAsync(); // opt-in Settings option only (launcher region)
                if (autoTick != null) ProgressResult += " " + autoTick;
            }
            catch (OperationCanceledException)
            {
                ProgressTitle = "Cancelled";
                ProgressResultKind = "warn";
                ProgressResult = "Cancelled. Your game folder was not changed. What was already downloaded is kept, so the next try continues where it stopped.";
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Calm English text for the player; the raw (often localized) exception goes to the log and "Details".
                var problem = PlayerErrors.ForInstall(ex);
                ProgressTitle = problem.Kind == ProblemKind.RollbackIncomplete ? "The update stopped" : "The update did not complete";
                ProgressResultKind = "error";
                ProgressResult = problem.Message;
                ProgressErrorDetail = problem.Detail;
                _svc.Log.Error("Install failed: " + problem.Message + " | " + problem.Detail);
                Page = Page.Progress; // the calm message, "Send a report" and "Try again" live there
                // "Try again" stays available: the engine re-runs every check (game closed, space, write access) anyway.
                plan.Issues.RemoveAll(i => i.Severity == IssueSeverity.Blocker);
            }
            finally
            {
                _installing = false;
                Busy = false;
                BusyText = null;
                _cts = null;
            }
            await CheckAsync(true);
        }

        private void OnProgress(EngineProgress e)
        {
            _installStage = e.Phase == EnginePhase.Queue || e.Phase == EnginePhase.Downloading ? 0
                          : e.Phase == EnginePhase.Preparing ? 1 : e.Phase == EnginePhase.Done ? 3 : 2;
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
            VerifyErrorDetail = null;
            VerifyKind = "checking";
            VerifySummary = "Checking every file…";
            var status = new Progress<string>(s => VerifySummary = s);
            try
            {
                var r = await Task.Run(() => _svc.VerifyAsync(status, CancellationToken.None));
                _repairPlan = r.RepairPlan;
                VerifyKind = r.Healthy && r.Duplicates.All(d => d.IsWorkshop) ? "ok" : "update";
                VerifySummary = r.Healthy
                    ? $"All {r.Checked} files are correct." + (r.MarkOfTheWebCleared > 0 ? $" Removed the internet mark from {Plural(r.MarkOfTheWebCleared, "file")}." : "")
                    : $"{r.Ok} of {r.Checked} files are correct. {r.Missing.Count} missing, {r.Modified.Count} different. \"Repair\" shows exactly what it will fix before doing anything.";
                foreach (var x in r.Missing.Take(200)) VerifyRows.Add(new IssueRow { Severity = "missing", Text = x });
                foreach (var x in r.Modified.Take(200)) VerifyRows.Add(new IssueRow { Severity = "different", Text = x });
                foreach (var x in r.Extra.Take(200)) VerifyRows.Add(new IssueRow { Severity = "extra", Text = x + "  (not part of the release; the updater leaves it as it is)" });
                foreach (var d in r.Duplicates)
                    VerifyRows.Add(d.IsWorkshop
                        ? new IssueRow { Severity = "info", Text = d.Advice, LinkUrl = d.WorkshopPageUrl, LinkText = d.WorkshopPageUrl != null ? "Open the Workshop page" : null } // Workshop subscription: the game uses Modules
                        : new IssueRow { Severity = "duplicate", Text = d.Id + " is also in " + d.Path + " (" + d.Where + "). Tick only one copy in the launcher, or remove the other one." });
                if (r.MarkOfTheWebCleared > 0) VerifyRows.Add(new IssueRow { Severity = "fixed", Text = $"Removed the \"downloaded from the internet\" mark from {Plural(r.MarkOfTheWebCleared, "file")} (Windows can refuse to load a mod DLL that carries it)." });
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var problem = PlayerErrors.ForVerify(ex);
                VerifyKind = "error";
                VerifySummary = problem.Message;
                VerifyErrorDetail = problem.Detail;
                _svc.Log.Error("Verify failed: " + problem.Detail);
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
            catch (Exception ex) { SupportLog = "(could not build the log: " + PlayerErrors.Technical(ex) + ")"; }
        }

        // --- error reports (begin): every error box offers "Send a report to the team" -> this page, nothing sent yet ---
        private string _supportContext;
        /// <summary>The problem being reported (shown at the top of the Support page), null when opened from the menu.</summary>
        public string SupportContext { get => _supportContext; set => Set(ref _supportContext, value); }
        private string _progressErrorDetail;
        public string ProgressErrorDetail { get => _progressErrorDetail; set => Set(ref _progressErrorDetail, value); }
        private string _statusErrorDetail;
        public string StatusErrorDetail { get => _statusErrorDetail; set => Set(ref _statusErrorDetail, value); }
        private string _verifyErrorDetail;
        public string VerifyErrorDetail { get => _verifyErrorDetail; set => Set(ref _verifyErrorDetail, value); }

        /// <param name="what">"install" | "check" | "verify": which error box the player clicked from.</param>
        private void OpenReport(string what)
        {
            string text = what == "install" ? ProgressResult : what == "verify" ? VerifySummary : StatusDetail;
            SupportContext = string.IsNullOrEmpty(text) ? null : "About this problem: " + text;
            SupportCode = null;
            SupportStatus = null;
            _svc.Log.Info("The player opened a report about: " + (text ?? what));
            Page = Page.Support; // refreshes the log shown below (exactly what will be sent)
        }
        // --- error reports (end) ---

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
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _svc.Log.Warn("Support upload failed: " + PlayerErrors.Technical(ex));
                SupportStatus = PlayerErrors.ForSupport(ex).Message;
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

        /// <summary>Optional, off by default: one shortcut in the user's Startup folder that starts the app minimised to the
        /// notification area (no registry Run key, no scheduled task, no service). The shortcut IS the setting.</summary>
        public bool StartWithWindows
        {
            get { try { return _startup.IsEnabled; } catch (Exception) { return false; } }
            set
            {
                try
                {
                    if (value) _startup.Enable(); else _startup.Disable();
                    _svc.Log.Info((value ? "Start with Windows on: " : "Start with Windows off: removed ") + _startup.ShortcutPath);
                    SettingsMessage = value ? "Added " + _startup.ShortcutPath : "Removed the Startup shortcut.";
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    SettingsMessage = "Could not change the Startup shortcut: " + ex.Message;
                }
                Raise();
            }
        }

        public string StartupShortcutPath => _startup.ShortcutPath;

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
            Folders.Add(new Fact("Startup (optional)", "only if you tick \"Start with Windows\" in Settings: one shortcut " + _startup.ShortcutPath + " (no registry Run key, no scheduled task, no service)"));
            Folders.Add(new Fact("Launcher mod list (optional)", "only when you press \"Tick them in the launcher for me\" on Play or in Settings, or tick \"Tick the mods automatically after each update\" in Settings: "
                + (LauncherFilePath ?? "Documents\\Mount and Blade II Bannerlord\\Configs\\LauncherData.xml")
                + ". Only the Multiplayer list changes (our modules ticked, in the right order); a dated copy of the previous file is kept next to it, and Undo puts it back."));
            if (_svc.Verifier.TrustedFingerprints.Count == 0) Fingerprints.Add("No release key embedded yet (placeholder): every release will be refused.");
            foreach (var f in _svc.Verifier.TrustedFingerprints) Fingerprints.Add(f);
        }

        /// <summary>Licence texts of the bundled fonts (SIL Open Font License 1.1), shown on the About page.</summary>
        public string FontLicences
        {
            get
            {
                try
                {
                    using (var s = typeof(MainViewModel).Assembly.GetManifestResourceStream("licences.OFL.txt"))
                    using (var r = new StreamReader(s)) return r.ReadToEnd();
                }
                catch (Exception) { return "SIL Open Font License 1.1 (text missing from this build)."; }
            }
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

        /// <summary>Opens a Workshop item page in the Steam client (its Unsubscribe button), else in the browser. Only
        /// steam://url/CommunityFilePage/&lt;digits&gt; is accepted: nothing else can be launched from a plan or verify row.</summary>
        private void OpenWorkshopPage(string url)
        {
            const string prefix = "steam://url/CommunityFilePage/";
            if (url == null || !url.StartsWith(prefix, StringComparison.Ordinal)) return;
            var id = url.Substring(prefix.Length);
            if (id.Length == 0 || !id.All(char.IsDigit)) return;
            try { Process.Start(new ProcessStartInfo(prefix + id) { UseShellExecute = true })?.Dispose(); }
            catch (Exception)
            {
                try { Process.Start(new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { _svc.Log.Warn(ex.Message); }
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

        #region Launcher look (0.3.0): what the Home screen shows
        // Presentation only: every value below is read from what the check, the plan and the install already produce.
        // Nothing here downloads, installs or decides anything; the buttons call the same commands as before.

        private bool _installing;
        private int _installStage;
        private RelayCommand _updateNowCmd, _sealCmd, _detailsCmd;

        public ObservableCollection<BannerModuleRow> BannerModules { get; } = new ObservableCollection<BannerModuleRow>();
        public ObservableCollection<StationRow> Stations { get; } = new ObservableCollection<StationRow>
        {
            new StationRow { Label = "DOWNLOAD", ShowRight = true },
            new StationRow { Label = "VERIFY", ShowLeft = true, ShowRight = true },
            new StationRow { Label = "INSTALL", ShowLeft = true, ShowRight = true },
            new StationRow { Label = "READY", ShowLeft = true },
        };

        private string _bannerHeader;
        public string BannerHeader { get => _bannerHeader; set => Set(ref _bannerHeader, value); }
        private string _homeEyebrow;
        public string HomeEyebrow { get => _homeEyebrow; set => Set(ref _homeEyebrow, value); }
        private string _homeTitle;
        public string HomeTitle { get => _homeTitle; set => Set(ref _homeTitle, value); }
        private string _homeLede;
        public string HomeLede { get => _homeLede; set => Set(ref _homeLede, value); }
        private string _sealKind = "wait";
        /// <summary>update | wait | play | locate: the look of the round button.</summary>
        public string SealKind { get => _sealKind; set => Set(ref _sealKind, value); }
        private string _sealTitle = "···";
        public string SealTitle { get => _sealTitle; set => Set(ref _sealTitle, value); }
        private string _sealSub;
        public string SealSub { get => _sealSub; set => Set(ref _sealSub, value); }
        private string _sealCaption;
        /// <summary>Line under the round button (what it does), null when the links say it.</summary>
        public string SealCaption { get => _sealCaption; set => Set(ref _sealCaption, value); }
        private bool _showDetailsLink;
        public bool ShowDetailsLink { get => _showDetailsLink; set => Set(ref _showDetailsLink, value); }
        private bool _installingNow;
        public bool InstallingNow { get => _installingNow; set => Set(ref _installingNow, value); }
        private bool _waitingLine;
        /// <summary>A check, a background download or a verify is running (not an install): the pulsing line on Home.</summary>
        public bool WaitingLine { get => _waitingLine; set => Set(ref _waitingLine, value); }
        private bool _canCheckAgain;
        /// <summary>The last check failed and the game is there: "Check again" under the round button.</summary>
        public bool CanCheckAgain { get => _canCheckAgain; set => Set(ref _canCheckAgain, value); }
        private bool _offerTick;
        /// <summary>Ready, and the launcher's list does not have our modules ticked yet: "Tick them for me".</summary>
        public bool OfferTick { get => _offerTick; set => Set(ref _offerTick, value); }
        private string _newsVersion;
        public string NewsVersion { get => _newsVersion; set => Set(ref _newsVersion, value); }
        private string _newsTeaser;
        public string NewsTeaser { get => _newsTeaser; set => Set(ref _newsTeaser, value); }
        private string _homeWarning;
        /// <summary>The game is found but something about it needs the player (the Modules folder is not writable).</summary>
        public string HomeWarning { get => _homeWarning; set => Set(ref _homeWarning, value); }
        private string _homeLauncherResult;
        /// <summary>What "Tick them for me" / "Undo" just did, shown on Home while the mods are ready.</summary>
        public string HomeLauncherResult { get => _homeLauncherResult; set => Set(ref _homeLauncherResult, value); }

        /// <summary>The round button's single click: update when there is one, open the game when ready (or when the check
        /// failed but the game is there), pick the game folder when it is not found.</summary>
        public RelayCommand SealCommand => _sealCmd ?? (_sealCmd = new RelayCommand(() =>
        {
            if (SealKind == "update") UpdateNowCommand.Execute(null);
            else if (SealKind == "play") LaunchCommand.Execute(null);
            else if (SealKind == "locate") BrowseGameCommand.Execute(null);
        }, () => SealKind == "update" ? UpdateNowCommand.CanExecute(null)
               : SealKind == "play" ? LaunchCommand.CanExecute(null)
               : SealKind == "locate" && BrowseGameCommand.CanExecute(null)));

        /// <summary>One click on Update. A plan with a warning or a blocker still opens the plan page first (as in 0.2.0),
        /// so nothing that needs the player's attention is ever skipped.</summary>
        public RelayCommand UpdateNowCommand => _updateNowCmd ?? (_updateNowCmd = new RelayCommand(() =>
        {
            var p = _latest?.Plan;
            if (p == null || !p.HasChanges) return;
            ShowPlan(p, "Update to " + ReleaseLabel(_latest.Manifest));
            if (p.IsBlocked || p.Issues.Any(i => i.Severity != IssueSeverity.Info)) return;
            _ = InstallAsync();
        }, () => !Busy && _latest?.Plan != null && _latest.Plan.HasChanges));

        /// <summary>"Show details": the live log while installing, else the exact plan.</summary>
        public RelayCommand DetailsCommand => _detailsCmd ?? (_detailsCmd = new RelayCommand(() =>
        {
            if (_installing || _latest?.Plan == null || !_latest.Plan.HasChanges) Page = Page.Progress;
            else ShowPlan(_latest.Plan, "Update to " + ReleaseLabel(_latest.Manifest));
        }, () => _installing || !Busy));

        /// <summary>Recomputed whenever the status, the progress or Busy changes (cheap: a handful of strings).</summary>
        private void RefreshLook()
        {
            if (_svc == null) return; // a setter ran before the constructor finished
            if (!_ui.CheckAccess()) { _ui.BeginInvoke(new Action(RefreshLook)); return; }
            var installed = _svc.InstalledManifest;
            var offered = _latest != null && _latest.Error == null ? _latest.Manifest : null;
            var plan = offered != null ? _latest.Plan : null;
            var shown = offered ?? installed;
            bool pending = plan != null && plan.HasChanges;
            int pct = (int)Math.Round(ProgressValue * 100);
            InstallingNow = _installing;
            WaitingLine = Busy && !_installing;

            // ---- the modules on the banner (Event first, then Core; never the admin module)
            var mods = shown == null ? new List<ManifestModule>()
                : shown.Modules.Where(x => !string.Equals(x.Role, ModuleRoles.AdminClient, StringComparison.Ordinal))
                               .OrderBy(x => x.IsCore() ? 1 : 0).ToList();
            BannerModules.Clear();
            foreach (var m in mods)
            {
                var mp = plan?.Modules.FirstOrDefault(x => string.Equals(x.Module.Id, m.Id, StringComparison.OrdinalIgnoreCase));
                var had = installed?.Modules.FirstOrDefault(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase))?.Version;
                var row = new BannerModuleRow { Name = m.DisplayName(), Version = Ver(m.Version) };
                if (offered == null || StatusKind == "checking") { row.Pill = StatusKind == "checking" ? "CHECKING" : null; row.PillKind = "wait"; }
                else if (mp != null && mp.HasChanges)
                {
                    if (had != null && had != m.Version) row.Version = Ver(had) + " → " + Ver(m.Version);
                    row.Pill = _installing ? pct + " %" : mp.IsNewFolder ? "INSTALL" : "UPDATE";
                    row.PillKind = "up";
                }
                else { row.Pill = "UP TO DATE"; row.PillKind = "ok"; }
                BannerModules.Add(row);
            }
            if (mods.Count == 0)
                foreach (var n in new[] { "LotRMPEvent", "LotRMPCore" })
                    BannerModules.Add(new BannerModuleRow { Name = n, Version = "not installed", Pill = StatusKind == "checking" ? "CHECKING" : null, PillKind = "wait" });
            int count = BannerModules.Count;
            BannerHeader = count == 1 ? "1 MODULE" : count == 2 ? "2 MODULES · BOTH REQUIRED" : count + " MODULES · ALL REQUIRED";

            // ---- the road, the title and the round button
            var ev = shown?.Modules.FirstOrDefault(x => string.Equals(x.Role, ModuleRoles.Event, StringComparison.Ordinal)) ?? mods.FirstOrDefault();
            int changes = plan?.Modules.Sum(m => m.Actions.Count) ?? 0;
            string[] notes = { "", "", "", "" };
            int stage; // -1: road unlit; 0..3: that station is the current one; 4: everything done
            ShowDetailsLink = false;
            SealCaption = null;
            if (_installing)
            {
                stage = _installStage;
                for (int i = 0; i < stage; i++) notes[i] = "done";
                notes[stage] = stage < 3 ? pct + " %" : "";
                HomeEyebrow = "UPDATING";
                HomeTitle = stage == 0 ? (ProgressTitle == "Waiting for a download slot" ? "Waiting for a download slot" : "Downloading")
                          : stage == 1 ? "Verifying files" : "Installing";
                HomeLede = "Please keep this window open. Your game folder is only changed at the very end.";
                SealKind = "wait"; SealTitle = pct + " %"; SealSub = "please wait";
                ShowDetailsLink = true;
            }
            else if (pending && StatusKind != "checking")
            {
                stage = 0;
                bool fresh = StatusKind == "missing";
                string verb = fresh ? "Install" : "Update";
                notes[0] = plan.DownloadBytes > 0 ? Units.Bytes(plan.DownloadBytes) : "already here";
                notes[1] = Plural(changes, "file");
                notes[2] = "a few seconds";
                HomeEyebrow = fresh ? "NOT INSTALLED YET" : "UPDATE AVAILABLE";
                HomeTitle = fresh ? "Install LOTR Event" : "Version " + Ver(ev?.Version);
                HomeLede = Plural(changes, "file") + (fresh ? " to install, " : " changed, ") +
                           (plan.DownloadBytes > 0 ? Units.Bytes(plan.DownloadBytes) + " to download. " : "already downloaded. ") +
                           (plan.IsBlocked || plan.Issues.Any(i => i.Severity != IssueSeverity.Info) ? "Something needs your attention first: click " + verb + " to see what."
                                           : "Nothing changes on your PC until you click " + verb + ".");
                SealKind = Busy ? "wait" : "update";
                SealTitle = verb.ToUpperInvariant();
                SealSub = plan.DownloadBytes > 0 ? Units.Bytes(plan.DownloadBytes) : "ready";
                ShowDetailsLink = true;
            }
            else if (StatusKind == "ok")
            {
                stage = 4;
                notes[0] = notes[1] = notes[2] = "done";
                HomeEyebrow = "UP TO DATE";
                HomeTitle = "Ready to play";
                // Named in the launcher's load order (Core first), as the player sees them there.
                var tick = mods.OrderBy(x => x.IsCore() ? 0 : 1).Select(x => x.DisplayName()).ToList();
                HomeLede = tick.Count == 0 ? "Your mods match the server."
                    : LauncherStatusKind == "ok"
                    ? "Your mods match the server, and " + (tick.Count == 2 ? "both modules are" : "the modules are") + " ticked in the Bannerlord launcher."
                    : "Your mods match the server. In the Bannerlord launcher, tick " + (tick.Count == 2 ? "both modules: " : tick.Count == 1 ? "the module " : "all the modules: ") + Join(tick) + ".";
                SealKind = Busy ? "wait" : "play"; SealTitle = "PLAY"; SealSub = null;
                SealCaption = LaunchHint?.Replace("Opens", "Starts").Replace(" (official launcher)", "");
            }
            else if (StatusKind == "checking")
            {
                stage = -1;
                HomeEyebrow = "CHECKING";
                HomeTitle = "Checking for updates";
                HomeLede = "Comparing your mod files with the server. This takes a few seconds.";
                SealKind = "wait"; SealTitle = "···"; SealSub = "checking";
            }
            else
            {
                // Could not check, older release offered, or no game folder: the same words as before, and the way out.
                stage = -1;
                HomeEyebrow = !HasGame ? "GAME NOT FOUND" : RollbackOffered ? "OLDER RELEASE OFFERED" : "COULD NOT CHECK";
                HomeTitle = StatusTitle;
                // No game: a short sentence here; the full search report (rejected folders...) stays in Settings.
                HomeLede = HasGame ? StatusDetail : "Bannerlord was not found on this PC. Click Locate and choose the game folder: the one that contains 'bin' and 'Modules'.";
                if (HasGame) { SealKind = Busy ? "wait" : "play"; SealTitle = "PLAY"; SealSub = null; SealCaption = RollbackOffered ? "Nothing changes until you accept the rollback." : "Your mods could not be compared with the server."; }
                else { SealKind = "locate"; SealTitle = "LOCATE"; SealSub = "the game"; SealCaption = "Choose the Bannerlord folder."; }
            }
            if (Busy && !_installing && SealKind == "wait" && StatusKind != "checking") { SealTitle = "···"; SealSub = "please wait"; }
            for (int i = 0; i < 4; i++)
            {
                var s = Stations[i];
                s.State = stage < 0 ? "todo" : stage == 4 || i < stage ? "done" : i == stage ? "active" : "todo";
                s.Note = notes[i];
                s.LeftLit = i > 0 && stage >= i;   // the segment arriving here is lit once the previous station is done
                s.RightLit = stage > i;
            }
            OfferTick = StatusKind == "ok" && !_installing && ShowLauncherCard && LauncherStatusKind != "ok";
            HomeLauncherResult = StatusKind == "ok" && !_installing ? _launcherMessage : null;
            HomeWarning = HasGame ? GameNote : null;
            CanCheckAgain = HasGame && StatusKind == "error" && !Busy;
            CommandRequery();
        }

        /// <summary>"v1.0.2.5" and "1.0.2.5" both show as 1.0.2.5 (modules write their version either way).</summary>
        private static string Ver(string v) => string.IsNullOrEmpty(v) ? "" : v.Length > 1 && (v[0] == 'v' || v[0] == 'V') && char.IsDigit(v[1]) ? v.Substring(1) : v;

        /// <summary>First line of the notes, without its bullet, for the "Patch notes" line on Home.</summary>
        private static string Teaser(string notes)
        {
            if (string.IsNullOrEmpty(notes)) return null;
            var line = notes.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', '·', ' ').Trim()).FirstOrDefault(l => l.Length > 0);
            if (line == null) return null;
            return line.Length > 70 ? line.Substring(0, 67).TrimEnd() + "…" : line;
        }
        #endregion

        #region Launcher mod list (opt-in "Tick the mods for me") and "What's new"
        // Everything for these two features lives in this region. The launcher's list is only ever changed on a click (or after
        // an update when the player ticked the Settings option), with a backup next to the file and an Undo link.

        private Core.Game.LauncherSelection _launcherSel;
        private Core.Game.LauncherSelectionUndo _launcherUndo;
        private string _launcherMessage;
        private Core.News.NewsHistory _newsHistory;
        private RelayCommand _tickLauncherCmd, _undoLauncherCmd, _openNewsCmd;

        /// <summary>Dev runs use a fake Documents folder inside the dev home: the real launcher file is never touched.</summary>
        private Core.Game.LauncherSelection LauncherSel
        {
            get
            {
                if (_launcherSel != null) return _launcherSel;
                var path = IsDev
                    ? Path.Combine(_svc.Paths.Home, "documents-test", "Mount and Blade II Bannerlord", "Configs", Core.Game.LauncherSelection.FileName)
                    : Core.Game.LauncherSelection.DefaultPath();
                if (path == null) return null;
                return _launcherSel = new Core.Game.LauncherSelection(path, new Core.Game.SystemProcessProbe());
            }
        }

        private List<Core.Game.LauncherModule> LauncherMods() => Core.Game.LauncherSelection.ModulesFor(_svc.InstalledManifest);

        // Independent of Busy (a background check or pre-download never touches the launcher file); one launcher action at a time.
        private bool _launcherWorking;
        public RelayCommand TickLauncherModsCommand => _tickLauncherCmd ?? (_tickLauncherCmd = new RelayCommand(() => _ = TickLauncherModsAsync(false), () => !_launcherWorking && ShowLauncherCard));
        public RelayCommand UndoLauncherModsCommand => _undoLauncherCmd ?? (_undoLauncherCmd = new RelayCommand(() => _ = UndoLauncherModsAsync(), () => !_launcherWorking && _launcherUndo != null));
        public RelayCommand OpenNewsCommand => _openNewsCmd ?? (_openNewsCmd = new RelayCommand(OpenChronicles, () => ChroniclesUrl != null));

        private bool _showLauncherCard;
        /// <summary>Home card: shown once a release is installed by this app and the game is found.</summary>
        public bool ShowLauncherCard { get => _showLauncherCard; set => Set(ref _showLauncherCard, value); }
        private string _launcherStatus;
        public string LauncherStatus { get => _launcherStatus; set => Set(ref _launcherStatus, value); }
        private string _launcherStatusKind;
        /// <summary>ok | warn | error | null</summary>
        public string LauncherStatusKind { get => _launcherStatusKind; set { if (Set(ref _launcherStatusKind, value)) RefreshLook(); } }
        private bool _canUndoLauncher;
        public bool CanUndoLauncher { get => _canUndoLauncher; set => Set(ref _canUndoLauncher, value); }
        public string LauncherFilePath => LauncherSel?.FilePath;

        /// <summary>Settings, off by default: tick the mods in the launcher after each successful update (same backup + undo).</summary>
        public bool AutoTickLauncherMods
        {
            get => _svc.State.AutoTickLauncherMods;
            set
            {
                if (_svc.State.AutoTickLauncherMods == value) return;
                _svc.State.AutoTickLauncherMods = value;
                _svc.SaveState();
                _svc.Log.Info("Tick the mods in the launcher after each update: " + (value ? "on" : "off"));
                Raise();
            }
        }

        /// <summary>Called from ShowInstalled: read-only look at the launcher's list (never writes).</summary>
        private void RefreshLauncherCard()
        {
            var mods = LauncherMods();
            ShowLauncherCard = HasGame && mods.Count > 0 && LauncherSel != null;
            CanUndoLauncher = _launcherUndo != null;
            if (!ShowLauncherCard) return;
            if (_launcherMessage != null) { LauncherStatus = _launcherMessage; return; }
            try
            {
                var r = LauncherSel.Inspect(mods);
                LauncherStatus = r.Message;
                LauncherStatusKind = r.Outcome == Core.Game.LauncherSelectionOutcome.AlreadyTicked ? "ok" : null;
            }
            catch (Exception ex) { LauncherStatus = null; _svc.Log.Detail("Launcher list not readable: " + ex.Message); }
            CommandRequery();
        }

        private void ShowLauncherResult(Core.Game.LauncherSelectionResult r)
        {
            _launcherMessage = r.Message;
            LauncherStatus = r.Message;
            LauncherStatusKind = r.Ok ? (r.OtherLotREntries.Count > 0 ? "warn" : "ok") : r.Outcome == Core.Game.LauncherSelectionOutcome.Failed ? "error" : "warn";
            CanUndoLauncher = _launcherUndo != null;
            RefreshLook();
        }

        /// <summary>The click (or, with <paramref name="automatic"/>, the opt-in after an update). Returns the sentence shown.</summary>
        private async Task<string> TickLauncherModsAsync(bool automatic)
        {
            var sel = LauncherSel;
            var mods = LauncherMods();
            if (sel == null || mods.Count == 0 || _launcherWorking) return null;
            Core.Game.LauncherSelectionResult r;
            _launcherWorking = true;
            CommandRequery();
            try { r = await Task.Run(() => sel.Apply(mods)); }
            catch (Exception ex)
            {
                r = new Core.Game.LauncherSelectionResult { Outcome = Core.Game.LauncherSelectionOutcome.Failed, Message = "Could not change the launcher's mod list (" + ex.Message + "). Tick the mods yourself in its Multiplayer tab." };
            }
            finally { _launcherWorking = false; }
            if (r.Outcome == Core.Game.LauncherSelectionOutcome.Changed) _launcherUndo = r.Undo;
            _svc.Log.Info((automatic ? "Automatic launcher tick (Settings option): " : "Launcher tick: ") + r.Message + " [" + sel.FilePath + "]");
            ShowLauncherResult(r);
            return r.Message;
        }

        private async Task UndoLauncherModsAsync()
        {
            var sel = LauncherSel;
            var undo = _launcherUndo;
            if (sel == null || undo == null || _launcherWorking) return;
            Core.Game.LauncherSelectionResult r;
            _launcherWorking = true;
            CommandRequery();
            try { r = await Task.Run(() => sel.Undo(undo)); }
            catch (Exception ex) { r = new Core.Game.LauncherSelectionResult { Outcome = Core.Game.LauncherSelectionOutcome.Failed, Message = "Could not undo (" + ex.Message + ")." }; }
            finally { _launcherWorking = false; }
            if (r.Outcome == Core.Game.LauncherSelectionOutcome.Changed) _launcherUndo = null;
            _svc.Log.Info("Launcher tick undo: " + r.Message);
            ShowLauncherResult(r);
        }

        /// <summary>Called once after a successful install; does nothing unless the player turned the option on.</summary>
        private async Task<string> AfterInstallLauncherTickAsync()
        {
            if (!_svc.State.AutoTickLauncherMods) return null;
            return await TickLauncherModsAsync(true);
        }

        // ---- What's new: the site's chronicle for that version (/hub.json, 0.3.1), else the notes of the signed manifest;
        // plain text only either way.
        private List<Core.News.SiteNewsItem> _siteNews;
        private bool _siteNewsLoading;

        /// <summary>After each successful check: reads the site's patch notes in the background, then refreshes the cards.
        /// A failure keeps what was there (the previous site notes, or the manifest's).</summary>
        private async Task LoadSiteNewsAsync()
        {
            if (_siteNewsLoading) return;
            _siteNewsLoading = true;
            try
            {
                var items = await Task.Run(() => _svc.FetchSiteNewsAsync(CancellationToken.None));
                if (items != null) { _siteNews = items; RefreshNews(); }
            }
            catch (Exception ex) { _svc.Log.Detail("Site news: " + ex.Message); }
            finally { _siteNewsLoading = false; }
        }

        private string _newsHeading;
        public string NewsHeading { get => _newsHeading; set => Set(ref _newsHeading, value); }
        private string _newsBody;
        public string NewsBody { get => _newsBody; set => Set(ref _newsBody, value); }
        private string _newsNote;
        /// <summary>"Arrives with the update" when the notes belong to a release not installed yet.</summary>
        public string NewsNote { get => _newsNote; set => Set(ref _newsNote, value); }
        private string _newsSignatureText, _newsSource;
        /// <summary>The manifest's signature line, only when the notes shown are the manifest's (site text is not signed).</summary>
        public string NewsSignatureText { get => _newsSignatureText; set => Set(ref _newsSignatureText, value); }
        /// <summary>Where the notes come from, when it is the website.</summary>
        public string NewsSource { get => _newsSource; set => Set(ref _newsSource, value); }
        public ObservableCollection<Core.News.NewsEntry> PreviousNews { get; } = new ObservableCollection<Core.News.NewsEntry>();
        public string ChroniclesUrl => Core.News.ReleaseNews.ChroniclesUrl(_svc.Config.BaseUrl);

        /// <summary>Called from ShowInstalled: picks the release the player has (or is about to get) and its notes.</summary>
        private void RefreshNews()
        {
            try
            {
                if (_newsHistory == null) _newsHistory = new Core.News.NewsHistory(Core.News.NewsHistory.DefaultFile(_svc.Paths));
                var installed = _svc.InstalledManifest;
                var offered = _latest != null && _latest.Error == null ? _latest.Manifest : null;
                bool changed = false;
                // Both were signature-checked when fetched (installed-manifest.json is only written after that check).
                if (installed != null) changed |= _newsHistory.Record(Core.News.ReleaseNews.FromManifest(installed));
                if (offered != null) changed |= _newsHistory.Record(Core.News.ReleaseNews.FromManifest(offered));
                if (changed) _newsHistory.Save();

                var shown = offered ?? installed;
                var cur = Core.News.ReleaseNews.FromManifest(shown);
                var evVersion = shown?.Modules.FirstOrDefault(x => string.Equals(x.Role, ModuleRoles.Event, StringComparison.Ordinal))?.Version;
                var site = Core.News.SiteNews.For(_siteNews, evVersion);
                NewsHeading = site != null ? Core.News.SiteNews.Heading(site) : Core.News.ReleaseNews.Heading(cur);
                NewsBody = site != null ? Core.News.SiteNews.Body(site) : Core.News.ReleaseNews.Body(cur);
                NewsVersion = Ver(evVersion) is var nv && nv.Length > 0 ? nv : cur?.Title;
                NewsTeaser = site != null ? site.Title : Teaser(cur?.Notes);
                NewsSignatureText = site != null ? null : SignatureText;
                NewsSource = site != null ? "From the Chronicles on the website. Display only: what gets installed is always checked against the team's signature." : null;
                NewsNote = cur != null && installed != null && offered != null && offered.ReleaseId != installed.ReleaseId && _latest.Plan != null && _latest.Plan.HasChanges
                    ? "Arrives with the update (not installed yet)."
                    : cur != null && installed == null ? "Arrives when you install the mod." : null;
                PreviousNews.Clear();
                foreach (var e in site != null ? Core.News.SiteNews.Previous(_siteNews, site) : _newsHistory.Previous(cur)) PreviousNews.Add(e);
            }
            catch (Exception ex) { _svc.Log.Detail("What's new: " + ex.Message); }
        }

        /// <summary>Opens only the link this app computed from its baked server address, never a URL from server text.</summary>
        private void OpenChronicles()
        {
            var url = ChroniclesUrl;
            if (url == null) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { _svc.Log.Warn(ex.Message); }
        }
        #endregion
    }
}
