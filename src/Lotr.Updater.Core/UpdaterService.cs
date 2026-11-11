using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Model;
using Lotr.Updater.Core.Game;
using Lotr.Updater.Core.Install;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.Objects;
using Lotr.Updater.Core.Planning;
using Lotr.Updater.Core.Schedule;
using Lotr.Updater.Core.Security;
using Lotr.Updater.Core.SelfUpdate;

namespace Lotr.Updater.Core
{
    public sealed class UpdaterConfig
    {
        public string AppVersion { get; set; } = "0.2.0";
        public string BaseUrl { get; set; } = ServerEndpoints.ProductionBase;
        /// <summary>Dev mode: local test server, a fixed fake game folder, extra trusted test key. Shown in red in the UI.</summary>
        public bool Dev { get; set; }
        /// <summary>When set, the game is never searched: only this folder is used (dev / tests: never the real game).</summary>
        public string FixedGamePath { get; set; }
        public List<string> TrustedKeysPem { get; } = new List<string>(TrustedKeys.Production);
        public bool AllowUnsigned { get; set; }
        public int Connections { get; set; } = 6;
        /// <summary>Path of the running exe (self-update compares its Authenticode signer with the new one's).</summary>
        public string CurrentExe { get; set; }
    }

    public sealed class CheckResult
    {
        public string LatestReleaseId { get; set; }
        public ReleaseManifest Manifest { get; set; }
        public VerificationResult Signature { get; set; }
        public InstallPlan Plan { get; set; }
        public string Error { get; set; }
        /// <summary>Technical cause of <see cref="Error"/> (log / "Details" only; may be in the player's Windows language).</summary>
        public string ErrorDetail { get; set; }
        public bool UpToDate => Plan != null && !Plan.HasChanges;
        /// <summary>The server offers an OLDER release than the installed one (CONTRACTS §8 anti-rollback): only installed after the player agrees.</summary>
        public bool RollbackOffered { get; set; }
    }

    /// <summary>Everything the UI needs, wired once. No UI types here.</summary>
    public sealed class UpdaterService
    {
        public UpdaterService(UpdaterConfig config, UpdaterPaths paths = null, IFileSystem fs = null, HttpClient http = null, IGameLocator locator = null,
            IProcessProbe processes = null, ILockingProcessFinder lockFinder = null, IManifestVerifier verifier = null, RetryPolicy retry = null,
            IAuthenticodeInspector authenticode = null)
        {
            Config = config;
            Paths = paths ?? UpdaterPaths.Default();
            Fs = fs ?? new RealFileSystem();
            Log = new UpdaterLog(Paths.LogDir);
            State = UpdaterState.Load(Paths.StateFile);
            Endpoints = new ServerEndpoints(config.BaseUrl);
            Fetcher = new HttpFetcher(http ?? HttpFetcher.CreateClient(config.AppVersion), Fs, Log, retry);
            Source = new HttpReleaseSource(Endpoints, Fetcher);
            Store = new ObjectStore(Paths, Fs, Fetcher, Log);
            Hashes = new HashCache(Paths.HashCacheFile, Fs);
            Locator = locator ?? new DetectionGameLocator();
            Verifier = verifier ?? (config.AllowUnsigned ? (IManifestVerifier)new DevAcceptAllVerifier() : new ReleaseSignatureVerifier(config.TrustedKeysPem));
            Queue = new QueueSlotClient(Fetcher, Endpoints, Log);
            Support = new SupportClient(Fetcher, Endpoints);
            Planner = new InstallPlanner(Fs, Hashes, Store, Source, Paths, Log);
            Engine = new InstallEngine(Fs, Paths, Store, Source, Queue, Hashes, processes ?? new SystemProcessProbe(), lockFinder, Log,
                new EngineOptions { Connections = config.Connections });
            SelfUpdate = new SelfUpdater(Endpoints, Fetcher, Paths, config.TrustedKeysPem, authenticode, Log, config.AppVersion, config.CurrentExe);
        }

        public SelfUpdater SelfUpdate { get; }

        public UpdaterConfig Config { get; }
        public UpdaterPaths Paths { get; }
        public IFileSystem Fs { get; }
        public UpdaterLog Log { get; }
        public UpdaterState State { get; }
        public ServerEndpoints Endpoints { get; }
        public HttpFetcher Fetcher { get; }
        public IReleaseSource Source { get; }
        public ObjectStore Store { get; }
        public HashCache Hashes { get; }
        public IGameLocator Locator { get; }
        public IManifestVerifier Verifier { get; }
        public QueueSlotClient Queue { get; }
        public SupportClient Support { get; }
        public InstallPlanner Planner { get; }
        public InstallEngine Engine { get; }

