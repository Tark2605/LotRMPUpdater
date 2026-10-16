using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Logging;

namespace Lotr.Updater.Core.Net
{
    /// <summary>The single server the updater talks to. Every URL it can ever call is built here
    /// (listed on the About screen: transparency).</summary>
    public sealed class ServerEndpoints
    {
        public ServerEndpoints(string baseUrl)
        {
            Base = new Uri(baseUrl.TrimEnd('/') + "/");
        }

        /// <summary>Placeholder used until the exe is built with -p:LotrBaseUrl=https://&lt;domain&gt;.</summary>
        public const string PlaceholderBase = "https://lotrmp.example";

        /// <summary>The production server, baked in at build time (assembly metadata "LotrBaseUrl").</summary>
        public static readonly string ProductionBase = ReadBakedBase() ?? PlaceholderBase;

        private static string ReadBakedBase()
        {
            foreach (var a in typeof(ServerEndpoints).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                var m = (System.Reflection.AssemblyMetadataAttribute)a;
                if (m.Key == "LotrBaseUrl" && !string.IsNullOrWhiteSpace(m.Value)) return m.Value.TrimEnd('/');
            }
            return null;
        }

        public Uri Base { get; }
        public string FilesPath { get; set; } = "files/releases/";
        public string QueuePath { get; set; } = "api/queue";
        public string SupportPath { get; set; } = "api/support";

        public Uri Channels => new Uri(Base, FilesPath + "channels.json");
        public Uri Manifest(string releaseId) => new Uri(Base, FilesPath + releaseId + "/manifest.json");
        public Uri Signature(string releaseId) => new Uri(Base, FilesPath + releaseId + "/manifest.json.sig");
        public Uri Object(string releaseId, string objectName) => new Uri(Base, FilesPath + releaseId + "/objects/" + objectName.Substring(0, 2) + "/" + objectName);
        public Uri QueueSlot => new Uri(Base, QueuePath + "/slot");
        public Uri QueueRelease => new Uri(Base, QueuePath + "/release");
        public Uri QueueHeartbeat => new Uri(Base, QueuePath + "/heartbeat");
        public Uri Security(string releaseId) => new Uri(Base, FilesPath + releaseId + "/security.json");
        public Uri Support => new Uri(Base, SupportPath);
        public Uri UpdaterLatest => new Uri(Base, "files/updater/latest.json");

        public string[] Describe() => new[]
        {
            "GET  " + Channels + "  (which release is current)",
            "GET  " + new Uri(Base, FilesPath) + "<release>/manifest.json and .sig  (file list + signature)",
            "GET  " + new Uri(Base, FilesPath) + "<release>/objects/...  (the mod files themselves)",
            "GET  " + new Uri(Base, FilesPath) + "<release>/security.json  (antivirus scan results, if published)",
            "POST " + QueueSlot + " , /heartbeat , /release  (only for the big Core download: a place in the download queue)",
            "POST " + Support + "  (only when you press \"Send to support\")",
            "GET  " + UpdaterLatest + "  (is there a newer updater)",
        };
    }

    public sealed class SignedManifest
    {
        public byte[] Bytes { get; set; }
        public byte[] Signature { get; set; }
    }

    public interface IReleaseSource
    {
        ServerEndpoints Endpoints { get; }
        /// <summary>channels.json → release id of the channel, null if the channel is empty.</summary>
        Task<string> GetCurrentReleaseIdAsync(string channel, CancellationToken ct);
        Task<SignedManifest> GetManifestAsync(string releaseId, CancellationToken ct);
        Uri ObjectUri(string releaseId, string objectName);
    }

    public sealed class HttpReleaseSource : IReleaseSource
    {
        private readonly HttpFetcher _fetch;

        public HttpReleaseSource(ServerEndpoints endpoints, HttpFetcher fetch)
        {
            Endpoints = endpoints;
            _fetch = fetch;
        }

        public ServerEndpoints Endpoints { get; }

        public async Task<string> GetCurrentReleaseIdAsync(string channel, CancellationToken ct)
        {
            var bytes = await _fetch.GetBytesAsync(Endpoints.Channels, ct, allowNotFound: true).ConfigureAwait(false);
            if (bytes == null) return null;
            return LotR.Releases.Model.ChannelIndex.Parse(bytes).Get(channel);
        }

