using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Hashing;
using LotR.Releases.Model;
using LotR.Releases.Signing;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;

namespace Lotr.Updater.Core.SelfUpdate
{
    /// <summary>Authenticode of a file (Windows WinVerifyTrust by default; tests inject fakes).</summary>
    public interface IAuthenticodeInspector
    {
        AuthenticodeInfo Inspect(string path);
    }

    public sealed class WindowsAuthenticodeInspector : IAuthenticodeInspector
    {
        public AuthenticodeInfo Inspect(string path)
        {
            try { return Authenticode.Inspect(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return AuthenticodeInfo.Unsigned("unreadable: " + ex.Message); }
        }
    }

    public sealed class SelfUpdateException : Exception
    {
        public SelfUpdateException(string message) : base(message) { }
    }

    /// <summary>A newer updater announced by files/updater/latest.json.</summary>
    public sealed class SelfUpdateOffer
    {
        public UpdaterLatest Latest { get; set; }
        public Uri DownloadUri { get; set; }
        /// <summary>latest.json carries a valid signature by a trusted release key (latest.json.sig).</summary>
        public bool ReleaseKeySigned { get; set; }
        public string Version => Latest.Version;
    }

    /// <summary>pending.json: a downloaded, verified updater that replaces the installed one at the next start.</summary>
    public sealed class PendingSelfUpdate
    {
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public string File { get; set; }
        public string StagedAtUtc { get; set; }

        public static PendingSelfUpdate Load(UpdaterPaths p)
        {
            try
            {
                if (!System.IO.File.Exists(p.PendingUpdateFile)) return null;
                var o = Json.Json.ParseObject(System.IO.File.ReadAllText(p.PendingUpdateFile, Encoding.UTF8));
                var r = new PendingSelfUpdate { Version = o.Str("version"), Sha256 = o.Str("sha256"), File = o.Str("file"), StagedAtUtc = o.Str("stagedAtUtc") };
                // Only a bare file name inside update\ is accepted (a damaged or edited pending.json cannot point elsewhere).
                if (r.Version == null || r.Sha256 == null || r.File == null || r.File != Path.GetFileName(r.File)) return null;
                return r;
            }
            catch (Exception) { return null; }
        }

        public string FullPath(UpdaterPaths p) => Path.Combine(p.UpdateDir, File);

        public void Save(UpdaterPaths p)
        {
            Directory.CreateDirectory(p.UpdateDir);
            var tmp = p.PendingUpdateFile + ".tmp";
            System.IO.File.WriteAllText(tmp, Json.Json.Write(new JsonObject { ["version"] = Version, ["sha256"] = Sha256, ["file"] = File, ["stagedAtUtc"] = StagedAtUtc }), new UTF8Encoding(false));
            if (System.IO.File.Exists(p.PendingUpdateFile)) System.IO.File.Replace(tmp, p.PendingUpdateFile, null);
            else System.IO.File.Move(tmp, p.PendingUpdateFile);
        }
    }

    /// <summary>Version strings "1.2.3", "1.2.3.4", "1.2.3-beta.1" (a pre-release sorts before its release).</summary>
    public static class AppVersions
    {
        private static readonly Regex Pattern = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?$", RegexOptions.CultureInvariant);

        public static bool TryParse(string s, out Version v, out string pre)
        {
            v = null;
            pre = null;
            var m = Pattern.Match(s ?? "");
            if (!m.Success) return false;
            v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0);
            pre = m.Groups[5].Success ? m.Groups[5].Value : null;
            return true;
        }

        /// <summary>&lt;0, 0, &gt;0; an unparsable version sorts first.</summary>
        public static int Compare(string a, string b)
        {
            bool okA = TryParse(a, out var va, out var pa), okB = TryParse(b, out var vb, out var pb);
            if (!okA || !okB) return okA.CompareTo(okB);
            int c = va.CompareTo(vb);
            if (c != 0) return c;
            if (pa == null && pb == null) return 0;
            if (pa == null) return 1;
            if (pb == null) return -1;
            return string.CompareOrdinal(pa, pb);
        }
    }

