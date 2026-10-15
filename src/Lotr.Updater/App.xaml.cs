using System;
using System.Windows;
using System.Windows.Threading;

namespace Lotr.Updater
{
    public partial class App : Application
    {
        private readonly StartOptions _options;
        private readonly SingleInstance _instance;

        public App(StartOptions options, SingleInstance instance = null)
        {
            _options = options;
            _instance = instance;
            DispatcherUnhandledException += OnUnhandled;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (_options.Error != null)
            {
                MessageBox.Show(_options.Error, "LotR MP Event updater", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
                return;
            }
            if (_options.Uninstall)
            {
                var u = new UninstallWindow(_options);
                MainWindow = u;
                u.Show();
                return;
            }
            var w = new MainWindow(_options);
            MainWindow = w;
            // Another start of the app (Start menu, shortcut) brings this window back instead of running twice.
            _instance?.ListenForShow(() => Dispatcher.BeginInvoke(new Action(w.ShowFromTray)));
            if (_options.Minimized && _options.TourDir == null) w.StartInTray(); // "Start with Windows": no window, tray icon only
            else w.Show();
        }

        private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Never crash silently: show the message; the log keeps the detail.
            MessageBox.Show("Something unexpected happened:\n\n" + e.Exception.Message + "\n\nNothing was half-installed: an interrupted update is undone the next time the app starts. You can send the log from the Support page.",
                "LotR MP Event updater", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }
    }
}