        public GameTarget Game { get; private set; }
        public GameSearch LastSearch { get; private set; }
        public ReleaseManifest InstalledManifest { get; private set; }

        private string InstalledManifestFile => Path.Combine(Paths.Home, "installed-manifest.json");

        public void SaveState()
        {
            try { State.Save(Paths.StateFile); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Log.Warn("Could not save settings: " + ex.Message); }
        }

        /// <summary>Finds the game: fixed path (dev), else the saved choice, else full detection.</summary>
        public GameSearch LocateGame()
        {
            GameSearch s;
            if (!string.IsNullOrEmpty(Config.FixedGamePath)) s = Locator.Validate(Config.FixedGamePath);
            else if (!string.IsNullOrEmpty(State.GamePath))
            {
                s = Locator.Validate(State.GamePath);
                if (s.Found.Count == 0) { Log.Warn("The saved game folder is no longer valid: " + s.Message); s = Locator.FindAll(); }
            }
            else s = Locator.FindAll();
            LastSearch = s;
            Game = s.Found.FirstOrDefault();
            if (Game != null)
            {
                Log.Info($"Game: Bannerlord {Game.GameVersion ?? "(version unknown)"} ({Game.StoreLabel}) at {Game.Root}");
                if (State.GamePath == null && string.IsNullOrEmpty(Config.FixedGamePath)) { State.GamePath = Game.Root; SaveState(); }
                try { Engine.RecoverInterrupted(Game.ModulesPath); } catch (Exception ex) { Log.Error("Recovery of an interrupted update failed: " + ex.Message); }
            }
            else Log.Warn(s.Message);
            LoadInstalledManifest();
            return s;
        }

        /// <summary>Settings: the player picks another folder.</summary>
        public GameSearch ChooseGame(string path)
        {
            var s = Locator.Validate(path);
            if (s.Found.Count > 0)
            {
                Game = s.Found[0];
                State.GamePath = Game.Root;
                SaveState();
                Log.Info("Game folder changed to " + Game.Root);
            }
            LastSearch = s;
            return s;
        }

        private void LoadInstalledManifest()
        {
            try
            {
                _installedManifestBytes = File.Exists(InstalledManifestFile) ? File.ReadAllBytes(InstalledManifestFile) : null;
                InstalledManifest = _installedManifestBytes == null ? null : ManifestCodec.Parse(_installedManifestBytes);
            }
            catch (Exception) { InstalledManifest = null; _installedManifestBytes = null; }
        }

        public async Task<(ReleaseManifest Manifest, VerificationResult Sig, byte[] Bytes)> FetchManifestAsync(string releaseId, CancellationToken ct)
        {
            var sm = await Source.GetManifestAsync(releaseId, ct).ConfigureAwait(false);
            var v = Verifier.Verify(sm.Bytes, sm.Signature);
            if (!v.Trusted) throw new InstallException(v.Message);
            // Signature checked BEFORE parsing anything (docs/MANIFEST.md §4), then the strict parser + validator.
            var m = ManifestCodec.Parse(sm.Bytes);
            var val = ManifestValidator.Validate(m);
            if (!val.IsValid) throw new ManifestException("The release file is invalid and was refused: " + string.Join("; ", val.Errors.Take(3)));
            foreach (var w in val.Warnings) Log.Detail("Manifest: " + w);
            var rule = UpdaterManifestRules.Check(m);
            if (rule != null) throw new ManifestException("The release was refused: " + rule);
            if (m.ReleaseId != releaseId) throw new InstallException("The release index and the release file disagree (" + releaseId + " vs " + m.ReleaseId + "); refused.");
            return (m, v, sm.Bytes);
        }

        private byte[] _installedManifestBytes;

        /// <summary>Antivirus results from the unsigned sidecar (informational only; absence never blocks anything).</summary>
        public async Task<List<SecurityScan>> FetchSecurityAsync(string releaseId, CancellationToken ct)
        {
            try
            {
                var b = await Fetcher.GetBytesAsync(Endpoints.Security(releaseId), ct, allowNotFound: true).ConfigureAwait(false);
                if (b == null) return new List<SecurityScan>();
                return SecurityScan.Parse(Json.Json.ParseObject(Encoding.UTF8.GetString(b)));
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Detail("No security results: " + ex.Message);
                return new List<SecurityScan>();
            }
        }

