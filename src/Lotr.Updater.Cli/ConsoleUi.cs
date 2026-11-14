using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Lotr.Updater.Core.Install;

namespace Lotr.Updater.Cli
{
    /// <summary>Terminal output: plain English lines, one rewritten progress line on a terminal, prompts.</summary>
    public sealed class ConsoleUi
    {
        private readonly TextReader _in;
        private readonly object _gate = new object();
        private int _progressWidth;

        public ConsoleUi(TextReader stdin, TextWriter stdout, TextWriter stderr, bool rewriteLines)
        {
            _in = stdin;
            Out = stdout;
            Err = stderr;
            RewriteLines = rewriteLines;
        }

        public TextWriter Out { get; }
        public TextWriter Err { get; }
        /// <summary>A terminal: the progress line is rewritten with '\r' (else a line every 2 % or 10 s).</summary>
        public bool RewriteLines { get; }

        public void Line(string text = "")
        {
            lock (_gate)
            {
                try
                {
                    ClearProgress();
                    Out.WriteLine(text);
                    Out.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
        }

        public void Error(string text)
        {
            lock (_gate)
            {
                try
                {
                    ClearProgress();
                    Out.Flush();
                    Err.WriteLine(text);
                    Err.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
        }

        /// <summary>Asks a yes/no question; anything but y/yes (or the end of the input) is no.</summary>
        public bool Confirm(string question)
        {
            lock (_gate)
            {
                try
                {
                    ClearProgress();
                    Out.Write(question + " [y/N] ");
                    Out.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
            var answer = _in.ReadLine();
            if (answer == null) Line();
            answer = (answer ?? "").Trim().ToLowerInvariant();
            return answer == "y" || answer == "yes";
        }

        public string Ask(string question)
        {
            lock (_gate)
            {
                try
                {
                    ClearProgress();
                    Out.Write(question);
                    Out.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
            return _in.ReadLine();
        }

        internal void Progress(string text)
        {
            lock (_gate)
            {
                try
                {
                    if (!RewriteLines) { Out.WriteLine(text); Out.Flush(); return; }
                    var pad = _progressWidth > text.Length ? new string(' ', _progressWidth - text.Length) : "";
                    _progressWidth = text.Length;
                    Out.Write("\r" + text + pad);
                    Out.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
        }

        private void ClearProgress()
        {
            if (_progressWidth == 0) return;
            int width = _progressWidth;
            _progressWidth = 0;
            Out.Write("\r" + new string(' ', width) + "\r");
        }

        public void EndProgress()
        {
            lock (_gate)
            {
                if (_progressWidth == 0) return;
                _progressWidth = 0;
                try
                {
                    Out.WriteLine();
                    Out.Flush();
                }
                catch (Exception ex) when (IsClosedOutput(ex)) { }
            }
        }

        /// <summary>
        /// The terminal went away (window closed during an install: SIGHUP, then EIO on every write). Output is best effort:
        /// progress is reported from inside the install engine, so a write error there must never turn the clean, cancellable
        /// stop that SIGHUP asks for into an unexpected failure. The log file keeps everything.
        /// </summary>
        private static bool IsClosedOutput(Exception ex) => ex is IOException || ex is ObjectDisposedException;

        public static string Bytes(long b) => Units.Bytes(b);
    }

    /// <summary>Engine progress as a terminal line: phase, bytes, speed, time left, queue position.</summary>
    public sealed class ConsoleProgress : IProgress<EngineProgress>
    {
        private readonly ConsoleUi _ui;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastMs = -100000;
        private double _lastFraction = -1;
        private EnginePhase? _lastPhase;
        private string _lastMessage;

        public ConsoleProgress(ConsoleUi ui)
        {
            _ui = ui;
        }

        public void Report(EngineProgress p)
        {
            if (p == null) return;
            long now = _clock.ElapsedMilliseconds;
            bool phaseChanged = _lastPhase != p.Phase || (p.Phase == EnginePhase.Queue && p.Message != _lastMessage);
            if (_ui.RewriteLines)
            {
                if (!phaseChanged && now - _lastMs < 250 && p.Phase != EnginePhase.Done) return;
            }
            else if (!phaseChanged && p.Phase != EnginePhase.Done && Math.Abs(p.Fraction - _lastFraction) < 0.02 && now - _lastMs < 10000)
            {
                return;
            }
            _lastMs = now;
            _lastFraction = p.Fraction;
            _lastPhase = p.Phase;
            _lastMessage = p.Message;
            _ui.Progress(Format(p));
        }

        public static string Format(EngineProgress p)
        {
            switch (p.Phase)
            {
                case EnginePhase.Downloading when p.Total > 0:
                    var s = FormattableString.Invariant($"Downloading {p.Fraction * 100:0.0}%  {Units.Bytes(p.Done)} / {Units.Bytes(p.Total)}");
                    if (p.BytesPerSecond > 0)
                    {
                        s += FormattableString.Invariant($"  {p.BytesPerSecond / (1024 * 1024):0.0} MB/s");
                        var left = (p.Total - p.Done) / p.BytesPerSecond;
                        if (left > 0 && left < 360000) s += "  " + Eta(left) + " left";
                    }
                    return s;
                case EnginePhase.Preparing when p.Total > 0:
                    return FormattableString.Invariant($"Preparing files {p.Fraction * 100:0}%");
                default:
                    return p.Message ?? p.Phase.ToString();
            }
        }

        private static string Eta(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString("00", CultureInfo.InvariantCulture) + " min"
                : t.Minutes > 0 ? t.Minutes.ToString(CultureInfo.InvariantCulture) + " min " + t.Seconds.ToString("00", CultureInfo.InvariantCulture) + " s" : t.Seconds.ToString(CultureInfo.InvariantCulture) + " s";
        }
    }
}
