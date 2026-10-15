using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using LotR.Releases.Install;
using LotR.Releases.Model;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.Objects;
using Lotr.Updater.Core.Planning;

namespace Lotr.Updater.Core.Install
{
    public enum EnginePhase { Checking, Queue, Downloading, Preparing, Swapping, Finishing, Done }

    public sealed class EngineProgress
    {
        public EnginePhase Phase { get; set; }
        public string Message { get; set; }
        public long Done { get; set; }
        public long Total { get; set; }
        public double BytesPerSecond { get; set; }
        public double Fraction => Total <= 0 ? 0 : Math.Min(1, (double)Done / Total);
    }

    /// <summary>An install step failed; nothing was changed (or everything was rolled back). Message is for the player.</summary>
    public sealed class InstallException : Exception
    {
        public InstallException(string message, Exception inner = null) : base(message, inner) { }
        public bool RolledBack { get; set; }
    }

    public sealed class EngineOptions
    {
        /// <summary>Parallel HTTP connections (PLAN: 2-4).</summary>
        public int Connections { get; set; } = 3;
        /// <summary>Free space kept on each drive on top of what the update needs.</summary>
        public long SafetyMarginBytes { get; set; } = 256L << 20;
    }

    public sealed class VerifyReport
    {
        public int Checked { get; set; }
        public int Ok { get; set; }
        public List<string> Missing { get; } = new List<string>();
        public List<string> Modified { get; } = new List<string>();
        public List<string> Extra { get; } = new List<string>();
        public int MarkOfTheWebCleared { get; set; }
        public List<DuplicateModule> Duplicates { get; } = new List<DuplicateModule>();
        public InstallPlan RepairPlan { get; set; }
        public bool Healthy => Missing.Count == 0 && Modified.Count == 0;
    }

    /// <summary>
    /// Applies an <see cref="InstallPlan"/>: preflight checks, downloads (resumable, parallel, verified), staging inside each module
    /// folder, journaled rename swap with rollback, backup of replaced files, Mark-of-the-Web removal.
    /// </summary>
    public sealed class InstallEngine
    {
        private readonly IFileSystem _fs;
        private readonly UpdaterPaths _paths;
        private readonly ObjectStore _store;
        private readonly IReleaseSource _source;
        private readonly QueueSlotClient _queue;
        private readonly HashCache _hashes;
        private readonly IProcessProbe _processes;
        private readonly ILockingProcessFinder _lockFinder;
        private readonly IUpdaterLog _log;
        private readonly EngineOptions _opt;

        public InstallEngine(IFileSystem fs, UpdaterPaths paths, ObjectStore store, IReleaseSource source, QueueSlotClient queue, HashCache hashes,
            IProcessProbe processes, ILockingProcessFinder lockFinder, IUpdaterLog log, EngineOptions options = null)
        {
            _fs = fs;
            _paths = paths;
            _store = store;
            _source = source;
            _queue = queue;
            _hashes = hashes;
            _processes = processes ?? new SystemProcessProbe();
            _lockFinder = lockFinder ?? new NoLockingProcessFinder();
            _log = log;
            _opt = options ?? new EngineOptions();
        }

        /// <summary>Test hook: called before each rename of the swap (index, op). Throwing simulates a crash or a lock.</summary>
        internal Action<int, string> BeforeSwapStep { get; set; }

        /// <summary>Test hook: exceptions for which the in-process rollback is skipped (simulates a power cut).</summary>
        internal Func<Exception, bool> SimulatesCrash { get; set; }

        // ---------------------------------------------------------------- preflight

