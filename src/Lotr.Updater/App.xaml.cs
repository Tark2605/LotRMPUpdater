using System;
using System.Windows;
using System.Windows.Threading;
using Lotr.Updater.Platform;

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
            StartupGuard.Trace("OnStartup");
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
            StartupGuard.Trace("main window created");
            // Another start of the app (Start menu, shortcut) brings this window back instead of running twice; the answer is
            // sent from the UI thread, so a start that gets no answer knows this one is stuck. A newer version asks this
            // one to exit (it takes over), unless an install is running: then this one stays and shows itself.
            _instance?.Listen(
                onShow: () => Dispatcher.BeginInvoke(new Action(() => { w.ShowFromTray(); _instance.Ack(); })),
                onExit: () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (w.CanExitForTakeOver)
                    {
                        StartupGuard.Trace("a newer updater takes over: exiting");
                        _instance.Bye();
                        w.ExitForRestart();
                    }
                    else { w.ShowFromTray(); _instance.Ack(); }
                })));
            if (_options.Minimized && _options.TourDir == null)
            {
                w.StartInTray(); // "Start with Windows": no window, tray icon only
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => StartupGuard.MarkReady("tray icon up")));
            }
            else w.Show();
        }

        private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (!StartupGuard.Ready)
            {
                // During start-up there may be no usable window: report, tell, and quit so nothing keeps the lock.
                StartupGuard.ShowFatal(StartupGuard.WriteCrashReport(e.Exception, "start-up (UI thread)"));
                Shutdown(1);
                return;
            }
            // Never crash silently, never show raw (localized, technical) exception text: the full detail goes to the log
            // and logs\crash-*.txt (both reach the team through the Support page).
            StartupGuard.WriteCrashReport(e.Exception, "the UI thread (the app carries on)");
            var kind = Core.Install.PlayerErrors.Classify(e.Exception);
            var hint = kind == Core.Install.ProblemKind.DiskFull ? " Your disk looks full: free some space."
                     : kind == Core.Install.ProblemKind.AccessDenied ? " Windows refused access to a file; check that the folder is not read-only."
                     : "";
            MessageBox.Show("Something unexpected happened in the updater." + hint + "\n\nNothing was half-installed: an interrupted update is undone the next time the app starts. The details were saved; you can send them to the team from the Support page.",
                "LotR MP Event updater", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
