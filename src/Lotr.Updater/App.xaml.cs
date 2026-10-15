using System;
using System.Windows;
using System.Windows.Threading;

namespace Lotr.Updater
{
    public partial class App : Application
    {
        private readonly StartOptions _options;

        public App(StartOptions options)
        {
            _options = options;
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
            Window w = _options.Uninstall ? (Window)new UninstallWindow(_options) : new MainWindow(_options);
            MainWindow = w;
            w.Show();
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
