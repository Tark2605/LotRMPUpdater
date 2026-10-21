using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Chunking;
using LotR.Releases.Install;
using LotR.Releases.Model;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.Objects;

namespace Lotr.Updater.Core.Planning
{
    /// <summary>
    /// Builds the Plan screen from the shared install-side logic of LotR.Releases: <see cref="InstalledModuleVerifier"/> (state of
    /// each module folder) + <see cref="LocalContent"/> + <see cref="DownloadPlanner"/> (keep / copy locally / download / rebuild
    /// a chunked file from local + missing chunks; deletions = files of the previous release only, extra files are never deleted).
    /// Read-only on the game folder; may download chunk indexes into the cache.
    /// </summary>
    public sealed class InstallPlanner
    {
        private readonly IFileSystem _fs;
        private readonly HashCache _hashes;
        private readonly ObjectStore _store;
        private readonly IReleaseSource _source;
        private readonly UpdaterPaths _paths;
        private readonly IUpdaterLog _log;

        public InstallPlanner(IFileSystem fs, HashCache hashes, ObjectStore store, IReleaseSource source, UpdaterPaths paths, IUpdaterLog log)
        {
            _fs = fs;
            _hashes = hashes;
            _store = store;
            _source = source;
            _paths = paths;
            _log = log;
        }

        public static string ToLocal(string folder, string relPath) => RelativePath.ToFileSystem(folder, relPath);

        /// <summary>Last per-module checks (Verify my install reads them).</summary>
        public Dictionary<string, InstallCheck> LastChecks { get; private set; } = new Dictionary<string, InstallCheck>();