        /// <summary>The site's patch notes (/hub.json). Display only, short retries: a missing or unreachable site just leaves
        /// the notes of the signed manifest on screen. Null when it could not be read.</summary>
        public async Task<List<News.SiteNewsItem>> FetchSiteNewsAsync(CancellationToken ct) => (await FetchSiteHubAsync(ct).ConfigureAwait(false))?.News;

        /// <summary>/hub.json as a whole: the patch notes and the coming battle nights (0.3.2). Same rules: display only,
        /// null when it could not be read.</summary>
        public async Task<News.SiteHub> FetchSiteHubAsync(CancellationToken ct)
        {
            try
            {
                var quick = new HttpFetcher(Fetcher.Client, Fs, null, new RetryPolicy { MaxAttempts = 2 });
                var b = await quick.GetBytesAsync(Endpoints.SiteNews, ct, allowNotFound: true, maxBytes: News.SiteNews.MaxBytes).ConfigureAwait(false);
                if (b == null) { Log.Detail("No site news (" + Endpoints.SiteNews.AbsolutePath + " not found)."); return null; }
                var hub = News.SiteNews.ParseHub(b);
                Log.Detail("Site news: " + hub.News.Count + " chronicle(s), " + hub.Musters.Count + " battle night(s).");
                return hub;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Detail("No site news: " + ex.Message);
                return null;
            }
        }

        /// <summary>The server's pause notice (/status.json, event lock), asked only after a failed check: one try, null when
        /// there is none (404) or it cannot be read. Never throws (except cancellation).</summary>
        public async Task<News.ServerNotice> FetchServerNoticeAsync(CancellationToken ct)
        {
            try
            {
                var once = new HttpFetcher(Fetcher.Client, Fs, null, new RetryPolicy { MaxAttempts = 1 });
                var b = await once.GetBytesAsync(Endpoints.Status, ct, allowNotFound: true, maxBytes: News.ServerNotice.MaxBytes).ConfigureAwait(false);
                if (b == null) return null;
                var n = News.ServerNotice.Parse(b, Config.BaseUrl);
                if (n != null) Log.Info("The server has paused downloads (event lock)" + (n.UntilUtc != null ? " until " + n.UntilUtc.Value.ToString("u", System.Globalization.CultureInfo.InvariantCulture) : "") + ".");
                return n;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                Log.Detail("No server notice: " + ex.Message);
                return null;
            }
        }

