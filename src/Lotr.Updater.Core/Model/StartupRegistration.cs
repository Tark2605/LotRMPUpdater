using System;
using System.IO;

namespace Lotr.Updater.Core.Model
{
    /// <summary>Creates a Windows shortcut (.lnk). The app uses WScript.Shell; tests use a fake.</summary>
    public interface IShortcutWriter
    {
        void Create(string lnkPath, string targetExe, string arguments, string description);
    }

    /// <summary>
    /// Optional "Start with Windows (minimised to the tray)": ONE shortcut in the user's own Startup folder
    /// (shell:startup, %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup). No registry Run key, no scheduled task,
    /// no service; off by default; removing the shortcut (here, by hand, or from Task Manager › Startup apps) turns it off.
    /// </summary>
    public sealed class StartupRegistration
    {
        public const string ShortcutFileName = "LotR MP Event updater.lnk";
        public const string Arguments = "--minimized";

        private readonly IShortcutWriter _writer;

        public StartupRegistration(string startupFolder, string targetExe, IShortcutWriter writer)
        {
            StartupFolder = startupFolder;
            TargetExe = targetExe;
            _writer = writer;
        }

        /// <summary>The real per-user Startup folder (never used in --dev runs or tests).</summary>
        public static string UserStartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        public string StartupFolder { get; }
        public string TargetExe { get; }
        public string ShortcutPath => Path.Combine(StartupFolder, ShortcutFileName);
        public bool IsEnabled => File.Exists(ShortcutPath);

        public void Enable()
        {
            if (string.IsNullOrEmpty(StartupFolder)) throw new InvalidOperationException("Windows did not give a Startup folder for this user.");
            Directory.CreateDirectory(StartupFolder);
            _writer.Create(ShortcutPath, TargetExe, Arguments, "Starts the LotR MP Event updater in the notification area, to download mod updates in advance");
            if (!File.Exists(ShortcutPath)) throw new IOException("Windows did not create the shortcut " + ShortcutPath);
        }

        public void Disable()
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
    }
}
