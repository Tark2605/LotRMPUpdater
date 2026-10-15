using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lotr.Security.Tests;

/// <summary>Never sleeps: DelayAsync just moves time forward and records the wait.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    public readonly List<TimeSpan> Waits = new List<TimeSpan>();
    public TimeSpan TotalWaited => TimeSpan.FromTicks(Waits.Sum(w => w.Ticks));

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero) { Waits.Add(delay); UtcNow += delay; }
        return Task.CompletedTask;
    }
}

public sealed class RecordedRequest
{
    public HttpMethod Method;
    public Uri Uri;
    public Dictionary<string, string> Headers;
    public string ContentType;
    public long ContentLength;
    public string ContentText; // only for small bodies
    public DateTimeOffset At;
    public override string ToString() => Method + " " + Uri;
}

/// <summary>
/// Scripted HTTP server. Routes are matched in order on "METHOD url-regex"; each route has a queue of responses,
/// the last one repeats. Every request is recorded (headers + body size) so tests can assert what was sent.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private sealed class Route
    {
        public string Method;
        public Regex Url;
        public Queue<Func<HttpResponseMessage>> Responses = new Queue<Func<HttpResponseMessage>>();
        public Func<HttpResponseMessage> Last;
    }

    private readonly List<Route> _routes = new List<Route>();
    public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();
    public FakeClock Clock;
    public readonly Dictionary<string, string> Vars = new Dictionary<string, string>();

    public static string FixturesDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures");

    public FakeHttp On(string method, string urlRegex, params Func<HttpResponseMessage>[] responses)
    {
        var r = new Route { Method = method, Url = new Regex(urlRegex, RegexOptions.IgnoreCase) };
        foreach (var f in responses) r.Responses.Enqueue(f);
        _routes.Add(r);
        return this;
    }

    public Func<HttpResponseMessage> Fixture(int status, string relPath, params (string name, string value)[] headers) => () =>
    {
        string text = File.ReadAllText(Path.Combine(FixturesDir, relPath), Encoding.UTF8);
        foreach (var kv in Vars) text = text.Replace("{" + kv.Key + "}", kv.Value);
        var resp = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        foreach (var h in headers) resp.Headers.TryAddWithoutValidation(h.name, h.value);
        return resp;
    };

    public static Func<HttpResponseMessage> Text(int status, string body) => () =>
        new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body ?? "") };

    public static Func<HttpResponseMessage> Throw(Exception ex) => () => throw ex;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var rec = new RecordedRequest
        {
            Method = request.Method,
            Uri = request.RequestUri,
            // User-Agent is a list of product tokens joined by spaces on the wire.
            Headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(h.Key == "User-Agent" ? " " : ",", h.Value), StringComparer.OrdinalIgnoreCase),
            At = Clock?.UtcNow ?? DateTimeOffset.MinValue,
        };
        if (request.Content != null)
        {
            byte[] body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            rec.ContentLength = body.Length;
            rec.ContentType = request.Content.Headers.ContentType?.MediaType;
            if (body.Length < 64 * 1024) rec.ContentText = Encoding.UTF8.GetString(body);
        }
        Requests.Add(rec);

        string url = request.RequestUri.ToString();
        foreach (Route r in _routes)
        {
            if (!string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase) || !r.Url.IsMatch(url)) continue;
            Func<HttpResponseMessage> f = r.Responses.Count > 0 ? (r.Last = r.Responses.Dequeue()) : r.Last;
            if (f == null) break;
            return f();
        }
        throw new InvalidOperationException("FakeHttp: no route for " + request.Method + " " + url);
    }
}

/// <summary>Temp folder per test, removed afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lotr-security-tests", Guid.NewGuid().ToString("N"));
    public TempDir() { Directory.CreateDirectory(Path); }
    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public string WriteFile(string rel, int size, int seed = 1)
    {
        string p = Combine(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
        var rnd = new Random(seed);
        var buf = new byte[size];
        rnd.NextBytes(buf);
        File.WriteAllBytes(p, buf);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
