using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Lotr.Updater.Core.Logging
{
    public enum LogLevel { Info, Warn, Error, Detail }

    public sealed class LogLine
    {
        public LogLine(DateTime time, LogLevel level, string text) { Time = time; Level = level; Text = text; }
        public DateTime Time { get; }
        public LogLevel Level { get; }
        public string Text { get; }
        public override string ToString() =>
            Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + (Level == LogLevel.Info ? "    " : Level == LogLevel.Detail ? " .. " : Level.ToString().ToUpperInvariant().PadRight(4)) + " " + Text;
    }

    public interface IUpdaterLog
    {
        void Write(LogLevel level, string text);
    }

    /// <summary>
    /// Readable, append-only daily log in %LOCALAPPDATA%\LotRMPUpdater\logs\YYYY-MM-DD.log (CONTRACTS §6), plus a live feed for
    /// the Progress screen. The local file keeps full paths (it never leaves the PC); <see cref="LogMasker"/> masks exports.
    /// </summary>
    public sealed class UpdaterLog : IUpdaterLog
    {
        private readonly string _dir;
        private readonly object _gate = new object();
        private readonly List<LogLine> _session = new List<LogLine>();

        public UpdaterLog(string dir) { _dir = dir; }

        public event Action<LogLine> Line;

        public string CurrentFile => _dir == null ? null : Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        public IReadOnlyList<LogLine> Session { get { lock (_gate) return _session.ToList(); } }

        public void Write(LogLevel level, string text)
        {
            var line = new LogLine(DateTime.Now, level, text ?? "");
            lock (_gate)
            {
                _session.Add(line);
                if (_dir != null)
                {
                    try
                    {
                        Directory.CreateDirectory(_dir);
                        File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
                    }
                    catch (IOException) { /* logging must never break an install */ }
                    catch (UnauthorizedAccessException) { }
                }
            }
            Line?.Invoke(line);
        }

        public void Info(string t) => Write(LogLevel.Info, t);
        public void Warn(string t) => Write(LogLevel.Warn, t);
        public void Error(string t) => Write(LogLevel.Error, t);
        public void Detail(string t) => Write(LogLevel.Detail, t);

        /// <summary>The last log files (today first), concatenated, for the Support screen. Not masked.</summary>
        public string ReadRecent(int days = 3, int maxChars = 1_500_000)
        {
            if (_dir == null || !Directory.Exists(_dir)) return string.Join(Environment.NewLine, Session.Select(l => l.ToString()));
            var files = Directory.GetFiles(_dir, "*.log").OrderByDescending(f => f, StringComparer.Ordinal).Take(days).Reverse();
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.AppendLine("===== " + Path.GetFileName(f) + " =====");
                try
                {
                    using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(s, Encoding.UTF8))
                        sb.Append(r.ReadToEnd());
                }
                catch (IOException ex) { sb.AppendLine("(unreadable: " + ex.Message + ")"); }
            }
            var text = sb.ToString();
            return text.Length > maxChars ? "(older lines cut)\n" + text.Substring(text.Length - maxChars) : text;
        }

        public void Prune(int keepDays = 30)
        {
            try
            {
                if (_dir == null || !Directory.Exists(_dir)) return;
                foreach (var f in Directory.GetFiles(_dir, "*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keepDays))
                    File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Masks the Windows user name (and profile path) in anything that leaves the PC.</summary>
    public static class LogMasker
    {
        public static string Mask(string text) => Mask(text, Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName);

        public static string Mask(string text, string userName, string profilePath, string machineName = null)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var r = text;
            if (!string.IsNullOrEmpty(profilePath) && profilePath.Length > 3)
            {
                // Same placeholder as lotr-services (which masks again server-side): C:\Users\<user>
                var parent = System.IO.Path.GetDirectoryName(profilePath.TrimEnd('\\'));
                var masked = parent == null ? "<user>" : parent.TrimEnd('\\') + "\\<user>";
                r = Regex.Replace(r, Regex.Escape(profilePath), masked.Replace("$", "$$"), RegexOptions.IgnoreCase);
                r = Regex.Replace(r, Regex.Escape(profilePath.Replace('\\', '/')), masked.Replace('\\', '/').Replace("$", "$$"), RegexOptions.IgnoreCase);
            }
            // 8.3 short form of the profile (C:\Users\UTILSA~1).
            r = Regex.Replace(r, @"(?i)([A-Z]:(?:\\|/)+Users(?:\\|/)+)[^\\/\s""']+~\d", "$1<user>"); // also JSON-escaped C:\\Users\\X~1
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
            {
                r = Regex.Replace(r, @"(?i)([\\/]Users[\\/])" + Regex.Escape(userName) + @"(?=[\\/""'\s]|$)", "$1<user>");
                r = Regex.Replace(r, @"(?i)(?<![0-9A-Za-z])" + Regex.Escape(userName) + @"(?![0-9A-Za-z])", "<user>");
            }
            if (!string.IsNullOrEmpty(machineName) && machineName.Length >= 3)
                r = Regex.Replace(r, @"(?i)(?<![0-9A-Za-z])" + Regex.Escape(machineName) + @"(?![0-9A-Za-z])", "<pc>");
            return r;
        }
    }
}