    /// <summary>
    /// Self-update of LotRMPUpdater.exe (CONTRACTS §1 files/updater): reads latest.json (+ latest.json.sig), downloads the
    /// new exe into %LOCALAPPDATA%\LotRMPUpdater\app\update\, checks its size and SHA-256, its Authenticode signer against
    /// the running exe's, and stages it. The swap happens at the next start (<see cref="SelfUpdateSwap"/>); the previous exe
    /// is kept as LotRMPUpdater.old.exe for rollback.
    /// Trust: the new exe is accepted only when latest.json is signed by a trusted release key, or when both exes are
    /// Authenticode-signed by the same subject. A signed updater never accepts an unsigned or differently signed one.
    /// </summary>
    public sealed class SelfUpdater
    {
        private static readonly Regex ExeName = new Regex(@"^LotRMPUpdater-[0-9A-Za-z.\-]+\.exe$", RegexOptions.CultureInvariant);
        private readonly ServerEndpoints _ep;
        private readonly HttpFetcher _fetch;
        private readonly UpdaterPaths _paths;
        private readonly List<TrustedKey> _keys;
        private readonly IAuthenticodeInspector _auth;
        private readonly IUpdaterLog _log;

        public SelfUpdater(ServerEndpoints ep, HttpFetcher fetch, UpdaterPaths paths, IEnumerable<string> trustedKeysPem, IAuthenticodeInspector auth,
            IUpdaterLog log, string currentVersion, string currentExe)
        {
            _ep = ep;
            _fetch = fetch;
            _paths = paths;
            _keys = (trustedKeysPem ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => TrustedKey.FromPem(k)).ToList();
            _auth = auth ?? new WindowsAuthenticodeInspector();
            _log = log;
            CurrentVersion = currentVersion;
            CurrentExe = currentExe;
        }

        public string CurrentVersion { get; }
        public string CurrentExe { get; }

        public static string LatestFileName(string channel) => string.IsNullOrEmpty(channel) || channel == "stable" ? "latest.json" : "latest-" + channel + ".json";

        /// <summary>A verified update staged for the next start, newer than this exe (null otherwise).</summary>
        public PendingSelfUpdate Pending
        {
            get
            {
                var p = PendingSelfUpdate.Load(_paths);
                return p != null && AppVersions.Compare(p.Version, CurrentVersion) > 0 && File.Exists(p.FullPath(_paths)) ? p : null;
            }
        }

        /// <summary>Returns the offer when the server announces a NEWER updater (and not the one the player rolled back from).</summary>
        public async Task<SelfUpdateOffer> CheckAsync(string channel, string skippedVersion, CancellationToken ct)
        {
            string name = LatestFileName(channel);
            var uri = new Uri(_ep.Base, "files/updater/" + name);
            var bytes = await _fetch.GetBytesAsync(uri, ct, allowNotFound: true, maxBytes: 64 * 1024).ConfigureAwait(false);
            if (bytes == null && name != "latest.json")
            {
                uri = _ep.UpdaterLatest;
                bytes = await _fetch.GetBytesAsync(uri, ct, allowNotFound: true, maxBytes: 64 * 1024).ConfigureAwait(false);
            }
            if (bytes == null) { _log?.Write(LogLevel.Detail, "No updater release published yet (latest.json not found)."); return null; }
            var sig = await _fetch.GetBytesAsync(new Uri(uri + ".sig"), ct, allowNotFound: true, maxBytes: 64 * 1024).ConfigureAwait(false);

            bool keySigned = false;
            if (sig != null)
            {
                if (_keys.Count == 0) _log?.Write(LogLevel.Detail, "latest.json is signed, but this build has no release key to check it.");
                else
                {
                    SignatureCheck c;
                    try { c = ManifestSignature.Verify(bytes, sig, _keys); }
                    catch (Exception ex) when (ex is FormatException || ex is System.Security.Cryptography.CryptographicException || ex is InvalidDataException || ex is LotR.Releases.Model.ManifestFormatException)
                    { c = new SignatureCheck { Valid = false, Message = ex.Message }; }
                    if (!c.Valid) throw new SelfUpdateException("The updater release file (latest.json) has a signature that does not match the LotR team key (" + c.Message + "). Ignored.");
                    keySigned = true;
                }
            }

            UpdaterLatest latest;
            try { latest = UpdaterLatest.Parse(bytes); }
            catch (Exception ex) when (ex is FormatException || ex is LotR.Releases.Model.ManifestFormatException || ex is LotR.Releases.Json.JsonParseException)
            { throw new SelfUpdateException("latest.json is not valid: " + ex.Message); }
            var val = latest.Validate();
            if (!val.IsValid) throw new SelfUpdateException("latest.json is not valid: " + string.Join("; ", val.Errors));

            if (AppVersions.Compare(latest.Version, CurrentVersion) <= 0)
            {
                _log?.Write(LogLevel.Detail, "The updater is up to date (" + CurrentVersion + ").");
                return null;
            }
            if (!string.IsNullOrEmpty(skippedVersion) && AppVersions.Compare(latest.Version, skippedVersion) <= 0)
            {
                _log?.Write(LogLevel.Detail, "Updater " + latest.Version + " skipped (you rolled back from it).");
                return null;
            }
            var download = ResolveUrl(latest.Url);
            _log?.Write(LogLevel.Info, "A new version of this updater is available: " + latest.Version + " (you have " + CurrentVersion + ").");
            return new SelfUpdateOffer { Latest = latest, DownloadUri = download, ReleaseKeySigned = keySigned };
        }