        /// <summary>Checks the channel, verifies the signature, compares with the disk. Changes nothing in the game folder.</summary>
        public async Task<CheckResult> CheckAsync(IProgress<string> status, CancellationToken ct)
        {
            var r = new CheckResult();
            if (Game == null) { r.Error = "Bannerlord was not found. Choose its folder in Settings."; return r; }
            try
            {
                status?.Report("Asking the server for the current release…");
                r.LatestReleaseId = await Source.GetCurrentReleaseIdAsync(State.Channel, ct).ConfigureAwait(false);
                if (r.LatestReleaseId == null) { r.Error = $"No release is published on the {State.Channel} channel yet."; return r; }
                Log.Info($"Channel {State.Channel}: current release {r.LatestReleaseId} (installed: {State.InstalledReleaseId ?? "none"})");
                var (m, v, bytes) = await FetchManifestAsync(r.LatestReleaseId, ct).ConfigureAwait(false);
                r.Manifest = m;
                r.Signature = v;
                Log.Info(v.Message);
                // CONTRACTS §8 anti-rollback: the signed channel must be the one asked for, and an older release than the installed
                // one is only taken when the player explicitly agrees (the team can legitimately roll back a broken release).
                if (!string.Equals(m.Channel, State.Channel, StringComparison.Ordinal))
                    throw new ManifestException($"The server pointed the {State.Channel} channel to a release signed for '{m.Channel}'. Refused.");
                var installed = InstalledForPlanning();
                if (installed != null && installed.PublishedUtc() > m.PublishedUtc() && State.AllowRollbackTo != m.ReleaseId)
                {
                    r.RollbackOffered = true;
                    r.Error = $"The server now offers an older release ({m.ReleaseId}, published {m.PublishedAt}) than the one you have ({installed.ReleaseId}). " +
                              "This normally means the team took back a broken update. Nothing was changed. " +
                              "Press \"Accept the rollback\" only if the team announced it on Discord; otherwise just check again later.";
                    // The other possible cause (someone replaying an old signed release) is for the log, not to frighten players.
                    Log.Warn(r.Error + " (An unannounced rollback could also be a replayed old release: refused until the player accepts.)");
                    return r;
                }
                status?.Report("Comparing with the files on your PC…");
                r.Plan = await Planner.PlanAsync(m, bytes, Game, installed, State.InstalledReleaseId, forceHash: false, status, ct).ConfigureAwait(false);
                r.Plan.SignatureStatus = v.Message;
                Engine.Preflight(r.Plan, forApply: false);
                Hashes.Save();
                State.LastCheckUtc = DateTime.UtcNow.ToString("o");
                if (r.Plan.HasChanges) PreDownloadPolicy.Observe(State, m.ReleaseId, DateTime.UtcNow);
                else if (State.InstalledReleaseId != m.ReleaseId)
                {
                    // Files already match this release (installed by hand or by a previous run): record it.
                    State.InstalledReleaseId = m.ReleaseId;
                    SaveInstalledManifest(bytes);
                }
                SaveState();
                Log.Info(r.Plan.HasChanges
                    ? $"Update available: {r.Plan.Modules.Sum(x => x.Actions.Count)} file changes, {Units.Bytes(r.Plan.DownloadBytes)} to download."
                    : "Your mod files are up to date.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpFetchException || ex is InstallException || ex is ManifestException || ex is FormatException || ex is HttpRequestException || ex is IOException || ex is NotSupportedException || ex is CorruptObjectException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                var p = PlayerErrors.ForCheck(ex);
                r.Error = p.Message;
                r.ErrorDetail = p.Detail;
                Log.Error("Check failed: " + PlayerErrors.Technical(ex));
            }
            return r;
        }

        private void SaveInstalledManifest(byte[] bytes)
        {
            if (bytes == null) return;
            try
            {
                Directory.CreateDirectory(Paths.Home);
                File.WriteAllBytes(InstalledManifestFile, bytes);
                LoadInstalledManifest();
            }
            catch (IOException) { }
        }

        public async Task<string> InstallAsync(InstallPlan plan, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            string backup;
            try
            {
                backup = await Engine.ApplyAsync(plan, State, progress, ct).ConfigureAwait(false);
            }
            catch (InstallException ex) when (ex.CanReplan && !ct.IsCancellationRequested && Game != null)
            {
                // Second safety net: a reused local file (Steam Workshop copy, moved file) failed even after the engine's own
                // fallback, and everything was put back. Compare with the disk again (fresh plan: it downloads what is gone) and
                // try exactly once more before telling the player.
                Log.Warn("Install failed because of a reused local file (" + ex.Detail + "); checking the files again and retrying once.");
                progress?.Report(new EngineProgress { Phase = EnginePhase.Checking, Message = "Some files changed while installing; checking again…" });
                InstallPlan fresh;
                try { fresh = await Planner.PlanAsync(plan.Manifest, plan.ManifestBytes, Game, InstalledForPlanning(), State.InstalledReleaseId, forceHash: false, null, ct).ConfigureAwait(false); }
                catch (Exception pex) when (!(pex is OperationCanceledException))
                {
                    Log.Error("Could not check the files again: " + PlayerErrors.Technical(pex));
                    throw ex;
                }
                fresh.SignatureStatus = plan.SignatureStatus;
                Hashes.Save();
                backup = await Engine.ApplyAsync(fresh, State, progress, ct).ConfigureAwait(false);
                plan = fresh;
            }
            SaveInstalledManifest(plan.ManifestBytes);
            if (State.AllowRollbackTo == plan.Manifest.ReleaseId) State.AllowRollbackTo = null;
            SaveState();
            return backup;
        }

        /// <summary>Background pre-download when due (objects only, into the cache; the game folder is not touched).</summary>
        public async Task<bool> PreDownloadIfDueAsync(CheckResult latest, IProgress<EngineProgress> progress, CancellationToken ct)
        {
            if (latest?.Plan == null || !latest.Plan.HasChanges || latest.Plan.Downloads.Count == 0) return false;
            if (!PreDownloadPolicy.IsDue(State, DateTime.UtcNow)) return false;
            // Same free space rule as the install (cache drive only: the game folder is not touched yet).
            var space = Engine.SpaceProblems(latest.Plan, downloadOnly: true);
            if (space.Count > 0)
            {
                Log.Warn("Background pre-download skipped: " + space[0]);
                return false;
            }
            Log.Info("Pre-downloading release " + latest.Manifest.ReleaseId + " in the background (nothing is installed until you click Update).");
            await Engine.DownloadAsync(latest.Plan, State.InstallId, progress, ct).ConfigureAwait(false);
            State.PendingDownloaded = true;
            SaveState();
            latest.Plan.Downloads.Clear();
            return true;
        }

        public async Task<VerifyReport> VerifyAsync(IProgress<string> status, CancellationToken ct)
        {
            if (Game == null) throw new InstallException("Bannerlord was not found.");
            var manifest = InstalledForPlanning();
            var bytes = _installedManifestBytes;
            if (manifest == null)
            {
                var id = State.InstalledReleaseId ?? await Source.GetCurrentReleaseIdAsync(State.Channel, ct).ConfigureAwait(false);
                if (id == null) throw new InstallException("No release to compare with.");
                var f = await FetchManifestAsync(id, ct).ConfigureAwait(false);
                manifest = f.Manifest;
                bytes = f.Bytes;
            }
            var rep = await Engine.VerifyAsync(Planner, manifest, bytes, Game, State.InstalledReleaseId, status, ct).ConfigureAwait(false);
            rep.RepairPlan.SignatureStatus = "Repair of the installed release";
            Engine.Preflight(rep.RepairPlan, forApply: false);
            return rep;
        }

        /// <summary>The support log: recent log files + game detection report, with the user name masked.</summary>
        public string BuildSupportLog()
        {
            var sb = new StringBuilder();
            var recent = Log.ReadRecent();
            int errors = recent.Split('\n').Count(l => l.Length > 14 && l.Substring(9).StartsWith("ERROR"));
            // Header read by lotr-services / the Discord bot summary (Key: value lines, first).
            sb.AppendLine("App version: " + Config.AppVersion);
            sb.AppendLine("Store: " + (Game?.StoreLabel ?? "not found"));
            sb.AppendLine("Errors: " + errors);
            sb.AppendLine("Game version: " + (Game?.GameVersion ?? "unknown"));
            sb.AppendLine("Channel: " + State.Channel);
            sb.AppendLine("Installed release: " + (State.InstalledReleaseId ?? "none"));
            sb.AppendLine("Install id: " + State.InstallId);
            sb.AppendLine($"Windows {Environment.OSVersion.VersionString}  64-bit {Environment.Is64BitOperatingSystem}");
            if (Game != null) sb.AppendLine($"Game: {Game.StoreLabel} {Game.GameVersion} at {Game.Root} (writable {Game.Writable})");
            if (LastSearch?.ReportForLog != null) { sb.AppendLine("--- game detection ---"); sb.AppendLine(LastSearch.ReportForLog); }
            // A start-up crash leaves logs\crash-*.txt (written by the start-up guard): the most recent one of the last 7 days
            // travels with the report, so a player whose app failed to start can still send it from the Support page.
            var crash = LatestCrashReport(TimeSpan.FromDays(7));
            if (crash != null)
            {
                sb.AppendLine("--- latest crash report (" + Path.GetFileName(crash) + ") ---");
                try { sb.AppendLine(File.ReadAllText(crash, Encoding.UTF8)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { sb.AppendLine("(unreadable: " + ex.Message + ")"); }
            }
            sb.AppendLine("--- log ---");
            sb.Append(recent);
            return LogMasker.Mask(sb.ToString());
        }

        /// <summary>Newest logs\crash-*.txt younger than <paramref name="maxAge"/>, or null.</summary>
        public string LatestCrashReport(TimeSpan maxAge)
        {
            try
            {
                if (!Directory.Exists(Paths.LogDir)) return null;
                return Directory.GetFiles(Paths.LogDir, "crash-*.txt")
                    .Select(f => new FileInfo(f))
                    .Where(f => DateTime.UtcNow - f.LastWriteTimeUtc <= maxAge)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName)
                    .FirstOrDefault();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        public Task<SupportCode> SendSupportAsync(string maskedLog, CancellationToken ct) => Support.SendAsync(State.InstallId, Config.AppVersion, maskedLog, ct);

        /// <summary>Modules the player must tick in the official launcher (the app edits the launcher's list only on request: Game/LauncherSelection.cs).</summary>
        /// <summary>The installed release's manifest when it matches state.json (its file list drives deletions).</summary>
        private ReleaseManifest InstalledForPlanning() =>
            InstalledManifest != null && InstalledManifest.ReleaseId == State.InstalledReleaseId ? InstalledManifest : null;

        /// <summary>The player accepted the older release the server offers (anti-rollback override, one release only).</summary>
        public void AcceptRollback(string releaseId)
        {
            State.AllowRollbackTo = releaseId;
            SaveState();
            Log.Warn("The player accepted a rollback to " + releaseId + ".");
        }

        public IReadOnlyList<string> ModulesToTick(ReleaseManifest m)
        {
            m = m ?? InstalledManifest;
            if (m == null) return new List<string>();
            return m.Modules.Where(x => !string.Equals(x.Role, ModuleRoles.AdminClient, StringComparison.Ordinal)).Select(x => x.DisplayName()).ToList();
        }
    }
}
