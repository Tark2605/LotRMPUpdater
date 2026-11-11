using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Updater.Testing
{
    /// <summary>What the server should do to one request (fault injection).</summary>
    public sealed class Fault
    {
        public int Status { get; set; }
        public int? RetryAfterSeconds { get; set; }
        /// <summary>Send the headers and this many body bytes, then cut the connection.</summary>
        public long? CutAfterBytes { get; set; }
        /// <summary>Flip one byte of the body.</summary>
        public bool Corrupt { get; set; }
        /// <summary>Ignore the Range header (answer 200 with the full body).</summary>
        public bool IgnoreRange { get; set; }
    }

    /// <summary>
    /// Tiny static server on http://localhost:&lt;port&gt;/ (HttpListener, no admin needed for localhost) that serves
    /// &lt;root&gt; at /files/, honours Range requests, and stubs /api/queue and /api/support (CONTRACTS §4).
    /// </summary>
    public sealed class TestHttpServer : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly string _root;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, int> _hits = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        public TestHttpServer(string filesRoot, int port = 0)
        {
            _root = Path.GetFullPath(filesRoot);
            Port = port == 0 ? FreePort() : port;
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            Task.Run(Loop);
        }

        public int Port { get; }
        public string BaseUrl => $"http://localhost:{Port}/";

        /// <summary>(path, hit number starting at 1) → fault or null.</summary>
        public Func<string, int, Fault> Faults { get; set; }

        /// <summary>Queue: number of 429 answers before granting a slot.</summary>
        public int QueueBusyAnswers { get; set; }
        public int QueueSlotsGranted;
        public int QueueSlotsReleased;
        public int Heartbeats;
        /// <summary>When set, slots behave like lotr-services: a slot dies this long after its grant or last heartbeat
        /// (heartbeat on a dead slot = 410), and <see cref="EnforceSlots"/> refuses Core objects without a live slot.</summary>
        public TimeSpan? SlotTtl { get; set; }
        public bool EnforceSlots { get; set; }
        public int ForbiddenObjectRequests;
        public int ExpiredHeartbeats;
        private readonly ConcurrentDictionary<string, DateTime> _slotSeen = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        public int LiveSlots => _slotSeen.Count(kv => !SlotExpired(kv.Key));

        public bool SlotExpired(string token) =>
            !_slotSeen.TryGetValue(token ?? "", out var seen) || (SlotTtl.HasValue && DateTime.UtcNow - seen > SlotTtl.Value);

        /// <summary>Kills a slot now (as if the server restarted or the TTL passed).</summary>
        public void ExpireAllSlots() => _slotSeen.Clear();
        /// <summary>0 = full speed.</summary>
        public int ThrottleBytesPerSecond { get; set; }
        public string LastSupportBody { get; private set; }
        public long BytesServed;
        public Action<string> OnRequest { get; set; }
        public List<string> RangeRequests { get; } = new List<string>();

        public int Hits(string path) => _hits.TryGetValue(path, out var n) ? n : 0;

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath;
            // The site's news feed and the pause notice live at the site root in production; here in <root>\site\.
            var servedPath = path == "/hub.json" ? "/files/site/hub.json" : path == "/status.json" ? "/files/site/status.json" : path;
            int hit = _hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            OnRequest?.Invoke(ctx.Request.HttpMethod + " " + ctx.Request.Url.PathAndQuery);
            try
            {
                var fault = Faults?.Invoke(path, hit);
                if (fault != null && fault.Status != 0)
                {
                    ctx.Response.StatusCode = fault.Status;
                    if (fault.RetryAfterSeconds.HasValue) ctx.Response.AddHeader("Retry-After", fault.RetryAfterSeconds.Value.ToString());
                    ctx.Response.Close();
                    return;
                }
                if (path.StartsWith("/api/", StringComparison.Ordinal)) { Api(ctx, path); return; }
                if (!servedPath.StartsWith("/files/", StringComparison.Ordinal)) { NotFound(ctx); return; }
                var slotToken = ctx.Request.QueryString["slot"];
                if (EnforceSlots && slotToken != null && SlotExpired(slotToken))
                {
                    Interlocked.Increment(ref ForbiddenObjectRequests);
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                var rel = Uri.UnescapeDataString(servedPath.Substring("/files/".Length)).Replace('/', Path.DirectorySeparatorChar);
                var file = Path.GetFullPath(Path.Combine(_root, rel));
                if (!file.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { NotFound(ctx); return; }
                var data = File.ReadAllBytes(file);
                if (fault?.Corrupt == true && data.Length > 0) { data = (byte[])data.Clone(); data[data.Length / 2] ^= 0x5A; }
                long start = 0;
                var range = ctx.Request.Headers["Range"];
                ctx.Response.AddHeader("Accept-Ranges", "bytes");
                if (range != null && fault?.IgnoreRange != true && range.StartsWith("bytes="))
                {
                    lock (RangeRequests) RangeRequests.Add(path + " " + range);
                    start = long.Parse(range.Substring(6).Split('-')[0]);
                    if (start >= data.Length) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {start}-{data.Length - 1}/{data.Length}");
                }
                else ctx.Response.StatusCode = 200;
                long len = data.Length - start;
                ctx.Response.ContentLength64 = len;
                ctx.Response.ContentType = "application/octet-stream";
                var outp = ctx.Response.OutputStream;
                long toSend = fault?.CutAfterBytes != null ? Math.Min(len, fault.CutAfterBytes.Value) : len;
                if (ThrottleBytesPerSecond > 0)
                {
                    // Dev runs: slow the transfer down so the progress screen can be seen.
                    int slice = Math.Max(4096, ThrottleBytesPerSecond / 20);
                    for (long sent = 0; sent < toSend; sent += slice)
                    {
                        int n = (int)Math.Min(slice, toSend - sent);
                        outp.Write(data, (int)(start + sent), n);
                        Thread.Sleep(50);
                    }
                }
                else outp.Write(data, (int)start, (int)toSend);
                Interlocked.Add(ref BytesServed, toSend);
                if (toSend < len) { outp.Flush(); Thread.Sleep(300); ctx.Response.Abort(); return; } // let the bytes reach the client before the reset
                ctx.Response.Close();
            }
            catch (Exception)
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private int _queueAsked;

        private void Api(HttpListenerContext ctx, string path)
        {
            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
            if (path == "/api/queue/slot")
            {
                int n = Interlocked.Increment(ref _queueAsked);
                if (n <= QueueBusyAnswers) { Json(ctx, 429, "{\"position\":" + (QueueBusyAnswers - n + 1) + ",\"retryAfter\":1}"); return; }
                Interlocked.Increment(ref QueueSlotsGranted);
                _slotSeen["tok-" + n] = DateTime.UtcNow;
                var ttl = SlotTtl ?? TimeSpan.FromMinutes(30);
                Json(ctx, 200, "{\"token\":\"tok-" + n + "\",\"expiresAt\":\"" + DateTime.UtcNow.Add(ttl).ToString("o") + "\",\"heartbeatSec\":" + Math.Max(1, (int)(ttl.TotalSeconds / 3)) + "}");
                return;
            }
            if (path == "/api/queue/heartbeat")
            {
                Interlocked.Increment(ref Heartbeats);
                var token = TokenOf(body);
                if (SlotExpired(token)) { _slotSeen.TryRemove(token ?? "", out _); Interlocked.Increment(ref ExpiredHeartbeats); Json(ctx, 410, "{\"error\":\"slot_expired\"}"); return; }
                _slotSeen[token] = DateTime.UtcNow;
                Json(ctx, 200, "{\"expiresAt\":\"" + DateTime.UtcNow.Add(SlotTtl ?? TimeSpan.FromMinutes(30)).ToString("o") + "\"}");
                return;
            }
            if (path == "/api/queue/release") { Interlocked.Increment(ref QueueSlotsReleased); _slotSeen.TryRemove(TokenOf(body) ?? "", out _); Json(ctx, 200, "{}"); return; }
            if (path == "/api/support")
            {
                LastSupportBody = body;
                Json(ctx, 200, "{\"code\":\"LOTR-7F3K\",\"expiresAt\":\"" + DateTime.UtcNow.AddHours(72).ToString("o") + "\"}");
                return;
            }
            NotFound(ctx);
        }

        private static string TokenOf(string body)
        {
            var m = System.Text.RegularExpressions.Regex.Match(body ?? "", @"""token""\s*:\s*""([^""]*)""");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static void Json(HttpListenerContext ctx, int status, string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.Close();
        }

        private static void NotFound(HttpListenerContext ctx)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
