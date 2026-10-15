using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security;

internal sealed class ApiResponse
{
    public int Status;
    public string Body;
    public JsonDocument Doc;
    public JsonElement Root => Doc != null ? Doc.RootElement : default;
    public bool IsSuccess => Status >= 200 && Status < 300;
    public string ErrorCode;
    public string ErrorMessage;
    public TimeSpan? RetryAfterHint;

    public string Describe() =>
        $"HTTP {Status}" + (ErrorCode != null ? " " + ErrorCode : "") +
        (ErrorMessage != null ? ": " + ErrorMessage : (Body != null && Doc == null ? ": " + Trim(Body) : ""));

    private static string Trim(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;
}

/// <summary>
/// One HTTP call = one quota unit. Handles the policy every service shares:
/// 401/403 abort the run, 429 backs off (then counts as "daily quota spent" if it keeps happening),
/// 5xx / network errors are retried with exponential backoff, anything else is returned to the caller.
/// </summary>
internal sealed class ApiTransport
{
    private readonly HttpClient _http;
    private readonly RateLimiter _limiter;
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly string _service;

    public int MaxTransientRetries = 3;
    public int Max429Retries = 2;
    public TimeSpan Default429BackOff = TimeSpan.FromSeconds(60);
    public TimeSpan TransientBaseDelay = TimeSpan.FromSeconds(5);

    public ApiTransport(string service, HttpClient http, RateLimiter limiter, IClock clock, ILog log)
    {
        _service = service; _http = http; _limiter = limiter; _clock = clock; _log = log;
    }

    public async Task<ApiResponse> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        int transient = 0, tooMany = 0;
        while (true)
        {
            await _limiter.AcquireAsync(ct).ConfigureAwait(false);
            ApiResponse r;
            string what;
            using (HttpRequestMessage req = makeRequest())
            {
                what = req.Method + " " + req.RequestUri.AbsolutePath;
                try
                {
                    using (HttpResponseMessage resp = await _http.SendAsync(req, ct).ConfigureAwait(false))
                    {
                        r = new ApiResponse { Status = (int)resp.StatusCode };
                        r.Body = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
                        ParseBody(r);
                        if (r.Status == 429 && resp.Headers.RetryAfter?.Delta is TimeSpan delta) r.RetryAfterHint = delta;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException || ex is System.IO.IOException))
                {
                    if (++transient > MaxTransientRetries)
                        return new ApiResponse { Status = 0, ErrorCode = "NetworkError", ErrorMessage = ex.Message };
                    TimeSpan wait = Backoff(transient);
                    _log.Warn($"{_service}: {what} failed ({ex.GetType().Name}: {ex.Message}); retry {transient}/{MaxTransientRetries} in {wait.TotalSeconds:0} s");
                    await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }

            if (r.Status == 401 || r.Status == 403)
                throw new ApiAuthException($"{_service} rejected the API key ({r.Describe()}). Check the key in the secrets file / environment variable (docs/SECURITY-AUTOMATION.md).");

            if (r.Status == 429)
            {
                if (++tooMany > Max429Retries)
                {
                    _limiter.MarkDayExhausted();
                    throw new QuotaExhaustedException($"{_service}: the API keeps answering 429 ({r.Describe()}); treating the daily quota as spent. Run again later, progress is saved.");
                }
                TimeSpan wait = r.RetryAfterHint ?? Default429BackOff;
                _log.Warn($"{_service}: {what} -> 429 ({r.ErrorCode ?? "rate limited"}); waiting {wait.TotalSeconds:0} s");
                _limiter.BackOff(wait);
                continue;
            }

            if (r.Status >= 500 && r.Status <= 599)
            {
                if (++transient > MaxTransientRetries) return r;
                TimeSpan wait = Backoff(transient);
                _log.Warn($"{_service}: {what} -> {r.Describe()}; retry {transient}/{MaxTransientRetries} in {wait.TotalSeconds:0} s");
                await _clock.DelayAsync(wait, ct).ConfigureAwait(false);
                continue;
            }

            return r;
        }
    }

    private TimeSpan Backoff(int attempt) => TimeSpan.FromTicks(TransientBaseDelay.Ticks * (long)Math.Pow(3, attempt - 1));

    private static void ParseBody(ApiResponse r)
    {
        if (string.IsNullOrWhiteSpace(r.Body)) return;
        string t = r.Body.TrimStart();
        if (!(t.StartsWith("{") || t.StartsWith("[") || t.StartsWith("\""))) return;
        try { r.Doc = JsonDocument.Parse(r.Body); }
        catch (JsonException) { return; }
        JsonElement root = r.Doc.RootElement;
        // VirusTotal: { "error": { "code", "message" } }   Hybrid Analysis: { "message": "...", "validation_errors": [...] }
        if (root.TryGet("error", out JsonElement err) && err.ValueKind == JsonValueKind.Object)
        {
            r.ErrorCode = err.Str("code");
            r.ErrorMessage = err.Str("message");
        }
        else if (r.Status >= 400 && root.ValueKind == JsonValueKind.Object)
        {
            r.ErrorMessage = root.Str("message");
        }
    }
}
