using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lotr.Updater.Platform;
using Lotr.Updater.ViewModels;

namespace Lotr.Updater
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly StartOptions _options;

        private TrayIcon _tray;
        private bool _started;
        private bool _exiting;
        private bool _toldAboutTray;

        public MainWindow(StartOptions options)
        {
            _options = options;
            InitializeComponent();
            _vm = new MainViewModel(options);
            DataContext = _vm;
            _vm.LogAppended += () => LiveLog.ScrollToEnd();
            _vm.TrayNotification += (title, text) => _tray?.Notify(title, text);
            _vm.RestartRequested += ExitForRestart;
            StartupGuard.UseLog(_vm.Service.Log);
            StartupGuard.Trace("window constructed");
            SourceInitialized += (s, e) => { StartupGuard.Trace("SourceInitialized"); WindowChrome.UseDarkTitleBar(this); FitToScreen(); };
            // First show: if no frame is rendered within a few seconds (GPU driver, overlay), restart in software rendering.
            IsVisibleChanged += (s, e) => { if (IsVisible) StartupGuard.ArmRenderWatchdog(); };
            ContentRendered += (s, e) => StartupGuard.Rendered();
            Loaded += async (s, e) =>
            {
                StartupGuard.Trace("Loaded");
                await BeginAsync();
                if (_options.TourDir != null) await TourAsync(_options.TourDir);
            };
            SizeChanged += (s, e) => FitBanner();
            Closing += OnClosing;
            Closed += (s, e) => _tray?.Dispose();
        }

        /// <summary>A small laptop screen at 125-150 % (1366x768) has less room than the default size: never open larger
        /// than the work area, and let the window shrink to it.</summary>
        private void FitToScreen()
        {
            var wa = SystemParameters.WorkArea;
            if (wa.Width < 1 || wa.Height < 1) return;
            MinWidth = Math.Min(MinWidth, wa.Width);
            MinHeight = Math.Min(MinHeight, wa.Height);
            if (Width > wa.Width) Width = wa.Width;
            if (Height > wa.Height) Height = wa.Height;
        }

        /// <summary>Short window (small laptop screen, 150 %): the banner drops its crest rather than shrinking the module
        /// names until they cannot be read.</summary>
        private void FitBanner()
        {
            bool shortWindow = ActualHeight < 600;
            BannerCrest.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            BannerBody.Margin = new Thickness(16, shortWindow ? 16 : 22, 16, 78);
        }

        private async Task BeginAsync()
        {
            if (_started) return;
            _started = true;
            await _vm.StartAsync();
            _ = _vm.LoadScansAsync();
        }

        /// <summary>Started by the optional Startup shortcut (--minimized): no window, only the tray icon; checks and the
        /// background pre-download run as usual, and a balloon says when an update is ready to install.</summary>
        public void StartInTray()
        {
            ShowTray();
            _ = BeginAsync();
        }

        public void ShowFromTray()
        {
            if (_tray != null) _tray.Visible = false;
            _vm.InTray = false;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        private void ShowTray()
        {
            if (_tray == null)
                _tray = new TrayIcon(Program.ExePath, () => Dispatcher.BeginInvoke(new Action(ShowFromTray)),
                    () => Dispatcher.BeginInvoke(new Action(() => _ = _vm.CheckAsync(true))),
                    () => Dispatcher.BeginInvoke(new Action(ExitFromTray)));
            _tray.Visible = true;
            _vm.InTray = true;
        }

        /// <summary>A newer updater asks this one to exit: only when nothing is running (an install is never cut).</summary>
        public bool CanExitForTakeOver => !_vm.Busy;

        /// <summary>Quits for real (restart into a new updater, or a newer one takes over), even from the tray.</summary>
        public void ExitForRestart()
        {
            _exiting = true;
            Close();
            Application.Current.Shutdown();
        }

        private void ExitFromTray()
        {
            _exiting = true;
            Close();
            if (!IsLoaded) Application.Current.Shutdown(); // never shown: Close() does not end the app
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // With "Start with Windows" on, closing the window keeps the app in the notification area (pre-download),
            // unless the player chose Exit there. Otherwise closing quits, as before.
            if (_exiting || _options.TourDir != null || !_vm.StartWithWindows || _vm.Busy) return;
            e.Cancel = true;
            Hide();
            ShowTray();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.Notify("Still running in the notification area", "It downloads mod updates in advance. Right-click the icon › Exit to close it, or turn off \"Start with Windows\" in Settings.");
            }
        }

        /// <summary>Dev only: walks through every screen against the local fake release and saves a PNG of each.</summary>
        // Dev tour only, LOTR_TOUR_FRAMES=1: a frame of the window every 200 ms in <tour>\frames (for the demo video),
        // rendered off-screen like Shot(), so windows above the updater never end up in the video.
        private DispatcherTimer _frameTimer;
        private int _frame;
        private bool Filming => _frameTimer != null;

        private void StartFrames(string dir)
        {
            var frames = Path.Combine(dir, "frames");
            Directory.CreateDirectory(frames);
            _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (s, e) => Save(Path.Combine(frames, (_frame++).ToString("D5") + ".png")), Dispatcher);
            _frameTimer.Start();
        }

        private async Task TourAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            if (Environment.GetEnvironmentVariable("LOTR_TOUR_FRAMES") == "1") StartFrames(dir);
            if (Environment.GetEnvironmentVariable("LOTR_TOUR_ONLY") == "home") { await HomeSizesAsync(dir); Close(); return; }
            await Shot(dir, "01-home-update-available");
            _vm.ReviewCommand.Execute(null);
            await Shot(dir, "02-plan");
            var install = _vm.InstallAsync();
            if (Filming) await Task.Delay(2500); // a local install takes about a second: catch it on the way
            await Shot(dir, "03-progress-downloading");
            await install;
            _vm.Page = Page.Progress;
            await Shot(dir, "04-progress-done");
            _vm.Page = Page.Home;
            await Shot(dir, "05-home-ready");
            double w = Width, h = Height;
            Width = MinWidth; Height = MinHeight;
            await Shot(dir, "05a-home-ready-smallest-window");
            Width = w; Height = h;
            _vm.Page = Page.News;
            await Shot(dir, "05b-news");
            _vm.Page = Page.Home;
            _vm.TickLauncherModsCommand.Execute(null);
            await Task.Delay(1500);
            await Shot(dir, "05c-home-ticked");
            _vm.UpdaterUpdateText = "A new version of this updater (0.3.1) is ready. It installs itself as soon as nothing is running.";
            await Shot(dir, "05d-home-updater-banner");
            _vm.UpdaterUpdateText = null;

            // Damage the FAKE install a little so Verify has something to show.
            var g = _vm.Service.Game;
            var ev = Path.Combine(g.ModulesPath, "LotRMPEvent");
            var xml = Directory.GetFiles(ev, "*.xml", SearchOption.AllDirectories).FirstOrDefault(f => !f.EndsWith("SubModule.xml"));
            if (xml != null)
            {
                File.AppendAllText(xml, "<!-- edited by hand -->");
                // .NET Framework refuses ':' in paths: let cmd write the alternate data stream, as a browser download would.
                var psi = new System.Diagnostics.ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c echo [ZoneTransfer]>\"" + xml + ":Zone.Identifier\"") { CreateNoWindow = true, UseShellExecute = false };
                System.Diagnostics.Process.Start(psi)?.WaitForExit(5000);
            }
            _vm.Page = Page.Verify;
            await _vm.VerifyAsync();
            await Shot(dir, "06-verify");
            _vm.RepairCommand.Execute(null);
            await Shot(dir, "07-plan-repair");
            _vm.Page = Page.Support;
            await _vm.SendSupportAsync();
            await Shot(dir, "08-support");
            _vm.Page = Page.Settings;
            await Shot(dir, "09-settings");
            await _vm.LoadScansAsync();
            _vm.Page = Page.About;
            await Shot(dir, "10-about");
            _frameTimer?.Stop();
            Close();
        }

        /// <summary>Dev tour, LOTR_TOUR_ONLY=home: the Home screen as it is (offline, no game, older release...) at the
        /// default size, on a 1366x768 laptop at 125 % and at 150 %, then Settings.</summary>
        private async Task HomeSizesAsync(string dir)
        {
            await Shot(dir, "home");
            if (Environment.GetEnvironmentVariable("LOTR_TOUR_CLICK") == "1")
            {
                // The round button as a player clicks it; then wait for whatever it started.
                _vm.SealCommand.Execute(null);
                await Task.Delay(500);
                for (int i = 0; i < 120 && _vm.Busy; i++) await Task.Delay(250);
                await Shot(dir, "after-click");
                _vm.Page = Page.Home;
                await Shot(dir, "home-after-click");
            }
            MinWidth = 800; MinHeight = 400;
            Width = 1093; Height = 576;
            await Shot(dir, "home-1366-125pc");
            Width = 910; Height = 480;
            await Shot(dir, "home-1366-150pc");
            Width = 1060; Height = 700;
            _vm.Page = Page.Settings;
            await Shot(dir, "settings");
        }

        private async Task Shot(string dir, string name)
        {
            await Task.Delay(Filming ? 3000 : 250); // filming: hold each screen long enough to be read
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(Path.Combine(dir, name + ".png"));
        }

        private void Save(string path)
        {
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(path)) enc.Save(f);
        }
    }
}