        /// <summary>Site-relative URL, or https on the same host as the configured server. Anything else is refused.</summary>
        public Uri ResolveUrl(string url)
        {
            Uri u;
            if (url.StartsWith("/", StringComparison.Ordinal)) u = new Uri(_ep.Base, url.TrimStart('/'));
            else if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, _ep.Base.Host, StringComparison.OrdinalIgnoreCase))
                throw new SelfUpdateException("latest.json points outside the LotR server (" + url + "); refused.");
            var file = Uri.UnescapeDataString(u.Segments.Last());
            if (!ExeName.IsMatch(file)) throw new SelfUpdateException("latest.json points at an unexpected file name (" + file + "); refused.");
            return u;
        }

        /// <summary>Downloads, verifies and stages the offer. Throws <see cref="SelfUpdateException"/> (nothing staged) on any doubt.</summary>
        public async Task<PendingSelfUpdate> DownloadAsync(SelfUpdateOffer offer, IProgress<long> progress, CancellationToken ct)
        {
            var already = Pending;
            if (already != null && already.Version == offer.Version && already.Sha256 == offer.Latest.Sha256) return already;

            Directory.CreateDirectory(_paths.UpdateDir);
            var fileName = Uri.UnescapeDataString(offer.DownloadUri.Segments.Last());
            var part = Path.Combine(_paths.UpdateDir, fileName + ".part");
            var final = Path.Combine(_paths.UpdateDir, fileName);
            long done = 0;
            await _fetch.DownloadResumableAsync(offer.DownloadUri, part, offer.Latest.Size, n => { done += n; progress?.Report(done); }, ct).ConfigureAwait(false);

            var sha = Sha256Util.OfFile(part);
            if (sha != offer.Latest.Sha256)
            {
                TryDelete(part);
                throw new SelfUpdateException("The downloaded updater does not match latest.json (SHA-256 " + sha + ", expected " + offer.Latest.Sha256 + "). It was deleted.");
            }
            TryDelete(final);
            File.Move(part, final);

            var current = CurrentExe != null && File.Exists(CurrentExe) ? _auth.Inspect(CurrentExe) : AuthenticodeInfo.Unsigned("running exe not found");
            var candidate = _auth.Inspect(final);
            string why = AuthenticodeInfo.CheckSameSigner(current, candidate);
            if (why == null && offer.Latest.Signed && !candidate.Valid) why = "latest.json says the new updater is signed, but it is not (" + candidate.Status + ")";
            if (why == null && !offer.ReleaseKeySigned && !(current.Valid && candidate.Valid))
                why = "neither latest.json (release key) nor the exe (Authenticode) carries a signature this updater can check";
            if (why != null)
            {
                TryDelete(final);
                throw new SelfUpdateException("The new updater was refused: " + why + ". It was deleted; this version keeps working.");
            }

            var pending = new PendingSelfUpdate { Version = offer.Version, Sha256 = sha, File = fileName, StagedAtUtc = DateTime.UtcNow.ToString("o") };
            pending.Save(_paths);
            foreach (var old in Directory.GetFiles(_paths.UpdateDir, "LotRMPUpdater-*.exe").Where(f => !string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)))
                TryDelete(old);
            _log?.Write(LogLevel.Info, "Updater " + offer.Version + " downloaded and checked (SHA-256" + (offer.ReleaseKeySigned ? ", release-key signature" : "") +
                                       (candidate.Valid ? ", Authenticode " + candidate.Subject : "") + "). It replaces this one at the next start.");
            return pending;
        }

        private static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); } catch (Exception) { }
        }
    }

    public sealed class SwapOutcome
    {
        public bool Swapped { get; set; }
        public string RelaunchExe { get; set; }
        public string Message { get; set; }
        public static SwapOutcome None(string why) => new SwapOutcome { Message = why };
    }

    /// <summary>
    /// The swap at start-up, before any window: the RUNNING installed exe is renamed to LotRMPUpdater.old.exe (Windows
    /// allows renaming a running exe, not deleting it), the staged one takes its name, and the caller restarts it.
    /// Any failure puts the old exe back. Only the installed copy (%LOCALAPPDATA%\LotRMPUpdater\app\) is ever swapped.
    /// </summary>
    public static class SelfUpdateSwap
    {
        public static SwapOutcome ApplyPending(UpdaterPaths p, string runningExe, string runningVersion, IUpdaterLog log)
        {
            var pending = PendingSelfUpdate.Load(p);
            if (pending == null) return SwapOutcome.None("nothing pending");
            if (!SamePath(runningExe, p.InstalledExe)) return SwapOutcome.None("not running the installed copy; the update stays pending");
            var staged = pending.FullPath(p);
            if (AppVersions.Compare(pending.Version, runningVersion) <= 0 || !File.Exists(staged) || Sha256Util.OfFile(staged) != pending.Sha256)
            {
                Discard(p, pending);
                log?.Write(LogLevel.Warn, "A staged updater (" + pending.Version + ") was not newer or no longer matched its checksum; it was discarded.");
                return SwapOutcome.None("discarded");
            }
            try
            {
                if (File.Exists(p.PreviousExe)) File.Delete(p.PreviousExe);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return SwapOutcome.None("the previous updater is still in use (" + ex.Message + "); trying again next start");
            }
            File.Move(p.InstalledExe, p.PreviousExe);
            try
            {
                File.Move(staged, p.InstalledExe);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                File.Move(p.PreviousExe, p.InstalledExe);
                log?.Write(LogLevel.Error, "Could not put the new updater in place (" + ex.Message + "); kept " + runningVersion + ".");
                return SwapOutcome.None("swap failed");
            }
            try { File.WriteAllText(Path.Combine(p.AppDir, "previous-version.txt"), runningVersion + "\n"); } catch (Exception) { }
            try { File.Delete(p.PendingUpdateFile); } catch (Exception) { }
            log?.Write(LogLevel.Info, "Updater " + runningVersion + " replaced by " + pending.Version + " (the previous one is kept as " + Path.GetFileName(p.PreviousExe) + " for rollback).");
            return new SwapOutcome { Swapped = true, RelaunchExe = p.InstalledExe, Message = "updated to " + pending.Version };
        }

        /// <summary>Puts LotRMPUpdater.old.exe back (the current one is kept as LotRMPUpdater.rolledback.exe until the next swap).</summary>
        public static SwapOutcome Rollback(UpdaterPaths p, IUpdaterLog log)
        {
            if (!File.Exists(p.PreviousExe)) return SwapOutcome.None("no previous updater is kept");
            var bad = Path.Combine(p.AppDir, "LotRMPUpdater.rolledback.exe");
            try { if (File.Exists(bad)) File.Delete(bad); } catch (Exception) { }
            if (File.Exists(p.InstalledExe)) File.Move(p.InstalledExe, bad);
            try { File.Move(p.PreviousExe, p.InstalledExe); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (File.Exists(bad) && !File.Exists(p.InstalledExe)) File.Move(bad, p.InstalledExe);
                return SwapOutcome.None("rollback failed: " + ex.Message);
            }
            var pending = PendingSelfUpdate.Load(p);
            if (pending != null) Discard(p, pending);
            log?.Write(LogLevel.Warn, "The updater was rolled back to the previous version.");
            return new SwapOutcome { Swapped = true, RelaunchExe = p.InstalledExe, Message = "rolled back" };
        }

        public static string PreviousVersion(UpdaterPaths p)
        {
            try
            {
                var f = Path.Combine(p.AppDir, "previous-version.txt");
                return File.Exists(p.PreviousExe) && File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch (Exception) { return null; }
        }

        private static void Discard(UpdaterPaths p, PendingSelfUpdate pending)
        {
            try { File.Delete(pending.FullPath(p)); } catch (Exception) { }
            try { File.Delete(p.PendingUpdateFile); } catch (Exception) { }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
