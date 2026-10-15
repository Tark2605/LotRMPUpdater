using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core.IO;
using Lotr.Updater.Core.Logging;

namespace Lotr.Updater.Core.Net
{
    /// <summary>Exponential backoff with jitter that honours Retry-After on 503/429 (PLAN: bandwidth).</summary>
    public sealed class RetryPolicy
    {
        private static readonly Random Rng = new Random();
        public int MaxAttempts { get; set; } = 8;
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>Upper bound for a server-requested Retry-After (protects against absurd values).</summary>
        public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(10);
        /// <summary>Injectable for tests.</summary>
        public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

        public TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter.HasValue)
                return retryAfter.Value > MaxRetryAfter ? MaxRetryAfter : (retryAfter.Value < TimeSpan.Zero ? TimeSpan.Zero : retryAfter.Value);
            double ms = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
            ms = Math.Min(ms, MaxDelay.TotalMilliseconds);
            double jitter;
            lock (Rng) jitter = Rng.NextDouble();
            return TimeSpan.FromMilliseconds(ms * (0.5 + jitter * 0.5));
        }

        public static TimeSpan? RetryAfterOf(HttpResponseMessage resp)
        {
            var ra = resp.Headers.RetryAfter;
            if (ra == null) return null;
            if (ra.Delta.HasValue) return ra.Delta.Value;
            if (ra.Date.HasValue) return ra.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }
    }

    public sealed class HttpFetchException : Exception
    {
        public HttpFetchException(string message, HttpStatusCode? status = null, Exception inner = null) : base(message, inner) { Status = status; }
        public HttpStatusCode? Status { get; }
    }

    /// <summary>Plain HTTP GETs with retries, and resumable range downloads into a .part file.</summary>
    public sealed class HttpFetcher
    {
        private readonly HttpClient _http;
        private readonly IFileSystem _fs;
        private readonly IUpdaterLog _log;

        public HttpFetcher(HttpClient http, IFileSystem fs, IUpdaterLog log, RetryPolicy retry = null)
        {
            _http = http;
            _fs = fs;
            _log = log;
            Retry = retry ?? new RetryPolicy();
        }

        public RetryPolicy Retry { get; }
        public HttpClient Client => _http;

        public static HttpClient CreateClient(string appVersion)
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseCookies = false };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            // The only identifying data ever sent: the app name and version (no telemetry).
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LotRMPUpdater/" + appVersion);
            return c;
        }

        /// <summary>GET a small resource (channels.json, manifest, signature, chunk index). 404 returns null when allowed.</summary>
        public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken ct, bool allowNotFound = false, long maxBytes = 64L << 20)
        {
            for (int attempt = 1; ; attempt++)
            {
                TimeSpan? wait = null;
                string why;
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(TimeSpan.FromSeconds(60));
                        using (var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            if (resp.IsSuccessStatusCode)
                            {
                                var len = resp.Content.Headers.ContentLength;
                                if (len.HasValue && len.Value > maxBytes) throw new HttpFetchException("Response too large from " + uri);
                                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                            }
                            if (resp.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
                            if (!IsTransient(resp.StatusCode))
                                throw new HttpFetchException($"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase} for {uri.AbsolutePath}", resp.StatusCode);
                            wait = RetryPolicy.RetryAfterOf(resp);
                            why = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                        }
                    }
                }
                catch (HttpRequestException ex) { why = ex.InnerException?.Message ?? ex.Message; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { why = "timeout"; }
                catch (IOException ex) { why = ex.Message; }

                if (attempt >= Retry.MaxAttempts) throw new HttpFetchException($"Could not reach the server after {attempt} tries ({why}): {uri.AbsolutePath}");
                var d = Retry.Backoff(attempt, wait);
                _log?.Write(LogLevel.Warn, $"{uri.AbsolutePath}: {why}; retrying in {d.TotalSeconds:0.#} s (try {attempt + 1}/{Retry.MaxAttempts})");
                await Retry.Delay(d, ct).ConfigureAwait(false);
            }
        }

        public async Task<string> PostJsonAsync(Uri uri, string json, CancellationToken ct, Func<HttpResponseMessage, string, Task<bool>> handleNonSuccess = null)
        {
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var resp = await _http.PostAsync(uri, content, ct).ConfigureAwait(false))
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return body;
                if (handleNonSuccess != null && await handleNonSuccess(resp, body).ConfigureAwait(false)) return null;
                throw new HttpFetchException($"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase}", resp.StatusCode);
            }
        }

        /// <summary>
        /// Downloads <paramref name="uri"/> into <paramref name="partPath"/>, resuming from the bytes already there with a Range request.
        /// Returns when the file has exactly <paramref name="expectedLength"/> bytes. Transient errors (network cut, 5xx, 429)
        /// are retried with backoff; a 200 answer to a range request restarts from zero.
        /// </summary>
        public async Task DownloadResumableAsync(Uri uri, string partPath, long expectedLength, Action<long> progress, CancellationToken ct)
        {
            int failures = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                long have = _fs.FileExists(partPath) ? _fs.GetFileLength(partPath) : 0;
                if (have > expectedLength) { _fs.DeleteFile(partPath); have = 0; }
                if (have == expectedLength) return;

                TimeSpan? wait = null;
                string why;
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                    using (req)
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        if (resp.StatusCode == HttpStatusCode.OK || resp.StatusCode == HttpStatusCode.PartialContent)
                        {
                            bool append = false;
                            if (resp.StatusCode == HttpStatusCode.PartialContent)
                            {
                                var cr = resp.Content.Headers.ContentRange;
                                if (cr != null && cr.From == have) append = true;
                            }
                            if (have > 0 && !append)
                            {
                                progress?.Invoke(-have); // server ignored the range: start over
                                have = 0;
                            }
                            using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var outp = _fs.OpenWrite(partPath, append))
                            {
                                var buf = new byte[1 << 17];
                                while (true)
                                {
                                    var readTask = body.ReadAsync(buf, 0, buf.Length, ct);
                                    // A stalled connection is treated as a cut (resumed later), not as an endless wait.
                                    var done = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(45), ct)).ConfigureAwait(false);
                                    if (done != readTask) throw new IOException("connection stalled");
                                    int n = await readTask.ConfigureAwait(false);
                                    if (n == 0) break;
                                    if (have + n > expectedLength) throw new HttpFetchException("The server sent more data than expected for " + uri.AbsolutePath);
                                    outp.Write(buf, 0, n);
                                    have += n;
                                    progress?.Invoke(n);
                                }
                            }
                            if (have == expectedLength) return;
                            why = $"connection closed at {have}/{expectedLength} bytes";
                        }
                        else if (resp.StatusCode == (HttpStatusCode)416)
                        {
                            progress?.Invoke(-have);
                            _fs.DeleteFile(partPath);
                            why = "range not satisfiable, restarting";
                        }
                        else if (IsTransient(resp.StatusCode))
                        {
                            wait = RetryPolicy.RetryAfterOf(resp);
                            why = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                        }
                        else
                        {
                            throw new HttpFetchException($"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase} for {uri.AbsolutePath}", resp.StatusCode);
                        }
                    }
                }
                catch (HttpRequestException ex) { why = ex.InnerException?.Message ?? ex.Message; }
                catch (IOException ex) { why = ex.Message; }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { why = "timeout"; }
                catch (ObjectDisposedException ex) { why = ex.Message; }

                failures++;
                if (failures >= Retry.MaxAttempts) throw new HttpFetchException($"Download kept failing ({why}): {uri.AbsolutePath}. Check your connection and try again; what was already downloaded is kept.");
                var d = Retry.Backoff(failures, wait);
                _log?.Write(LogLevel.Warn, $"{Path.GetFileName(partPath)}: {why}; resuming in {d.TotalSeconds:0.#} s");
                await Retry.Delay(d, ct).ConfigureAwait(false);
            }
        }

        private static bool IsTransient(HttpStatusCode s) =>
            s == (HttpStatusCode)429 || s == HttpStatusCode.ServiceUnavailable || s == HttpStatusCode.BadGateway
            || s == HttpStatusCode.GatewayTimeout || s == HttpStatusCode.InternalServerError || s == HttpStatusCode.RequestTimeout;

        internal static string Fmt(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
