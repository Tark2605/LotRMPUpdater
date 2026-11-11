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

    /// <summary>An install step failed; nothing was changed (or everything was rolled back). Message is for the player (short,
    /// calm, English); <see cref="Detail"/> keeps the technical cause for the log and the "Details" expander.</summary>
    public sealed class InstallException : Exception
    {
        public InstallException(string message, Exception inner = null) : base(message, inner)
        {
            Kind = inner == null ? ProblemKind.Unknown : PlayerErrors.Classify(inner);
            Detail = PlayerErrors.Technical(inner);
        }

        public bool RolledBack { get; set; }
        public ProblemKind Kind { get; set; }
        /// <summary>Technical cause (exception types and raw, possibly localized, messages). Never the main text.</summary>
        public string Detail { get; set; }
        /// <summary>The failure came from a local file the plan reused: a fresh plan (download instead) is expected to work.</summary>
        public bool CanReplan => RolledBack && Kind == ProblemKind.LocalSourceChanged;
    }

    public sealed class EngineOptions
    {
        /// <summary>Highest number of parallel HTTP connections (also the per-host limit set in <see cref="HttpFetcher.CreateClient"/>).</summary>
        public const int MaxConnections = 8;
        /// <summary>Parallel HTTP connections (default 6, capped at <see cref="MaxConnections"/>): the Core is ~38,000 small objects,
        /// so latency, not bandwidth, limits a single connection.</summary>
        public int Connections { get; set; } = 6;
        /// <summary>Free space kept on each drive on top of what the update needs.</summary>
        public long SafetyMarginBytes { get; set; } = 256L << 20;
        /// <summary>Waits between tries when a file is locked (antivirus scan, Explorer preview, Steam): a few short retries,
        /// then the player is told which program holds it.</summary>
        public int[] LockRetryDelaysMs { get; set; } = { 250, 750, 1500, 3000 };
        /// <summary>A local source that fails while preparing (Steam removed or changed it) is downloaded instead, on the spot.
        /// Tests turn it off to exercise the second safety net (automatic re-plan in <see cref="UpdaterService.InstallAsync"/>).</summary>
        public bool DownloadWhenLocalSourceFails { get; set; } = true;
        /// <summary>Injectable wait (tests).</summary>
        public Action<int> Sleep { get; set; } = ms => Thread.Sleep(ms);
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

        public EngineOptions Options => _opt;

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
                // A Workshop subscription next to our Modules copy is harmless (the game prefers Modules): informative only.
                plan.Issues.Add(new PlanIssue(d.IsWorkshop ? IssueSeverity.Info : IssueSeverity.Warning, d.Advice)
                {
                    LinkUrl = d.WorkshopPageUrl,
                    LinkText = d.WorkshopPageUrl != null ? "Open the Workshop page" : null,
                });

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
                    // The probe detail carries a Windows message in the player's language: log only.
                    _log?.Write(LogLevel.Warn, "Write test failed: " + probe.Detail);
                    plan.Issues.Add(new PlanIssue(IssueSeverity.Blocker,
                        $"Windows does not let this app write in {probeDir} (it never asks for administrator rights). " +
                        (g.IsProtectedLocation
                            ? "Your game is in a protected Xbox/Windows folder: in the Xbox app, open Bannerlord > Manage > Files and enable \"Advanced management features\", or move the game to another folder."
                            : "Check that the folder is not read-only and that Windows Security \"Controlled folder access\" allows LotRMPUpdater, or move the game to a folder you own (for example D:\\Games).")));
                    break;
                }
            }

            CheckSpace(plan);
        }

        private void CheckSpace(InstallPlan plan)
        {
            foreach (var problem in SpaceProblems(plan, downloadOnly: false))
                plan.Issues.Add(new PlanIssue(IssueSeverity.Blocker, problem));
        }

        /// <summary>One sentence per drive without enough free space (empty when there is enough). <paramref name="downloadOnly"/>:
        /// only the object cache is filled (background pre-download), the game folder and the backup are not touched. Files copied
        /// locally (moved files, Steam Workshop copy) need room in Modules (staging) but nothing in the cache. A drive whose free
        /// space cannot be read is logged and not blocked.</summary>
        public List<string> SpaceProblems(InstallPlan plan, bool downloadOnly)
        {
            var problems = new List<string>();
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Add(string path, long bytes)
            {
                if (bytes <= 0) return;
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                need[root] = (need.TryGetValue(root, out var v) ? v : 0) + bytes;
            }
            Add(_paths.ObjectCache, plan.DownloadBytes);
            if (!downloadOnly)
            {
                Add(plan.Game.ModulesPath, plan.StagingBytes);
                if (!_fs.SameVolume(plan.Game.ModulesPath, plan.BackupPath)) Add(plan.BackupPath, plan.BackupBytes);
            }
            foreach (var kv in need)
            {
                long free;
                try { free = _fs.GetAvailableFreeSpace(kv.Key); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _log?.Write(LogLevel.Warn, $"Could not read the free space of {kv.Key} ({ex.Message}); the space check is skipped for this drive.");
                    continue;
                }
                long required = kv.Value + _opt.SafetyMarginBytes;
                if (free < required)
                    problems.Add(downloadOnly
                        ? $"Not enough free space on {kv.Key}: the download needs {Units.Bytes(required)} (download and a safety margin), {Units.Bytes(free)} is free. Free {Units.Bytes(required - free)} and try again."
                        : $"Not enough free space on {kv.Key}: the update needs {Units.Bytes(required)} (download, temporary copy and a safety margin), {Units.Bytes(free)} is free. Free {Units.Bytes(required - free)} and try again.");
            }
            return problems;
        }

        // ---------------------------------------------------------------- download

        /// <summary>Downloads every missing object into the cache. Used by Apply and by the background pre-download.</summary>
        public Task DownloadAsync(InstallPlan plan, string installId, IProgress<EngineProgress> progress, CancellationToken ct) =>
            DownloadItemsAsync(plan, plan.Downloads, installId, progress, ct);

        private async Task DownloadItemsAsync(InstallPlan plan, IReadOnlyList<DownloadItem> items, string installId, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            if (items.Count == 0) return;
            string slot = null;
            if (items.Any(d => d.IsCore) && _queue != null)
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
                long total = items.Sum(d => d.ObjectSize);
                long done = items.Sum(d => d.AlreadyHave);
                var sw = Stopwatch.StartNew();
                long lastReport = 0;
                var speed = new SpeedMeter();
                _log?.Write(LogLevel.Info, $"Downloading {items.Count} objects, {Units.Bytes(total - done)} ({Math.Max(1, Math.Min(EngineOptions.MaxConnections, _opt.Connections))} connections).");
                void OnBytes(long n)
                {
                    long now = Interlocked.Add(ref done, n);
                    speed.Add(n);
                    long ms = sw.ElapsedMilliseconds;
                    if (ms - Interlocked.Read(ref lastReport) < 200) return;
                    Interlocked.Exchange(ref lastReport, ms);
                    progress?.Report(new EngineProgress { Phase = EnginePhase.Downloading, Message = "Downloading", Done = now, Total = total, BytesPerSecond = speed.BytesPerSecond });
                }

                using (var gate = new SemaphoreSlim(Math.Max(1, Math.Min(EngineOptions.MaxConnections, _opt.Connections))))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var tasks = items.OrderByDescending(d => d.ObjectSize).Select(async d =>
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
            // The plan may be hours old (built at check time): Steam may have removed or updated the Workshop copy since
            // (unsubscribe, item update, "verify integrity"), the player may have changed files in Modules. Re-check every
            // local source and every file the plan keeps; anything gone or changed is downloaded instead.
            try { await RevalidateAsync(plan, progress, ct).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                var kind = PlayerErrors.Classify(ex);
                throw new InstallException(PlayerErrors.InstallText(kind, ex, false), ex);
            }
            plan.Issues.RemoveAll(i => i.Severity == IssueSeverity.Blocker);
            Preflight(plan, forApply: true);
            var blocker = plan.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Blocker);
            if (blocker != null) throw new InstallException(blocker.Text) { Kind = ProblemKind.Blocked };

            _log?.Write(LogLevel.Info, $"Installing release {plan.Manifest.ReleaseId} into {plan.Game.ModulesPath}");
            try
            {
                await DownloadAsync(plan, state.InstallId, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException) || !ct.IsCancellationRequested)
            {
                // The game folder is not touched before this point.
                _log?.Write(LogLevel.Error, "Download failed: " + PlayerErrors.Technical(ex));
                var kind = PlayerErrors.Classify(ex);
                if (kind == ProblemKind.Unknown && ex is OperationCanceledException) kind = ProblemKind.Network; // HttpClient timeout
                throw new InstallException(PlayerErrors.InstallText(kind, ex, false), ex) { Kind = kind };
            }

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
                        long before = done;
                        try { BuildFile(a, dst, ct, n => done += n); }
                        catch (LocalSourceException lex) when (_opt.DownloadWhenLocalSourceFails)
                        {
                            // Steam removed/updated the Workshop copy, an antivirus holds the file, or the player changed it:
                            // download this file instead (the game folder is still untouched).
                            _log?.Write(LogLevel.Warn, $"{m.Module.Folder}/{a.RelPath}: the local copy it was built from changed or disappeared ({lex.Message}{(lex.InnerException != null ? "; " + lex.InnerException.Message : "")}). Downloading it instead.");
                            done = before;
                            var extra = await SwitchToDownloadAsync(plan, m, a, ct).ConfigureAwait(false);
                            await DownloadItemsAsync(plan, extra, state.InstallId, null, ct).ConfigureAwait(false);
                            BuildFile(a, dst, ct, n => done += n);
                        }
                    }
                }

                // 2. swap (renames only; journal first)
                ct.ThrowIfCancellationRequested();
                progress?.Report(new EngineProgress { Phase = EnginePhase.Swapping, Message = "Replacing files…", Done = 1, Total = 1 });
                int step = 0;
                foreach (var j in journals)
                {
                    // Files the player deleted since the plan: a Replace becomes an Add (nothing to move out of the way).
                    for (int k = 0; k < j.Ops.Count; k++)
                        if (j.Ops[k].Kind == FileActionKind.Replace && !_fs.FileExists(j.TargetPath(j.Ops[k].Path)))
                            j.Ops[k] = (FileActionKind.Add, j.Ops[k].Path);
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
                _log?.Write(cancelled ? LogLevel.Info : LogLevel.Error, (cancelled ? "Cancelled" : "Install failed: " + PlayerErrors.Technical(ex)) + " — rolling back.");
                var rollbackErrors = new List<string>();
                foreach (var j in Enumerable.Reverse(journals))
                {
                    try { Rollback(j); }
                    catch (Exception rex) { rollbackErrors.Add(j.ModuleFolder + ": " + PlayerErrors.Technical(rex)); }
                }
                if (rollbackErrors.Count > 0)
                {
                    _log?.Write(LogLevel.Error, "Rollback incomplete: " + string.Join("; ", rollbackErrors));
                    throw new InstallException("The update stopped and a few files could not be put back yet (another program is probably holding them). Close Bannerlord and its launcher, then open this app again: it puts them back by itself at start-up.", ex)
                    {
                        Kind = ProblemKind.RollbackIncomplete,
                        Detail = PlayerErrors.Technical(ex) + " | rollback: " + string.Join("; ", rollbackErrors),
                    };
                }
                if (cancelled) throw;
                var kind = PlayerErrors.Classify(ex);
                throw new InstallException(PlayerErrors.InstallText(kind, ex, true), ex) { RolledBack = true, Kind = kind };
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
                            // Same content already on disk under another name (moved/duplicated file, Steam Workshop copy):
                            // copy, hash checked below. Any read problem means "download it instead" (LocalSourceException).
                            try
                            {
                                using (var src = _fs.OpenRead(a.LocalSource))
                                {
                                    var buf = new byte[1 << 20];
                                    int n;
                                    while ((n = src.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); hashing.Write(buf, 0, n); progress(n); }
                                }
                            }
                            catch (Exception ex) when (IsLocalReadProblem(ex)) { throw new LocalSourceException(a.LocalSource, "could not be read", ex); }
                            break;
                        case FileAction.Rebuild:
                            // Chunked: concatenate chunks in order, local ones re-read and checked, missing ones decoded from the cache.
                            foreach (var (c, lc) in a.Chunks)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (lc != null)
                                {
                                    var buf = new byte[lc.Size];
                                    int read = 0;
                                    try
                                    {
                                        using (var s = _fs.OpenRead(lc.FullPath))
                                        {
                                            s.Seek(lc.Offset, SeekOrigin.Begin);
                                            while (read < buf.Length) { int n = s.Read(buf, read, buf.Length - read); if (n == 0) break; read += n; }
                                        }
                                    }
                                    catch (Exception ex) when (IsLocalReadProblem(ex)) { throw new LocalSourceException(lc.FullPath, "could not be read", ex); }
                                    if (read != buf.Length || ObjectCodec.Sha256Hex(buf, 0, buf.Length) != c.Sha256)
                                        throw new LocalSourceException(lc.FullPath, "a reused part no longer matches (the file changed since the check)");
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
                {
                    // A reused local file whose hash was trusted from the cache (same size and date) but whose content changed.
                    if (a.Source == FileAction.CopyLocal) throw new LocalSourceException(a.LocalSource, "content differs from the release (changed since the check)");
                    if (a.Source == FileAction.Rebuild && a.Chunks.Any(x => x.Local != null)) throw new LocalSourceException(a.Chunks.First(x => x.Local != null).Local.FullPath, "rebuilt file differs from the release");
                    throw new CorruptObjectException("The prepared file " + f.Path + " does not match the release (SHA-256). Nothing was changed.");
                }
            }
        }

        private static bool IsLocalReadProblem(Exception ex) =>
            (ex is IOException && !PlayerErrors.IsDiskFull(ex)) || ex is UnauthorizedAccessException;

        // ---------------------------------------------------------------- re-validation (stale plans)

        /// <summary>
        /// Right before applying: every local source of the plan (CopyLocal file, reused chunks) must still exist with the same
        /// size and date as when the plan was made; a CopyLocal source whose date changed is re-hashed (the hash cache keys on
        /// size + date, so a changed file is never trusted). Files the plan keeps must still be there with their size. Anything
        /// that fails is switched to a download, and the downloads/queue/space needs are updated accordingly.
        /// </summary>
        internal async Task<int> RevalidateAsync(InstallPlan plan, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            int switched = 0;
            foreach (var m in plan.Modules)
            {
                var acted = new HashSet<string>(m.Actions.Select(x => x.RelPath), StringComparer.OrdinalIgnoreCase);
                foreach (var a in m.Actions.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    string why = null;
                    if (a.Kind == FileActionKind.Remove) continue;
                    if (a.Source == FileAction.CopyLocal) why = CopySourceProblem(plan, a, ct);
                    else if (a.Source == FileAction.Rebuild)
                    {
                        var bad = a.Chunks.Where(x => x.Local != null).Select(x => x.Local.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                            .Where(p => ChunkSourceProblem(plan, p, a.Chunks.Where(x => x.Local != null && string.Equals(x.Local.FullPath, p, StringComparison.OrdinalIgnoreCase)).Max(x => x.Local.Offset + x.Local.Size)) != null)
                            .ToList();
                        if (bad.Count > 0) why = "reused parts come from a file that changed: " + string.Join(", ", bad);
                    }
                    if (why == null) continue;
                    _log?.Write(LogLevel.Warn, $"{m.Module.Folder}/{a.RelPath}: {why}; it will be downloaded instead.");
                    foreach (var d in await SwitchToDownloadAsync(plan, m, a, ct).ConfigureAwait(false)) AddDownload(plan, d);
                    switched++;
                }
                // Files the plan keeps as they are: the player (or a tool) may have removed or changed them since.
                if (m.IsNewFolder) continue;
                foreach (var f in m.Module.Files)
                {
                    if (acted.Contains(f.Path)) continue;
                    var p = InstallPlanner.ToLocal(m.TargetFolder, f.Path);
                    bool exists = _fs.FileExists(p);
                    long len = -1;
                    if (exists) { try { len = _fs.GetFileLength(p); } catch (Exception ex) when (IsLocalReadProblem(ex)) { } }
                    if (exists && len == f.Size) continue;
                    _log?.Write(LogLevel.Warn, $"{m.Module.Folder}/{f.Path}: {(exists ? "changed" : "removed")} since the check; it will be downloaded.");
                    var a = new PlanAction { Kind = exists ? FileActionKind.Replace : FileActionKind.Add, RelPath = f.Path, Size = f.Size, OldSize = Math.Max(0, len), Entry = f };
                    foreach (var d in await SwitchToDownloadAsync(plan, m, a, ct).ConfigureAwait(false)) AddDownload(plan, d);
                    m.Actions.Add(a);
                    m.UnchangedFiles = Math.Max(0, m.UnchangedFiles - 1);
                    m.UnchangedBytes = Math.Max(0, m.UnchangedBytes - f.Size);
                    switched++;
                }
            }
            if (switched > 0)
            {
                _log?.Write(LogLevel.Info, $"{switched} file(s) changed since the check: they are downloaded instead ({Units.Bytes(plan.DownloadBytes)} to download now).");
                progress?.Report(new EngineProgress { Phase = EnginePhase.Checking, Message = "Some files changed since the check; downloading them instead." });
            }
            return switched;
        }

        private string CopySourceProblem(InstallPlan plan, PlanAction a, CancellationToken ct)
        {
            var p = a.LocalSource;
            try
            {
                if (string.IsNullOrEmpty(p) || !_fs.FileExists(p)) return "its local copy " + p + " is gone";
                long len = _fs.GetFileLength(p);
                if (len != a.Entry.Size) return "its local copy " + p + " changed size";
                if (plan.SourceStamps.TryGetValue(p, out var st) && st.Length == len && st.LastWriteUtc == _fs.GetLastWriteTimeUtc(p)) return null;
                // Date changed (Steam rewrote it?) or unknown: re-hash; the hash cache re-reads it because its key has the date.
                return _hashes.Hash(p, false, ct) == a.Entry.Sha256 ? null : "its local copy " + p + " has different content now";
            }
            catch (Exception ex) when (IsLocalReadProblem(ex)) { return "its local copy " + p + " cannot be read (" + ex.Message + ")"; }
        }

        private string ChunkSourceProblem(InstallPlan plan, string p, long minLength)
        {
            try
            {
                if (!_fs.FileExists(p)) return "gone";
                long len = _fs.GetFileLength(p);
                if (len < minLength) return "shorter";
                if (plan.SourceStamps.TryGetValue(p, out var st) && (st.Length != len || st.LastWriteUtc != _fs.GetLastWriteTimeUtc(p))) return "changed";
                return null;
            }
            catch (Exception ex) when (IsLocalReadProblem(ex)) { return "unreadable"; }
        }

        private static void AddDownload(InstallPlan plan, DownloadItem d)
        {
            var have = plan.Downloads.FirstOrDefault(x => x.ObjectName == d.ObjectName);
            if (have != null) { have.IsCore |= d.IsCore; return; }
            plan.Downloads.Add(d);
        }

        /// <summary>Turns an action into a pure download (whole object, or every chunk of a chunked file) and returns the objects
        /// that are not in the cache yet.</summary>
        private async Task<List<DownloadItem>> SwitchToDownloadAsync(InstallPlan plan, ModulePlan m, PlanAction a, CancellationToken ct)
        {
            var f = a.Entry;
            bool core = m.Module.IsCore();
            var need = new List<DownloadItem>();
            void Need(string obj, long objectSize, long size)
            {
                if (_store.Has(obj) || need.Any(x => x.ObjectName == obj)) return;
                need.Add(new DownloadItem { ObjectName = obj, ObjectSize = objectSize, Size = size, IsCore = core, AlreadyHave = Math.Min(_store.PartialBytes(obj), objectSize) });
            }
            if (a.FromWorkshop) plan.WorkshopReusedBytes = Math.Max(0, plan.WorkshopReusedBytes - (a.Source == FileAction.CopyLocal ? f.Size : 0));
            a.FromWorkshop = false;
            a.LocalSource = null;
            if (!f.Chunked)
            {
                a.Source = FileAction.Download;
                a.Chunks = null;
                Need(f.Object, f.ObjectSize, f.Size);
                return need;
            }
            if (!_store.Has(f.ChunkIndex))
                await _store.EnsureSmallAsync(_source.ObjectUri(plan.Manifest.ReleaseId, f.ChunkIndex), f.ChunkIndex, ct).ConfigureAwait(false);
            var idx = LotR.Releases.Chunking.ChunkIndex.Parse(_store.ReadDecoded(f.ChunkIndex, ct));
            a.Source = FileAction.Rebuild;
            a.Chunks = idx.Chunks.Select(c => (c, (LotR.Releases.Install.LocalChunk)null)).ToList();
            foreach (var c in idx.Chunks) Need(c.Object, c.ObjectSize, c.Size);
            return need;
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
            var delays = _opt.LockRetryDelaysMs ?? new int[0];
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    _fs.MoveFile(from, to);
                    return;
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    // Antivirus scans, Explorer previews and Steam often hold a file for a moment: wait a little and retry.
                    if (attempt < delays.Length)
                    {
                        _log?.Write(LogLevel.Warn, $"{Path.GetFileName(from)} is in use; trying again in {delays[attempt]} ms ({attempt + 1}/{delays.Length}).");
                        _opt.Sleep(delays[attempt]);
                        continue;
                    }
                    var who = _lockFinder.WhoLocks(new[] { from });
                    var names = who.Count > 0 ? string.Join(", ", who.Select(w => w.ToString())) : "an unknown program";
                    _log?.Write(LogLevel.Error, $"{from} is locked by {names}: {ex.Message}");
                    throw new FileLockedException($"{Path.GetFileName(from)} is in use by {names}.", from, who, ex);
                }
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
            // The journal sits in the game folder, which other Windows users may be able to write: never trust its
            // backup folder (only one of ours, under BackupRoot) nor a staging folder that is a link to elsewhere.
            if (!IsUnder(j.BackupDir, _paths.BackupRoot) || IsReparsePoint(SwapJournal.StagingDir(j.ModuleFolder)) || IsReparsePoint(oldRoot))
            {
                _log?.Write(LogLevel.Warn, "Swap journal in " + j.ModuleFolder + " ignored: its backup folder or staging folder is not one this updater made.");
                return;
            }
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

        private static bool IsUnder(string dir, string root)
        {
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(root)) return false;
            try
            {
                var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                var r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                return d.Length > r.Length && d.StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }

        private static bool IsReparsePoint(string dir)
        {
            try { return Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return true; }
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
