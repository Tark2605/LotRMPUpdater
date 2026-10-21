using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater
{
    /// <summary>
    /// One updater per home folder. A second start (Start menu while it sits in the tray) asks the running copy to show its
    /// window and waits for its answer; a NEWER exe asks an older running copy to exit and takes over. Named kernel objects
    /// (per home): the mutex, "show" and "exit" requests, "ack" (alive, window shown) and "bye" (exiting) answers.
    /// home\instance.txt names the running copy (PID, version) so a second start knows whom it talks to.
    /// </summary>
    public sealed class SingleInstance : IDisposable, IInstanceChannel
    {
        private readonly Mutex _mutex;
        private readonly EventWaitHandle _show, _exit, _ack, _bye;
        private readonly UpdaterPaths _paths;
        private readonly string _version;

        private SingleInstance(UpdaterPaths p, string version, Mutex m, EventWaitHandle show, EventWaitHandle exit, EventWaitHandle ack, EventWaitHandle bye, bool first)
        {
            _paths = p;
            _version = version;
            _mutex = m;
            _show = show;
            _exit = exit;
            _ack = ack;
            _bye = bye;
            IsFirst = first;
            if (first) WriteInfo();
        }

        public bool IsFirst { get; private set; }

        public static SingleInstance TryAcquire(UpdaterPaths p, string version)
        {
            var id = LotR.Releases.Hashing.Sha256Util.Of(Encoding.UTF8.GetBytes(p.Home.ToLowerInvariant())).Substring(0, 16);
            var m = new Mutex(false, @"Local\LotRMPUpdater-" + id);
            bool owned;
            try { owned = m.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // the previous holder died: the lock is ours
            // "show" keeps the name of 0.1.x, so an older running copy still shows itself when asked.
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LotRMPUpdater-show-" + id);
            var exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LotRMPUpdater-exit-" + id);
            var ack = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LotRMPUpdater-ack-" + id);
            var bye = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\LotRMPUpdater-bye-" + id);
            return new SingleInstance(p, version, m, show, exit, ack, bye, owned);
        }

        // ------------------------------------------------------------ running copy side

        /// <summary>Calls <paramref name="onShow"/> / <paramref name="onExit"/> (on a background thread) for each request
        /// of another start. The handlers answer with <see cref="Ack"/> or <see cref="Bye"/>.</summary>
        public void Listen(Action onShow, Action onExit)
        {
            var t = new Thread(() =>
            {
                try
                {
                    var handles = new WaitHandle[] { _show, _exit };
                    while (true)
                    {
                        int i = WaitHandle.WaitAny(handles);
                        if (i == 0) onShow();
                        else if (i == 1) onExit();
                    }
                }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true, Name = "single-instance" };
            t.Start();
        }

        /// <summary>"Alive, and my window is shown" (sent from the UI thread, so it proves the UI is not stuck).</summary>
        public void Ack() { try { _ack.Set(); } catch (ObjectDisposedException) { } }

        /// <summary>"Exiting now": the asking start can take the lock in a moment.</summary>
        public void Bye() { try { _bye.Set(); } catch (ObjectDisposedException) { } }

        // ------------------------------------------------------------ second start side (IInstanceChannel)

        public void RequestShow()
        {
            _ack.Reset();
            _bye.Reset();
            _show.Set();
        }

        public void RequestExit()
        {
            _ack.Reset();
            _bye.Reset();
            _exit.Set();
        }

        public PeerReply WaitReply(TimeSpan timeout)
        {
            int i = WaitHandle.WaitAny(new WaitHandle[] { _ack, _bye }, timeout);
            return i == 0 ? PeerReply.Ack : i == 1 ? PeerReply.Bye : PeerReply.None;
        }

        public bool WaitOwnership(TimeSpan timeout)
        {
            if (IsFirst) return true;
            bool owned;
            try { owned = _mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { owned = true; }
            if (owned)
            {
                IsFirst = true;
                WriteInfo();
            }
            return owned;
        }

        // ------------------------------------------------------------ home\instance.txt

        private string InfoFile => Path.Combine(_paths.Home, "instance.txt");

        private void WriteInfo()
        {
            try
            {
                Directory.CreateDirectory(_paths.Home);
                File.WriteAllText(InfoFile, "pid=" + Process.GetCurrentProcess().Id + "\nversion=" + _version + "\nexe=" + Program.ExePath + "\n", new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        /// <summary>The running copy named by home\instance.txt, when that process is still alive (null: none, or a 0.1.x copy).</summary>
        public PeerInfo ReadPeer()
        {
            try
            {
                if (!File.Exists(InfoFile)) return null;
                var kv = File.ReadAllLines(InfoFile).Select(l => l.Split(new[] { '=' }, 2)).Where(a => a.Length == 2).ToDictionary(a => a[0].Trim(), a => a[1].Trim());
                if (!kv.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)) return null;
                if (pid == Process.GetCurrentProcess().Id) return null;
                using (var p = Process.GetProcessById(pid))
                    if (!PeerProcesses.LooksLikeUpdater(p)) return null;
                kv.TryGetValue("version", out var v);
                kv.TryGetValue("exe", out var exe);
                return new PeerInfo { Pid = pid, Version = v, Exe = exe };
            }
            catch (Exception) { return null; } // not running any more (ArgumentException) or unreadable
        }

        public void Dispose()
        {
            if (IsFirst)
            {
                try
                {
                    // Only our own line: a newer copy may already have written its own.
                    if (File.Exists(InfoFile) && File.ReadAllText(InfoFile).StartsWith("pid=" + Process.GetCurrentProcess().Id + "\n", StringComparison.Ordinal)) File.Delete(InfoFile);
                }
                catch (Exception) { }
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _mutex.Dispose();
            _show.Dispose();
            _exit.Dispose();
            _ack.Dispose();
            _bye.Dispose();
        }
    }

    public sealed class PeerInfo
    {
        public int Pid { get; set; }
        public string Version { get; set; }
        public string Exe { get; set; }
    }

    /// <summary>Other LotRMPUpdater processes of this user (same Windows session, readable exe path).</summary>
    public static class PeerProcesses
    {
        public static bool LooksLikeUpdater(Process p)
        {
            try { return p.ProcessName.StartsWith("LotRMPUpdater", StringComparison.OrdinalIgnoreCase) && !p.HasExited; }
            catch (Exception) { return false; }
        }

        public static string PathOf(Process p)
        {
            try { return p.MainModule?.FileName; } catch (Exception) { return null; }
        }

        public static string VersionOf(string exe)
        {
            try { return SelfInstallPolicy.NormalizeFileVersion(FileVersionInfo.GetVersionInfo(exe).FileVersion); } catch (Exception) { return null; }
        }

        /// <summary>The processes to close for this home: the one named by instance.txt when known, else every updater of
        /// this session (only under the home's parent folder for a test home, so a dev run never touches a real copy).</summary>
        public static List<Process> Find(UpdaterPaths paths, bool testHome, int? knownPid)
        {
            var result = new List<Process>();
            int me = Process.GetCurrentProcess().Id;
            int session = Process.GetCurrentProcess().SessionId;
            if (knownPid.HasValue && knownPid.Value != me)
            {
                try
                {
                    var p = Process.GetProcessById(knownPid.Value);
                    if (LooksLikeUpdater(p)) { result.Add(p); return result; }
                    p.Dispose();
                }
                catch (Exception) { }
            }
            var root = Path.GetDirectoryName(paths.Home.TrimEnd('\\'));
            foreach (var p in Process.GetProcesses())
            {
                bool keep = false;
                try
                {
                    if (p.Id != me && p.SessionId == session && LooksLikeUpdater(p))
                    {
                        var exe = PathOf(p);
                        keep = exe != null && (!testHome || (root != null && Path.GetFullPath(exe).StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
                    }
                }
                catch (Exception) { }
                if (keep) result.Add(p); else p.Dispose();
            }
            return result;
        }

        /// <summary>Processes running a given exe file (another start of the installed copy).</summary>
        public static List<Process> Running(string exe)
        {
            var result = new List<Process>();
            int me = Process.GetCurrentProcess().Id;
            var full = Path.GetFullPath(exe);
            foreach (var p in Process.GetProcesses())
            {
                bool keep = false;
                try { keep = p.Id != me && LooksLikeUpdater(p) && string.Equals(PathOf(p), full, StringComparison.OrdinalIgnoreCase); } catch (Exception) { }
                if (keep) result.Add(p); else p.Dispose();
            }
            return result;
        }

        public static void Kill(IEnumerable<Process> ps, Action<string> log)
        {
            foreach (var p in ps)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        log?.Invoke("Closing the other updater (PID " + p.Id + ", " + (PathOf(p) ?? p.ProcessName) + ").");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (Exception ex) { log?.Invoke("Could not close PID " + p.Id + ": " + ex.Message); }
                finally { p.Dispose(); }
            }
        }
    }
}