        public async Task<SignedManifest> GetManifestAsync(string releaseId, CancellationToken ct)
        {
            var m = await _fetch.GetBytesAsync(Endpoints.Manifest(releaseId), ct).ConfigureAwait(false);
            var s = await _fetch.GetBytesAsync(Endpoints.Signature(releaseId), ct, allowNotFound: true).ConfigureAwait(false);
            return new SignedManifest { Bytes = m, Signature = s };
        }

        public Uri ObjectUri(string releaseId, string objectName) => Endpoints.Object(releaseId, objectName);
    }

    /// <summary>Core download queue (CONTRACTS §4): POST /api/queue/slot {kind:"core", client} → token or 429 {position, retryAfter}.</summary>
    public sealed class QueueSlotClient
    {
        private readonly HttpFetcher _fetch;
        private readonly ServerEndpoints _ep;
        private readonly IUpdaterLog _log;

        public QueueSlotClient(HttpFetcher fetch, ServerEndpoints ep, IUpdaterLog log)
        {
            _fetch = fetch;
            _ep = ep;
            _log = log;
        }

        /// <summary>Waits for a slot. Returns the token, or null when the server has no queue (404: advisory in v1).</summary>
        public async Task<string> AcquireAsync(string installId, Action<int, TimeSpan> onWaiting, CancellationToken ct)
        {
            var body = Json.Json.Write(new JsonObject { ["kind"] = "core", ["client"] = installId }, indent: false);
            for (int attempt = 1; ; attempt++)
            {
                int position = 0;
                TimeSpan retry = TimeSpan.Zero;
                bool queued = false, noQueue = false;
                string text;
                try
                {
                    text = await _fetch.PostJsonAsync(_ep.QueueSlot, body, ct, (resp, b) =>
                    {
                        if (resp.StatusCode == (HttpStatusCode)429)
                        {
                            queued = true;
                            try
                            {
                                var o = Json.Json.ParseObject(b);
                                position = (int)o.Long("position");
                                retry = TimeSpan.FromSeconds(Math.Max(1, o.Long("retryAfter", 15)));
                            }
                            catch (FormatException) { retry = RetryPolicy.RetryAfterOf(resp) ?? TimeSpan.FromSeconds(15); }
                            return Task.FromResult(true);
                        }
                        if (resp.StatusCode == HttpStatusCode.NotFound) { noQueue = true; return Task.FromResult(true); }
                        return Task.FromResult(false);
                    }).ConfigureAwait(false);
                }
                catch (HttpFetchException ex) when (attempt < 5)
                {
                    _log?.Write(LogLevel.Warn, "Download queue: " + ex.Message + "; retrying");
                    await _fetch.Retry.Delay(_fetch.Retry.Backoff(attempt, null), ct).ConfigureAwait(false);
                    continue;
                }
                catch (System.Net.Http.HttpRequestException ex) when (attempt < 5)
                {
                    _log?.Write(LogLevel.Warn, "Download queue unreachable: " + ex.Message + "; retrying");
                    await _fetch.Retry.Delay(_fetch.Retry.Backoff(attempt, null), ct).ConfigureAwait(false);
                    continue;
                }
                if (noQueue)
                {
                    _log?.Write(LogLevel.Detail, "No download queue on this server; downloading directly.");
                    return null;
                }
                if (!queued)
                {
                    var o = Json.Json.ParseObject(text);
                    HeartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, Math.Min(60, o.Long("heartbeatSec", 60))));
                    _log?.Write(LogLevel.Info, "Download slot granted (valid until " + o.Str("expiresAt") + ").");
                    return o.Str("token");
                }
                if (retry > TimeSpan.FromMinutes(5)) retry = TimeSpan.FromMinutes(5);
                onWaiting?.Invoke(position, retry);
                _log?.Write(LogLevel.Info, $"Many players are downloading the Core right now: you are number {position} in the queue. Next try in {retry.TotalSeconds:0} s.");
                await _fetch.Retry.Delay(retry, ct).ConfigureAwait(false);
            }
        }

        private TimeSpan _heartbeat = TimeSpan.FromSeconds(60);

        /// <summary>From the last grant (heartbeatSec, between 5 and 60 s; lotr-services answers TTL/3 = 60 s).</summary>
        public TimeSpan HeartbeatInterval
        {
            get => HeartbeatIntervalOverride ?? _heartbeat;
            private set => _heartbeat = value;
        }

        /// <summary>Tests and rehearsals with a very short slot TTL only (never set by the app).</summary>
        public TimeSpan? HeartbeatIntervalOverride { get; set; }

        /// <summary>Keeps the slot alive (CONTRACTS §8, 15/10). False when the server says the slot expired (410).</summary>
        public async Task<bool> HeartbeatAsync(string token, CancellationToken ct)
        {
            bool expired = false;
            try
            {
                var body = Json.Json.Write(new JsonObject { ["token"] = token }, indent: false);
                await _fetch.PostJsonAsync(_ep.QueueHeartbeat, body, ct, (r, b) =>
                {
                    if (r.StatusCode == HttpStatusCode.Gone) expired = true;
                    return Task.FromResult(true);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log?.Write(LogLevel.Detail, "Queue heartbeat failed: " + ex.Message); }
            return !expired;
        }

        public async Task ReleaseAsync(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            try
            {
                var body = Json.Json.Write(new JsonObject { ["token"] = token }, indent: false);
                await _fetch.PostJsonAsync(_ep.QueueRelease, body, CancellationToken.None, (r, b) => Task.FromResult(true)).ConfigureAwait(false);
            }
            catch (Exception ex) { _log?.Write(LogLevel.Detail, "Queue release failed (the slot expires by itself): " + ex.Message); }
        }
    }

    public sealed class SupportCode
    {
        public string Code { get; set; }
        public string ExpiresAt { get; set; }
    }

    /// <summary>POST /api/support (CONTRACTS §4). The log is masked BEFORE this is called. Answers as lotr-services sends
    /// them: 200 {code, expiresAt}, 429 {error:"rate_limited", retryAfter}, 4xx {error, message} (checked by the rehearsal,
    /// tools/rehearsal/Run-Rehearsal.ps1, against the real service).</summary>
    public sealed class SupportClient
    {
        private readonly HttpFetcher _fetch;
        private readonly ServerEndpoints _ep;

        public SupportClient(HttpFetcher fetch, ServerEndpoints ep)
        {
            _fetch = fetch;
            _ep = ep;
        }

        public const int MaxBytes = 2 * 1024 * 1024 - 4096;

        public async Task<SupportCode> SendAsync(string installId, string appVersion, string maskedLog, CancellationToken ct)
        {
            var log = maskedLog ?? "";
            while (Encoding.UTF8.GetByteCount(log) > MaxBytes) log = "(start cut)\n" + log.Substring(log.Length / 4);
            var body = Json.Json.Write(new JsonObject { ["installId"] = installId, ["appVersion"] = appVersion, ["log"] = log }, indent: false);
            string text = null;
            try
            {
                text = await _fetch.PostJsonAsync(_ep.Support, body, ct, (resp, b) =>
                {
                    if (resp.StatusCode == (HttpStatusCode)429)
                        throw new HttpFetchException("Too many support requests from this PC in the last hour. Please wait a little, or copy the log by hand into your ticket.", resp.StatusCode);
                    // lotr-services answers {"error": "<code>", "message": "<text>"} for refused uploads (400, 413, ...).
                    string detail = null;
                    try { var e = Json.Json.ParseObject(b); detail = e.Str("message") ?? e.Str("error"); } catch (FormatException) { }
                    if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500)
                        throw new HttpFetchException("The support service refused the log (" + (int)resp.StatusCode + (detail != null ? ": " + detail : "") + ").", resp.StatusCode);
                    return Task.FromResult(false);
                }).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                throw new HttpFetchException("Could not reach the support service: " + (ex.InnerException?.Message ?? ex.Message));
            }
            var o = Json.Json.ParseObject(text);
            return new SupportCode { Code = o.Str("code"), ExpiresAt = o.Str("expiresAt") };
        }
    }
}
