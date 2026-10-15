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

        public MainWindow(StartOptions options)
        {
            _options = options;
            InitializeComponent();
            _vm = new MainViewModel(options);
            DataContext = _vm;
            _vm.LogAppended += () => LiveLog.ScrollToEnd();
            SourceInitialized += (s, e) => WindowChrome.UseDarkTitleBar(this);
            Loaded += async (s, e) =>
            {
                if (options.Minimized) WindowState = WindowState.Minimized;
                await _vm.StartAsync();
                _ = _vm.LoadScansAsync();
                if (_options.TourDir != null) await TourAsync(_options.TourDir);
            };
        }

        /// <summary>Dev only: walks through every screen against the local fake release and saves a PNG of each.</summary>
        private async Task TourAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            await Shot(dir, "01-home-update-available");
            _vm.ReviewCommand.Execute(null);
            await Shot(dir, "02-plan");
            var install = _vm.InstallAsync();
            await Task.Delay(2500);
            await Shot(dir, "03-progress-downloading");
            await install;
            _vm.Page = Page.Progress;
            await Shot(dir, "04-progress-done");
            _vm.Page = Page.Home;
            await Shot(dir, "05-home-ready");

            // Damage the FAKE install a little so Verify has something to show.
            var g = _vm.Service.Game;
            var ev = Path.Combine(g.ModulesPath, "LotRMPEvent");
            var xml = Directory.GetFiles(ev, "*.xml", SearchOption.AllDirectories).FirstOrDefault(f => !f.EndsWith("SubModule.xml"));
            if (xml != null)
            {
                File.AppendAllText(xml, "<!-- edited by hand -->");
                // .NET Framework refuses ':' in paths: let cmd write the alternate data stream, as a browser download would.
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c echo [ZoneTransfer]>\"" + xml + ":Zone.Identifier\"") { CreateNoWindow = true, UseShellExecute = false };
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
            Close();
        }

        private async Task Shot(string dir, string name)
        {
            await Task.Delay(250);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = File.Create(Path.Combine(dir, name + ".png"))) enc.Save(f);
        }
    }
}
