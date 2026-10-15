using System;
using System.IO;
using System.Linq;
using System.Windows;
using Lotr.Updater.Core;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Platform;

namespace Lotr.Updater
{
    /// <summary>Started by Windows "Installed apps" (UninstallString = "...exe --uninstall") or from Settings.</summary>
    public partial class UninstallWindow : Window
    {
        private readonly StartOptions _options;
        private readonly UpdaterService _svc;

        public UninstallWindow(StartOptions options)
        {
            _options = options;
            InitializeComponent();
            SourceInitialized += (s, e) => WindowChrome.UseDarkTitleBar(this);
            var paths = options.Dev ? new UpdaterPaths(options.Home) : UpdaterPaths.Default();
            _svc = new UpdaterService(new UpdaterConfig { Dev = options.Dev, FixedGamePath = options.Game, BaseUrl = options.Server ?? Core.Net.ServerEndpoints.ProductionBase }, paths);
            _svc.LocateGame();
            var m = _svc.InstalledManifest;
            if (m == null || _svc.Game == null)
            {
                RemoveMods.IsEnabled = false;
                ModList.Text = "(no installed release recorded on this PC)";
            }
            else ModList.Text = string.Join(Environment.NewLine, m.Modules.Select(x => Path.Combine(_svc.Game.ModulesPath, x.Folder)));
        }

        private void OnCancel(object sender, RoutedEventArgs e) => Close();

        private void OnUninstall(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RemoveMods.IsChecked == true && _svc.InstalledManifest != null && _svc.Game != null)
                {
                    var running = new Core.Game.SystemProcessProbe().RunningGameProcesses();
                    if (running.Count > 0) throw new InvalidOperationException("Close Bannerlord and its launcher first (" + string.Join(", ", running.Select(r => r.Name)) + ").");
                    _svc.Engine.RemoveModules(_svc.InstalledManifest, _svc.Game.ModulesPath);
                }
                if (_options.Dev)
                {
                    _svc.Log.Info("DEV MODE: uninstall simulated (registry, shortcut and app folder left alone).");
                    MessageBox.Show("Dev mode: uninstall simulated.", Title);
                }
                else
                {
                    SelfInstall.Remove(_svc.Paths);
                    MessageBox.Show("The updater was removed." + (RemoveMods.IsChecked == true ? " The LotR mod folders were deleted." : " The mod files were kept."), Title);
                }
                Application.Current.Shutdown();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Message.Text = ex.Message;
                Message.Visibility = Visibility.Visible;
            }
        }
    }
}