        /// <param name="installedManifest">Manifest of the release installed by this app (for deletions), or null.</param>
        /// <param name="forceHash">true for "Verify my install": re-hash every file instead of trusting the hash cache.</param>
        public async Task<InstallPlan> PlanAsync(ReleaseManifest manifest, byte[] manifestBytes, GameTarget game, ReleaseManifest installedManifest, string installedReleaseId,
            bool forceHash, IProgress<string> status, CancellationToken ct)
        {
            var plan = new InstallPlan { Manifest = manifest, ManifestBytes = manifestBytes, Game = game, InstalledReleaseId = installedReleaseId };
            plan.BackupPath = _paths.BackupDir(installedReleaseId ?? ("before-" + manifest.ReleaseId));
            var checks = new Dictionary<string, InstallCheck>(StringComparer.Ordinal);
            var local = new LocalContent();

            // Chunk indexes of this release: small, fetched once, kept in the cache (CONTRACTS §8: keep the installed release's indexes).
            foreach (var f in manifest.AllFiles.Where(x => x.Chunked))
            {
                plan.ChunkIndexObjects.Add(f.ChunkIndex);
                if (_store.Has(f.ChunkIndex)) continue;
                status?.Report("Reading the chunk list of " + f.Path);
                await _store.EnsureSmallAsync(_source.ObjectUri(manifest.ReleaseId, f.ChunkIndex), f.ChunkIndex, ct).ConfigureAwait(false);
            }

            foreach (var m in manifest.Modules)
            {
                ct.ThrowIfCancellationRequested();
                var folder = Path.Combine(game.ModulesPath, m.Folder);
                var mp = new ModulePlan { Module = m, TargetFolder = folder, IsNewFolder = !_fs.DirectoryExists(folder) };
                plan.Modules.Add(mp);
                if (!mp.IsNewFolder) mp.LocalId = DuplicateModuleScanner.ReadModuleId(_fs, folder);
                if (mp.LocalId != null && !string.Equals(mp.LocalId, m.Id, StringComparison.OrdinalIgnoreCase))
                    plan.Issues.Add(new PlanIssue(IssueSeverity.Warning, $"The folder Modules\\{m.Folder} currently holds another module ({mp.LocalId}). Its files will be replaced by {m.DisplayName()}; replaced files go to the backup folder."));

                status?.Report($"Checking {m.DisplayName()} ({m.Files.Count} files)…");
                var check = InstalledModuleVerifier.Verify(folder, m, InstallCheckMode.SizeOnly, cancel: ct);
                // SizeOnly + our hash cache = a real hash check without re-reading 10 GB every time (Verify my install forces it).
                int i = 0;
                foreach (var f in check.Files.Where(x => x.State == InstalledFileState.Ok))
                {
                    ct.ThrowIfCancellationRequested();
                    if ((++i % 50) == 0) status?.Report($"Checking {m.DisplayName()}: {i}/{check.Files.Count} files");
                    try
                    {
                        f.ActualSha256 = _hashes.Hash(f.FullPath, forceHash, ct);
                        if (f.ActualSha256 != f.Expected.Sha256) f.State = InstalledFileState.HashDiffers;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        f.State = InstalledFileState.Unreadable;
                        f.Error = ex.Message;
                    }
                }
                check.Extra.RemoveAll(InstallLayout.IsStagingPath);
                mp.Extra.AddRange(check.Extra);
                checks[m.Id] = check;
                local.AddInstalled(check);
                // Files of the previous release that moved or were renamed: their content can be copied instead of downloaded.
                var oldMod = installedManifest?.FindModule(m.Id);
                if (oldMod != null)
                    foreach (var of in oldMod.Files.Where(x => m.FindFile(x.Path) == null))
                    {
                        var full = ToLocal(folder, of.Path);
                        try { if (_fs.FileExists(full) && _fs.GetFileLength(full) == of.Size && _hashes.Hash(full, forceHash, ct) == of.Sha256) local.AddFile(of.Sha256, full); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }

                // Chunk reuse for big files: kept index of the local version, else re-chunk the local file (FastCDC, ~1-2 GB/s).
                foreach (var f in check.Files.Where(x => x.Expected.Chunked && x.State != InstalledFileState.Missing && x.State != InstalledFileState.Unreadable))
                {
                    try
                    {
                        string sha = f.ActualSha256 ?? _hashes.Hash(f.FullPath, false, ct);
                        var idxObj = _hashes.ChunkIndexFor(sha) ?? (sha == f.Expected.Sha256 ? f.Expected.ChunkIndex : null);
                        ChunkIndex idx = idxObj != null && _store.Has(idxObj) ? TryParseIndex(idxObj, ct) : null;
                        if (idx != null && idx.Sha256 == sha) local.AddChunks(f.FullPath, idx);
                        else if (f.State != InstalledFileState.Ok)
                        {
                            status?.Report("Looking for reusable parts of " + f.Path);
                            local.AddChunksByScanning(f.FullPath);
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _log?.Write(LogLevel.Warn, "Could not read " + f.FullPath + ": " + ex.Message); }
                }
            }
            LastChecks = checks;
            // Steam: the Workshop copy of a module (registered last, so files already in Modules stay the preferred source).
            var workshopRoots = AddWorkshopSources(manifest, game, checks, local, status, ct);

            var dp = DownloadPlanner.Compute(manifest, checks, installedManifest, obj => _store.Has(obj) ? TryParseIndex(obj, ct) : null, local);
            if (dp.NeedsIndexes) throw new ManifestException("A chunk list of this release could not be read. Try again later.");

            var byFolder = manifest.Modules.ToDictionary(x => x.Folder, StringComparer.OrdinalIgnoreCase);
            var downloads = new Dictionary<string, DownloadItem>(StringComparer.Ordinal);
            void Need(string obj, long objectSize, long size, bool core)
            {
                if (downloads.TryGetValue(obj, out var d)) { d.IsCore |= core; return; }
                downloads[obj] = new DownloadItem { ObjectName = obj, ObjectSize = objectSize, Size = size, IsCore = core };
            }

            foreach (var pf in dp.Files)
            {
                var mod = byFolder[pf.ModuleFolder];
                var mp = plan.Modules.First(x => x.Module == mod);
                var cur = checks[mod.Id].Files.First(x => x.Path == pf.Target.Path);
                if (pf.Action == FileAction.Keep)
                {
                    mp.UnchangedFiles++;
                    mp.UnchangedBytes += pf.Target.Size;
                    continue;
                }
                var a = new PlanAction
                {
                    Kind = cur.State == InstalledFileState.Missing ? FileActionKind.Add : FileActionKind.Replace,
                    RelPath = pf.Target.Path,
                    Size = pf.Target.Size,
                    OldSize = cur.State == InstalledFileState.Missing ? 0 : cur.ActualSize,
                    Entry = pf.Target,
                    Source = pf.Action,
                    LocalSource = pf.LocalSource,
                    Chunks = pf.Chunks,
                };
                switch (pf.Action)
                {
                    case FileAction.CopyLocal:
                        Stamp(plan, pf.LocalSource);
                        a.FromWorkshop = IsUnder(pf.LocalSource, workshopRoots);
                        if (a.FromWorkshop) { plan.WorkshopCopiedFiles++; plan.WorkshopReusedBytes += pf.Target.Size; }
                        break;
                    case FileAction.Download:
                    case FileAction.FromCache: // not used: the updater caches encoded objects by name
                        a.Source = FileAction.Download;
                        Need(pf.Target.Object, pf.Target.ObjectSize, pf.Target.Size, mod.IsCore());
                        break;
                    case FileAction.Rebuild:
                        foreach (var (c, l) in pf.Chunks)
                        {
                            if (l != null) { plan.ReusedLocalChunks++; Stamp(plan, l.FullPath); }
                            else Need(c.Object, c.ObjectSize, c.Size, mod.IsCore());
                            if (l != null && IsUnder(l.FullPath, workshopRoots)) { a.FromWorkshop = true; plan.WorkshopReusedBytes += c.Size; }
                        }
                        break;
                }
                mp.Actions.Add(a);
            }
            foreach (var (folder, path) in dp.Deletes)
            {
                var mp = plan.Modules.First(x => string.Equals(x.Module.Folder, folder, StringComparison.OrdinalIgnoreCase));
                var full = ToLocal(mp.TargetFolder, path);
                if (!_fs.FileExists(full)) continue;
                mp.Actions.Add(new PlanAction { Kind = FileActionKind.Remove, RelPath = path, OldSize = _fs.GetFileLength(full) });
                mp.Extra.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            }
            plan.ReusedLocalBytes = dp.LocalReuseBytes;
            if (plan.WorkshopReusedBytes > 0)
                _log?.Write(LogLevel.Info, $"Steam Workshop copy reused: {plan.WorkshopCopiedFiles} files and {Units.Bytes(plan.WorkshopReusedBytes)} copied from your Steam Workshop copy instead of downloaded (read-only, the Workshop folder is not changed).");

            foreach (var d in downloads.Values)
            {
                if (_store.Has(d.ObjectName)) continue;
                d.AlreadyHave = Math.Min(_store.PartialBytes(d.ObjectName), d.ObjectSize);
                plan.Downloads.Add(d);
            }
            return plan;
        }

        /// <summary>
        /// Steam players: a subscribed Workshop copy of one of our modules (above all LotRMPCore, ~10 GB) becomes a local source.
        /// A file with the release's size and SHA-256 is copied instead of downloaded; a big chunked file that differs gives its
        /// parts, so only the missing ones are downloaded. Hashes go through the hash cache (one full read the first time only).
        /// Candidates: the manifest's workshopItem folder, else every item whose SubModule.xml (root or one level deeper) has the
        /// module Id. Strictly read-only on the Workshop folder; an unreadable file or folder is skipped, never fails the plan.
        /// </summary>
        /// <returns>The Workshop content folders (to tell Workshop sources from Modules ones); empty when there is none.</returns>
        private List<string> AddWorkshopSources(ReleaseManifest manifest, GameTarget game, Dictionary<string, InstallCheck> checks, LocalContent local,
            IProgress<string> status, CancellationToken ct)
        {
            var roots = SteamWorkshop.ContentFolders(_fs, game);
            if (roots.Count == 0) return roots;
            Dictionary<string, List<string>> byId = null; // module Id -> Workshop module folders, read on first need
            foreach (var m in manifest.Modules)
            {
                ct.ThrowIfCancellationRequested();
                checks.TryGetValue(m.Id, out var check);
                var ok = new HashSet<string>(check?.Files.Where(x => x.State == InstalledFileState.Ok && x.ActualSha256 == x.Expected.Sha256).Select(x => x.Path)
                    ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                // Only what Modules does not already hold: an up-to-date install never re-reads the Workshop copy.
                var needed = m.Files.Where(f => !ok.Contains(f.Path) && !local.TryGetFile(f.Sha256, out _)).ToList();
                if (needed.Count == 0) continue;

                var candidates = new List<string>();
                if (!string.IsNullOrEmpty(m.WorkshopItem))
                    foreach (var ws in roots) candidates.AddRange(SteamWorkshop.ModuleFolders(_fs, Path.Combine(ws, m.WorkshopItem)));
                if (candidates.Count == 0)
                {
                    if (byId == null)
                    {
                        byId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var ws in roots)
                            foreach (var item in SteamWorkshop.Items(_fs, ws))
                                foreach (var d in SteamWorkshop.ModuleFolders(_fs, item))
                                {
                                    var id = DuplicateModuleScanner.ReadModuleId(_fs, d);
                                    if (id == null) continue;
                                    if (!byId.TryGetValue(id, out var l)) byId[id] = l = new List<string>();
                                    l.Add(d);
                                }
                    }
                    if (byId.TryGetValue(m.Id, out var found)) candidates.AddRange(found);
                }

                var copied = new HashSet<ManifestFile>();
                foreach (var folder in candidates)
                {
                    int i = 0, usable = 0;
                    foreach (var f in needed)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (copied.Contains(f)) continue;
                        var full = ToLocal(folder, f.Path);
                        if (!_fs.FileExists(full)) continue;
                        if ((++i % 50) == 0) status?.Report($"Looking for {m.DisplayName()} in your Steam Workshop copy: {i}/{needed.Count} files");
                        try
                        {
                            if (_fs.GetFileLength(full) == f.Size && _hashes.Hash(full, false, ct) == f.Sha256)
                            {
                                local.AddFile(f.Sha256, full);
                                copied.Add(f);
                                usable++;
                            }
                            else if (f.Chunked)
                            {
                                // Older Workshop version of a big file: its unchanged parts are reused, only the missing ones are downloaded.
                                status?.Report("Looking for reusable parts of " + f.Path + " in your Steam Workshop copy");
                                local.AddChunksByScanning(full);
                                usable++;
                            }
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            _log?.Write(LogLevel.Warn, "Steam Workshop copy: skipped " + full + " (" + ex.Message + ")");
                        }
                    }
                    if (usable > 0) _log?.Write(LogLevel.Info, $"Steam Workshop copy of {m.DisplayName()} found in {folder}: {usable} of {needed.Count} needed files usable.");
                }
            }
            return roots;
        }

        /// <summary>Remembers size + date of a reused local file (re-checked right before the install).</summary>
        private void Stamp(InstallPlan plan, string path)
        {
            if (string.IsNullOrEmpty(path) || plan.SourceStamps.ContainsKey(path)) return;
            try { plan.SourceStamps[path] = new SourceStamp(_fs.GetFileLength(path), _fs.GetLastWriteTimeUtc(path)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private static bool IsUnder(string path, List<string> folders) =>
            path != null && folders.Any(d => Path.GetFullPath(path).StartsWith(Path.GetFullPath(d).TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));

        private ChunkIndex TryParseIndex(string obj, CancellationToken ct)
        {
            try { return ChunkIndex.Parse(_store.ReadDecoded(obj, ct)); }
            catch (Exception ex) when (ex is FormatException || ex is InvalidDataException || ex is CorruptObjectException || ex is IOException)
            {
                _log?.Write(LogLevel.Warn, "Chunk list " + obj.Substring(0, 12) + "… unreadable: " + ex.Message);
                _store.Delete(obj);
                return null;
            }
        }
    }
}