        /// <summary>Adds the checks the Plan screen shows (also re-run right before applying).</summary>
        public void Preflight(InstallPlan plan, bool forApply)
        {
            var g = plan.Game;
            var verMsg = GameVersions.Check(g.GameVersion, plan.Manifest.GameMin(), plan.Manifest.GameMax());
            if (verMsg != null) plan.Issues.Add(new PlanIssue(IssueSeverity.Warning, verMsg));

            foreach (var d in DuplicateModuleScanner.Scan(_fs, g, plan.Manifest))
                plan.Issues.Add(new PlanIssue(IssueSeverity.Warning, $"Another copy of {d.Id} exists: {d.Path} ({d.Where}). In the launcher, make sure only ONE {d.Id} is ticked, or remove/unsubscribe the other copy, otherwise the game may load the wrong version."));

            if (!plan.HasChanges) return;

            var running = _processes.RunningGameProcesses();
            if (running.Count > 0)
                plan.Issues.Add(new PlanIssue(forApply ? IssueSeverity.Blocker : IssueSeverity.Warning,
                    "Bannerlord or its launcher is running (" + string.Join(", ", running.Select(r => r.Name)) + "). Close it before updating: Windows does not let anyone replace files the game is using."));

            foreach (var m in plan.Modules.Where(x => x.HasChanges))
            {
                var probeDir = m.IsNewFolder ? g.ModulesPath : m.TargetFolder;
                var probe = _fs.ProbeWritable(probeDir);
                if (!probe.Writable)
                {
                    plan.Issues.Add(new PlanIssue(IssueSeverity.Blocker,
                        $"This app cannot write in {probeDir}. It never asks for administrator rights. " +
                        (g.IsProtectedLocation
                            ? "Your game is in a protected Xbox/Windows folder: in the Xbox app, open Bannerlord > Manage > Files and enable \"Advanced management features\", or move the game to another folder. "
                            : "Check that the folder is not read-only, that Windows Security \"Controlled folder access\" allows LotRMPUpdater, or move the game to a folder you own (e.g. D:\\Games). ") +
                        "Details: " + probe.Detail));
                    break;
                }
            }

            CheckSpace(plan);
        }

        private void CheckSpace(InstallPlan plan)
        {
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, long bytes)
            {
                if (bytes <= 0) return;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                need[root] = (need.TryGetValue(root, out var v) ? v : 0) + bytes;
            }
            Add(_paths.ObjectCache, plan.DownloadBytes);
            Add(plan.Game.ModulesPath, plan.StagingBytes);
            if (!_fs.SameVolume(plan.Game.ModulesPath, plan.BackupPath)) Add(plan.BackupPath, plan.BackupBytes);
            foreach (var kv in need)
            {
                long free;
                try { free = _fs.GetAvailableFreeSpace(kv.Key); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                long required = kv.Value + _opt.SafetyMarginBytes;
                if (free < required)
                    plan.Issues.Add(new PlanIssue(IssueSeverity.Blocker,
                        $"Not enough free space on {kv.Key}: the update needs {Units.Bytes(required)} (download, temporary copy and a safety margin), {Units.Bytes(free)} is free. Free {Units.Bytes(required - free)} and try again."));
            }
        }

        // ---------------------------------------------------------------- download

