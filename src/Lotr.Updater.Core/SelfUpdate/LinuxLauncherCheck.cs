using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LotR.Releases.Model;
using LotR.Releases.Publishing;
using LotR.Releases.Signing;
using Lotr.Updater.Core.Logging;
using Lotr.Updater.Core.Net;

namespace Lotr.Updater.Core.SelfUpdate
{
    /// <summary>A newer lotr-updater announced by files/updater/linux/latest.json.</summary>
    public sealed class LinuxLauncherNotice
    {
        public string Version { get; set; }
        public string CurrentVersion { get; set; }
        /// <summary>The site's Install page (built from the configured server, never taken from latest.json).</summary>
        public string DownloadPage { get; set; }
        public long Size { get; set; }
    }

    /// <summary>
    /// "Is there a newer lotr-updater?" for the Linux / Steam Deck updater (docs/LINUX.md). It reads ONLY
    /// files/updater/linux/latest.json (+ .sig), never the Windows updater/latest.json, and it never downloads or replaces
    /// anything: a running single-file program is not swapped on Linux, the player gets the new archive from the Install
    /// page. Same trust rule as <see cref="SelfUpdater.CheckAsync"/>: a build that carries a release key believes only a
    /// latest.json signed with it. Any doubt (missing, unsigned, bad signature, invalid, foreign url, network) = no notice,
    /// logged as a detail; it never stops the command that asked.
    /// </summary>
    public sealed class LinuxLauncherCheck
    {
        public const string LatestPath = "files/updater/linux/latest.json";
        public const string InstallPagePath = "install/";
        private const int MaxBytes = 64 * 1024;

        private readonly ServerEndpoints _ep;
        private readonly HttpFetcher _fetch;
        private readonly List<TrustedKey> _keys;
        private readonly IUpdaterLog _log;

        public LinuxLauncherCheck(ServerEndpoints ep, HttpFetcher fetch, IEnumerable<string> trustedKeysPem, IUpdaterLog log, string currentVersion)
        {
            _ep = ep;
            _fetch = fetch;
            _keys = (trustedKeysPem ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => TrustedKey.FromPem(k)).ToList();
            _log = log;
            CurrentVersion = currentVersion;
        }

        public string CurrentVersion { get; }

        public Uri LatestUri => new Uri(_ep.Base, LatestPath);

        public Uri DownloadPage => new Uri(_ep.Base, InstallPagePath);

        /// <summary>The notice when the server announces a NEWER lotr-updater, else null. Throws only on cancellation.</summary>
        public async Task<LinuxLauncherNotice> CheckAsync(CancellationToken ct)
        {
            try
            {
                var bytes = await _fetch.GetBytesAsync(LatestUri, ct, allowNotFound: true, maxBytes: MaxBytes).ConfigureAwait(false);
                if (bytes == null)
                {
                    _log?.Write(LogLevel.Detail, "No Linux updater release published yet (linux/latest.json not found).");
                    return null;
                }

                var sig = await _fetch.GetBytesAsync(new Uri(LatestUri + ".sig"), ct, allowNotFound: true, maxBytes: MaxBytes).ConfigureAwait(false);
                string why = Evaluate(bytes, sig, out var latest);
                if (why != null)
                {
                    _log?.Write(LogLevel.Detail, "linux/latest.json ignored: " + why);
                    return null;
                }

                if (AppVersions.Compare(latest.Version, CurrentVersion) <= 0)
                {
                    _log?.Write(LogLevel.Detail, "lotr-updater is up to date (" + CurrentVersion + ").");
                    return null;
                }

                _log?.Write(LogLevel.Info, "A new lotr-updater is available: " + latest.Version + " (you have " + CurrentVersion + "), on " + DownloadPage + ".");
                return new LinuxLauncherNotice { Version = latest.Version, CurrentVersion = CurrentVersion, DownloadPage = DownloadPage.ToString(), Size = latest.Size };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _log?.Write(LogLevel.Detail, "Could not check for a newer lotr-updater: " + ex.Message);
                return null;
            }
        }

        /// <summary>Null when <paramref name="bytes"/> is a trustworthy Linux latest.json (then in <paramref name="latest"/>), else why not.</summary>
        public string Evaluate(byte[] bytes, byte[] sig, out UpdaterLatest latest)
        {
            latest = null;
            if (sig == null && _keys.Count > 0) return "not signed with the LotR team key";
            if (sig != null && _keys.Count > 0)
            {
                SignatureCheck c;
                try { c = ManifestSignature.Verify(bytes, sig, _keys); }
                catch (Exception ex) when (ex is FormatException || ex is System.Security.Cryptography.CryptographicException || ex is InvalidDataException)
                { c = new SignatureCheck { Valid = false, Message = ex.Message }; }
                if (!c.Valid) return "its signature does not match the LotR team key (" + c.Message + ")";
            }

            UpdaterLatest parsed;
            try { parsed = UpdaterLatest.Parse(bytes); }
            catch (FormatException ex) { return "not valid (" + ex.Message + ")"; }
            var val = parsed.Validate();
            if (!val.IsValid) return "not valid (" + string.Join("; ", val.Errors) + ")";
            if (!LinuxUpdaterArchive.IsOwnUrl(parsed.Url, parsed.Version)) return "it does not point at a lotr-updater archive on this server (" + parsed.Url + ")";
            if (parsed.Url.StartsWith("https://", StringComparison.Ordinal) && !string.Equals(new Uri(parsed.Url).Host, _ep.Base.Host, StringComparison.OrdinalIgnoreCase))
                return "it points at another server (" + parsed.Url + ")";
            latest = parsed;
            return null;
        }
    }
}