        /// <summary>Downloads every missing object into the cache. Used by Apply and by the background pre-download.</summary>
        public async Task DownloadAsync(InstallPlan plan, string installId, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            if (plan.Downloads.Count == 0) return;
            string slot = null;
            if (plan.NeedsCoreSlot && _queue != null)
            {
                progress?.Report(new EngineProgress { Phase = EnginePhase.Queue, Message = "Asking the server for a download slot (Core)…" });
                slot = await _queue.AcquireAsync(installId, (pos, wait) =>
                    progress?.Report(new EngineProgress { Phase = EnginePhase.Queue, Message = $"Many players are downloading right now. You are number {pos} in the queue; next try in {wait.TotalSeconds:0} s." }), ct).ConfigureAwait(false);
            }
            try
            {
                // Heartbeat while downloading; a slot that expired anyway is requested again (enforce mode needs a live one).
                Task heartbeat = Task.CompletedTask;
                var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (slot != null)
                    heartbeat = Task.Run(async () =>
                    {
                        while (!hbCts.IsCancellationRequested)
                        {
                            await Task.Delay(_queue.HeartbeatInterval, hbCts.Token).ConfigureAwait(false);
                            if (!await _queue.HeartbeatAsync(slot, hbCts.Token).ConfigureAwait(false))
                            {
                                _log?.Write(LogLevel.Warn, "The download slot expired; asking for a new one.");
                                slot = await _queue.AcquireAsync(installId, null, hbCts.Token).ConfigureAwait(false);
                                if (slot == null) return;
                            }
                        }
                    });
                try {
                long total = plan.DownloadTotalBytes;
                long done = plan.Downloads.Sum(d => d.AlreadyHave);
                var sw = Stopwatch.StartNew();
                long lastReport = 0;
                var speed = new SpeedMeter();
                _log?.Write(LogLevel.Info, $"Downloading {plan.Downloads.Count} objects, {Units.Bytes(plan.DownloadBytes)} ({_opt.Connections} connections).");
                void OnBytes(long n)
                {
                    long now = Interlocked.Add(ref done, n);
                    speed.Add(n);
                    long ms = sw.ElapsedMilliseconds;
                    if (ms - Interlocked.Read(ref lastReport) < 200) return;
                    Interlocked.Exchange(ref lastReport, ms);
                    progress?.Report(new EngineProgress { Phase = EnginePhase.Downloading, Message = "Downloading", Done = now, Total = total, BytesPerSecond = speed.BytesPerSecond });
                }

                using (var gate = new SemaphoreSlim(Math.Max(1, Math.Min(4, _opt.Connections))))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var tasks = plan.Downloads.OrderByDescending(d => d.ObjectSize).Select(async d =>
                    {
                        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
                        try
                        {
                            var uri = _source.ObjectUri(plan.Manifest.ReleaseId, d.ObjectName);
                            if (slot != null && d.IsCore) uri = new Uri(uri + "?slot=" + Uri.EscapeDataString(slot));
                            await _store.EnsureAsync(uri, d.ObjectName, d.ObjectSize, OnBytes, linked.Token).ConfigureAwait(false);
                        }
                        catch (Exception) { linked.Cancel(); throw; }
                        finally { gate.Release(); }
                    }).ToList();
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                    catch (Exception)
                    {
                        var real = tasks.Where(t => t.IsFaulted).Select(t => t.Exception.GetBaseException()).FirstOrDefault(e => !(e is OperationCanceledException));
                        if (real != null && !ct.IsCancellationRequested) throw real;
                        throw;
                    }
                }
                progress?.Report(new EngineProgress { Phase = EnginePhase.Downloading, Message = "Download complete", Done = total, Total = total });
                _log?.Write(LogLevel.Info, $"Download complete in {sw.Elapsed.TotalSeconds:0} s.");
                }
                finally
                {
                    hbCts.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception ex) { _log?.Write(LogLevel.Detail, "Heartbeat: " + ex.Message); }
                    hbCts.Dispose();
                }
            }
            finally
            {
                if (slot != null) await _queue.ReleaseAsync(slot).ConfigureAwait(false);
            }
        }

        // ---------------------------------------------------------------- apply

        public async Task<string> ApplyAsync(InstallPlan plan, UpdaterState state, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            RecoverInterrupted(plan.Game.ModulesPath);
            plan.Issues.RemoveAll(i => i.Severity == IssueSeverity.Blocker);
            Preflight(plan, forApply: true);
            var blocker = plan.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Blocker);
            if (blocker != null) throw new InstallException(blocker.Text);

            _log?.Write(LogLevel.Info, $"Installing release {plan.Manifest.ReleaseId} into {plan.Game.ModulesPath}");
            try
            {
                await DownloadAsync(plan, state.InstallId, progress, ct).ConfigureAwait(false);
            }
            catch (HttpFetchException ex) { throw new InstallException(ex.Message + " Nothing was changed in your game folder.", ex); }
            catch (CorruptObjectException ex) { throw new InstallException(ex.Message, ex); }

            var changed = plan.Modules.Where(m => m.HasChanges).ToList();
            var journals = new List<SwapJournal>();
            try
            {
                // 1. staging (game folder untouched apart from .lotr-staging)
                long total = plan.StagingBytes, done = 0;
                foreach (var m in changed)
                {
                    var j = new SwapJournal { ModuleFolder = m.TargetFolder, ReleaseId = plan.Manifest.ReleaseId, BackupDir = Path.Combine(plan.BackupPath, m.Module.Folder) };
                    journals.Add(j);
                    var staging = SwapJournal.StagingDir(m.TargetFolder);
                    if (_fs.DirectoryExists(staging)) _fs.DeleteDirectory(staging, true);
                    foreach (var a in m.Actions)
                    {
                        ct.ThrowIfCancellationRequested();
                        j.Ops.Add((a.Kind, a.RelPath));
                        if (a.Kind == FileActionKind.Remove) continue;
                        progress?.Report(new EngineProgress { Phase = EnginePhase.Preparing, Message = "Preparing " + a.RelPath, Done = done, Total = total });
                        var dst = j.NewPath(a.RelPath);
                        _fs.CreateDirectory(Path.GetDirectoryName(dst));
                        BuildFile(a, dst, ct, n => done += n);
                    }
                }

                // 2. swap (renames only; journal first)
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EngineProgress { Phase = EnginePhase.Swapping, Message = "Replacing files…", Done = 1, Total = 1 });
                int step = 0;
                foreach (var j in journals)
                {
                    j.Save(_fs);
                    foreach (var (kind, path) in j.Ops)
                    {
                        BeforeSwapStep?.Invoke(step++, path);
                        SwapOne(j, kind, path);
                    }
                }
            }
            catch (Exception ex) when (SimulatesCrash == null || !SimulatesCrash(ex))
            {
                bool cancelled = ex is OperationCanceledException;
                _log?.Write(cancelled ? LogLevel.Info : LogLevel.Error, (cancelled ? "Cancelled" : "Install failed: " + ex.Message) + " — rolling back.");
                var rollbackErrors = new List<string>();
                foreach (var j in Enumerable.Reverse(journals))
                {
                    try { Rollback(j); }
                    catch (Exception rex) { rollbackErrors.Add(j.ModuleFolder + ": " + rex.Message); }
                }
                if (rollbackErrors.Count > 0)
                    throw new InstallException("The update failed AND some files could not be put back (" + string.Join("; ", rollbackErrors) + "). Close the game, then open this app again: it finishes the rollback at start-up.", ex);
                if (cancelled) throw;
                throw new InstallException(Explain(ex) + " Your previous files were put back; nothing changed.", ex) { RolledBack = true };
            }

            // 3. commit: from here the new release is in place; failures only leave extra files behind.
            progress?.Report(new EngineProgress { Phase = EnginePhase.Finishing, Message = "Saving a backup of the replaced files…", Done = 1, Total = 1 });
            foreach (var j in journals)
            {
                j.State = SwapJournal.Committed;
                j.Save(_fs);
                FinishCommit(j);
            }
            int motw = 0;
            foreach (var m in plan.Modules)
                foreach (var f in m.Module.Files)
                {
                    var p = InstallPlanner.ToLocal(m.TargetFolder, f.Path);
                    try { if (_fs.FileExists(p) && _fs.ClearMarkOfTheWeb(p)) motw++; }
                    catch (IOException ex) { _log?.Write(LogLevel.Warn, ex.Message); }
                    if (f.Chunked) _hashes.RememberChunkIndex(f.Sha256, f.ChunkIndex);
                }
            foreach (var m in changed)
                foreach (var a in m.Actions.Where(x => x.Kind != FileActionKind.Remove))
                    _hashes.Remember(InstallPlanner.ToLocal(m.TargetFolder, a.RelPath), a.Entry.Sha256);
            _hashes.Save();
            if (motw > 0) _log?.Write(LogLevel.Info, $"Removed the \"downloaded from the internet\" mark from {motw} files.");

            if (changed.Count > 0 && state.InstalledReleaseId != plan.Manifest.ReleaseId)
            {
                state.PreviousReleaseId = state.InstalledReleaseId;
            }
            state.InstalledReleaseId = plan.Manifest.ReleaseId;
            state.InstalledAt = DateTime.UtcNow.ToString("o");
            if (state.PendingReleaseId == plan.Manifest.ReleaseId) { state.PendingReleaseId = null; state.PendingDownloaded = false; }
            PruneBackups(plan.BackupPath);
            PurgeCache(plan);
            progress?.Report(new EngineProgress { Phase = EnginePhase.Done, Message = "Done", Done = 1, Total = 1 });
            _log?.Write(LogLevel.Info, $"Release {plan.Manifest.ReleaseId} installed. Backup of replaced files: {plan.BackupPath}");
            return plan.BackupPath;
        }

        private void BuildFile(PlanAction a, string dst, CancellationToken ct, Action<long> progress)
        {
            var f = a.Entry;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                using (var o = _fs.OpenWrite(dst, false))
                using (var hashing = new System.Security.Cryptography.CryptoStream(o, sha, System.Security.Cryptography.CryptoStreamMode.Write))
                {
                    switch (a.Source)
                    {
                        case FileAction.CopyLocal:
                            // Same content already on disk under another name (moved/duplicated file): copy, hash checked below.
                            using (var src = _fs.OpenRead(a.LocalSource))
                            {
                                var buf = new byte[1 << 20];
                                int n;
                                while ((n = src.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); hashing.Write(buf, 0, n); progress(n); }
                            }
                            break;
                        case FileAction.Rebuild:
                            // Chunked: concatenate chunks in order, local ones re-read and checked, missing ones decoded from the cache.
                            foreach (var (c, lc) in a.Chunks)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (lc != null)
                                {
                                    var buf = new byte[lc.Size];
                                    using (var s = _fs.OpenRead(lc.FullPath))
                                    {
                                        s.Seek(lc.Offset, SeekOrigin.Begin);
                                        int read = 0;
                                        while (read < buf.Length) { int n = s.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
                                    }
                                    if (ObjectCodec.Sha256Hex(buf, 0, buf.Length) != c.Sha256)
                                        throw new CorruptObjectException("A local file changed while the update was being prepared (" + Path.GetFileName(lc.FullPath) + "). Run the update again.");
                                    hashing.Write(buf, 0, buf.Length);
                                    progress(buf.Length);
                                }
                                else _store.DecodeVerified(c.Object, hashing, progress, ct, c.Size);
                            }
                            break;
                        default:
                            _store.DecodeVerified(f.Object, hashing, progress, ct, f.Size);
                            break;
                    }
                    hashing.FlushFinalBlock();
                }
                if (ObjectCodec.Hex(sha.Hash) != f.Sha256)
                    throw new CorruptObjectException("The prepared file " + f.Path + " does not match the release (SHA-256). Nothing was changed.");
            }
        }

        private void SwapOne(SwapJournal j, FileActionKind kind, string rel)
        {
            var target = j.TargetPath(rel);
            switch (kind)
            {
                case FileActionKind.Replace:
                    MoveCreating(target, j.OldPath(rel));
                    MoveCreating(j.NewPath(rel), target);
                    break;
                case FileActionKind.Add:
                    if (_fs.FileExists(target)) MoveCreating(target, j.OldPath(rel)); // appeared since planning
                    MoveCreating(j.NewPath(rel), target);
                    break;
                case FileActionKind.Remove:
                    if (_fs.FileExists(target)) MoveCreating(target, j.OldPath(rel));
                    break;
            }
        }

        private void MoveCreating(string from, string to)
        {
            var dir = Path.GetDirectoryName(to);
            if (!_fs.DirectoryExists(dir)) _fs.CreateDirectory(dir);
            try
            {
                _fs.MoveFile(from, to);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                var who = _lockFinder.WhoLocks(new[] { from });
                var names = who.Count > 0 ? string.Join(", ", who.Select(w => w.ToString())) : "another program (often the game, its launcher, an antivirus scan or a file explorer preview)";
                throw new FileLockedException($"{Path.GetFileName(from)} is in use by {names}. Close it and try again.", from, who, ex);
            }
        }

        private static bool IsSharingViolation(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33 || code == 5;
        }

        /// <summary>Undoes a (possibly partial) swap. Safe to run several times.</summary>
        public void Rollback(SwapJournal j)
        {
            foreach (var (kind, rel) in Enumerable.Reverse(j.Ops))
            {
                var target = j.TargetPath(rel);
                var old = j.OldPath(rel);
                var neu = j.NewPath(rel);
                switch (kind)
                {
                    case FileActionKind.Replace:
                    case FileActionKind.Add:
                        if (_fs.FileExists(target) && !_fs.FileExists(neu) && (kind == FileActionKind.Add || _fs.FileExists(old)))
                            MoveCreating(target, neu);
                        if (_fs.FileExists(old) && !_fs.FileExists(target)) MoveCreating(old, target);
                        break;
                    case FileActionKind.Remove:
                        if (_fs.FileExists(old) && !_fs.FileExists(target)) MoveCreating(old, target);
                        break;
                }
            }
            var staging = SwapJournal.StagingDir(j.ModuleFolder);
            _fs.DeleteDirectory(staging, true);
            RemoveEmptyDirs(j.ModuleFolder);
            _log?.Write(LogLevel.Info, "Rolled back " + j.ModuleFolder);
        }

        private void FinishCommit(SwapJournal j)
        {
            var oldRoot = Path.Combine(SwapJournal.StagingDir(j.ModuleFolder), "old");
            try
            {
                if (_fs.DirectoryExists(oldRoot))
                {
                    var prefix = oldRoot.TrimEnd('\\') + "\\";
                    foreach (var f in _fs.EnumerateFiles(oldRoot, true).ToList())
                    {
                        var dst = Path.Combine(j.BackupDir, f.Substring(prefix.Length));
                        _fs.CreateDirectory(Path.GetDirectoryName(dst));
                        if (_fs.FileExists(dst)) _fs.DeleteFile(dst);
                        if (_fs.SameVolume(f, dst)) _fs.MoveFile(f, dst);
                        else { _fs.CopyFile(f, dst, true); _fs.DeleteFile(f); }
                    }
                }
                _fs.DeleteDirectory(SwapJournal.StagingDir(j.ModuleFolder), true);
                RemoveEmptyDirs(j.ModuleFolder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log?.Write(LogLevel.Warn, "The update is installed, but the backup copy could not be completed: " + ex.Message + " (the old files stay in " + oldRoot + ")");
            }
        }

        private void RemoveEmptyDirs(string root)
        {
            try
            {
                foreach (var d in _fs.EnumerateDirectories(root).ToList())
                {
                    RemoveEmptyDirs(d);
                    if (!_fs.EnumerateFiles(d, true).Any() && !_fs.EnumerateDirectories(d).Any()) _fs.DeleteDirectory(d, false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Finishes or undoes a swap interrupted by a crash (called at start-up and before every install).</summary>
        public int RecoverInterrupted(string modulesPath)
        {
            int n = 0;
            foreach (var dir in SafeDirs(modulesPath))
            {
                SwapJournal j;
                try { j = SwapJournal.Load(_fs, dir); }
                catch (Exception ex) { _log?.Write(LogLevel.Warn, "Unreadable swap journal in " + dir + ": " + ex.Message); continue; }
                if (j == null)
                {
                    // Staging without journal = interrupted before the first rename: just discard it.
                    var st = SwapJournal.StagingDir(dir);
                    if (_fs.DirectoryExists(st)) { _fs.DeleteDirectory(st, true); n++; }
                    continue;
                }
                n++;
                if (j.State == SwapJournal.Committed) { _log?.Write(LogLevel.Info, "Finishing an interrupted update in " + dir); FinishCommit(j); }
                else { _log?.Write(LogLevel.Warn, "An update was interrupted in " + dir + "; putting the previous files back."); Rollback(j); }
            }
            return n;
        }

        private IEnumerable<string> SafeDirs(string d)
        {
            try { return _fs.EnumerateDirectories(d).ToList(); } catch (IOException) { return new List<string>(); } catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        private void PruneBackups(string keep)
        {
            try
            {
                foreach (var d in _fs.EnumerateDirectories(_paths.BackupRoot).ToList())
                    if (!string.Equals(Path.GetFullPath(d).TrimEnd('\\'), Path.GetFullPath(keep).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        _fs.DeleteDirectory(d, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _log?.Write(LogLevel.Detail, "Old backup not removed: " + ex.Message); }
        }

        /// <summary>Installed objects are no longer needed in the cache (chunk lists are kept for the next update's chunk reuse).</summary>
        private void PurgeCache(InstallPlan plan)
        {
            var keep = new HashSet<string>(_hashes.KnownIndexObjects.Concat(plan.ChunkIndexObjects), StringComparer.Ordinal);
            foreach (var m in plan.Modules)
                foreach (var a in m.Actions.Where(x => x.Kind != FileActionKind.Remove))
                {
                    if (a.Source == FileAction.Rebuild) { foreach (var (c, l) in a.Chunks) if (l == null && !keep.Contains(c.Object)) _store.Delete(c.Object); }
                    else if (a.Source == FileAction.Download && !keep.Contains(a.Entry.Object)) _store.Delete(a.Entry.Object);
                }
        }

        private static string Explain(Exception ex)
        {
            if (ex is FileLockedException) return ex.Message;
            if (ex is CorruptObjectException) return ex.Message;
            if (ex is UnauthorizedAccessException) return "Windows refused access to a file (" + ex.Message + ").";
            if (ex is IOException io && ((io.HResult & 0xFFFF) == 112 || (io.HResult & 0xFFFF) == 39)) return "The disk became full during the update.";
            return ex.Message;
        }

        // ---------------------------------------------------------------- verify

        /// <summary>"Verify my install": re-hashes every file (no cache), clears Mark-of-the-Web, lists duplicates; returns a repair plan.</summary>
        public async Task<VerifyReport> VerifyAsync(InstallPlanner planner, ReleaseManifest manifest, byte[] manifestBytes, GameTarget game, string installedReleaseId, IProgress<string> status, CancellationToken ct)
        {
            var plan = await planner.PlanAsync(manifest, manifestBytes, game, manifest, installedReleaseId, forceHash: true, status, ct).ConfigureAwait(false);
            var r = new VerifyReport { RepairPlan = plan };
            foreach (var m in plan.Modules)
            {
                var check = planner.LastChecks[m.Module.Id];
                r.Checked += check.Files.Count;
                foreach (var f in check.Files)
                {
                    var name = m.Module.Folder + "/" + f.Path;
                    if (f.State == InstalledFileState.Ok) r.Ok++;
                    else if (f.State == InstalledFileState.Missing) r.Missing.Add(name);
                    else r.Modified.Add(name);
                }
                r.Extra.AddRange(m.Extra.Select(x => m.Module.Folder + "/" + x));
                foreach (var f in m.Module.Files)
                {
                    var p = InstallPlanner.ToLocal(m.TargetFolder, f.Path);
                    try { if (_fs.FileExists(p) && _fs.ClearMarkOfTheWeb(p)) r.MarkOfTheWebCleared++; }
                    catch (IOException) { }
                }
            }
            r.Duplicates.AddRange(DuplicateModuleScanner.Scan(_fs, game, manifest));
            _hashes.Save();
            _log?.Write(LogLevel.Info, $"Verify: {r.Ok}/{r.Checked} files OK, {r.Missing.Count} missing, {r.Modified.Count} different, {r.Extra.Count} extra, {r.MarkOfTheWebCleared} internet marks removed, {r.Duplicates.Count} duplicate copies.");
            return r;
        }

        /// <summary>Deletes the module folders of a release (uninstall with "remove mod files").</summary>
        public void RemoveModules(ReleaseManifest manifest, string modulesPath)
        {
            foreach (var m in manifest.Modules)
            {
                var folder = Path.Combine(modulesPath, m.Folder);
                if (!_fs.DirectoryExists(folder)) continue;
                var id = DuplicateModuleScanner.ReadModuleId(_fs, folder);
                if (id != null && !string.Equals(id, m.Id, StringComparison.OrdinalIgnoreCase)) { _log?.Write(LogLevel.Warn, "Not removing " + folder + ": it holds another module (" + id + ")."); continue; }
                _fs.DeleteDirectory(folder, true);
                _log?.Write(LogLevel.Info, "Removed " + folder);
            }
        }
    }

    public sealed class FileLockedException : IOException
    {
        public FileLockedException(string message, string path, IReadOnlyList<RunningProcessInfo> lockers, Exception inner) : base(message, inner)
        {
            Path = path;
            Lockers = lockers;
        }
        public string Path { get; }
        public IReadOnlyList<RunningProcessInfo> Lockers { get; }
    }

    internal sealed class SpeedMeter
    {
        private readonly Queue<(long Ticks, long Bytes)> _q = new Queue<(long, long)>();
        private readonly object _gate = new object();
        private long _sum;

        public void Add(long n)
        {
            lock (_gate)
            {
                var now = Stopwatch.GetTimestamp();
                _q.Enqueue((now, n));
                _sum += n;
                while (_q.Count > 0 && now - _q.Peek().Ticks > Stopwatch.Frequency * 5) _sum -= _q.Dequeue().Bytes;
            }
        }

        public double BytesPerSecond
        {
            get
            {
                lock (_gate)
                {
                    if (_q.Count < 2) return 0;
                    double secs = (double)(Stopwatch.GetTimestamp() - _q.Peek().Ticks) / Stopwatch.Frequency;
                    return secs <= 0.05 ? 0 : Math.Max(0, _sum / secs);
                }
            }
        }
    }

    public static class Units
    {
        public static string Bytes(long b)
        {
            double v = b;
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (Math.Abs(v) >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString(v >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture)) + " " + u[i];
        }
    }
}
