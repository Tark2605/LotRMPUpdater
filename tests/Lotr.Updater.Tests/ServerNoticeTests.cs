using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core;
using Lotr.Updater.Core.News;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    public sealed class ServerNoticeTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lotr-notice-tests", Guid.NewGuid().ToString("N").Substring(0, 10));
        private const string Base = "https://lotr.test";

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        // As Set-EventNightMode.ps1 writes it (site deploy package).
        private static string Status(string until, string url) =>
            "{\"schema\":1,\"state\":\"event-lock\",\"since\":\"2026-11-17T18:45:00Z\",\"until\":" + (until == null ? "null" : "\"" + until + "\"") +
            ",\"title\":\"The battle has begun\",\"message\":\"Downloads are paused.\\nAlready up to date? Play.\",\"installUrl\":" + (url == null ? "null" : "\"" + url + "\"") + "}";

        [Fact]
        public void Reads_the_event_lock_notice()
        {
            var until = DateTime.UtcNow.AddHours(2);
            var n = ServerNotice.Parse(B(Status(until.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), "https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa")), Base);
            Assert.NotNull(n);
            Assert.Equal("The battle has begun", n.Title);
            Assert.Contains("Already up to date? Play.", n.Message);
            Assert.Equal("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa", n.InstallUrl);
            Assert.Equal(DateTimeKind.Utc, n.UntilUtc.Value.Kind);
            Assert.True(Math.Abs((n.UntilUtc.Value - until).TotalSeconds) < 1);
            Assert.StartsWith("Back after the battle, around ", n.UntilText());
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ broken")]
        [InlineData("{\"schema\":1,\"state\":\"open\"}")]
        [InlineData("{\"schema\":2,\"state\":\"event-lock\"}")]
        [InlineData("[1]")]
        public void Anything_else_is_no_notice(string json)
        {
            Assert.Null(ServerNotice.Parse(B(json), Base));
        }

        [Fact]
        public void Missing_texts_get_defaults_and_odd_end_times_are_dropped()
        {
            var n = ServerNotice.Parse(B("{\"schema\":1,\"state\":\"event-lock\",\"until\":\"2030-01-01T00:00:00Z\"}"), Base);
            Assert.NotNull(n);
            Assert.False(string.IsNullOrEmpty(n.Title));
            Assert.False(string.IsNullOrEmpty(n.Message));
            Assert.Null(n.UntilUtc);         // days away: a typo, not a battle night
            Assert.Null(n.InstallUrl);
            Assert.Null(n.UntilText());
        }

        [Theory]
        [InlineData("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa", true)]
        [InlineData("https://DRIVE.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa/?usp=sharing", true)]
        [InlineData("https://drive.google.com/drive/folders/someone-elses-folder", false)]   // only the team's folder
        [InlineData("https://drive.google.com/file/d/1abc/view", false)]                     // any uploaded file: no
        [InlineData("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa/../x", false)]
        [InlineData("https://lotr.test/install/", true)]          // the LotR server itself
        [InlineData("http://drive.google.com/x", false)]          // https only
        [InlineData("https://drive.google.com.evil.test/x", false)]
        [InlineData("https://evil.test/x", false)]
        [InlineData("https://user@drive.google.com/x", false)]
        [InlineData("https://drive.google.com:8443/x", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("file:///C:/Windows/notepad.exe", false)]
        public void Links_are_restricted(string url, bool kept)
        {
            Assert.Equal(kept, ServerNotice.SafeLink(url, Base) != null);
        }

        [Fact]
        public void Drive_links_are_rebuilt_without_query()
        {
            Assert.Equal("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa",
                ServerNotice.SafeLink("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa?usp=sharing#x", Base));
        }

        [Fact]
        public void Texts_are_cleaned()
        {
            var n = ServerNotice.Parse(B("{\"schema\":1,\"state\":\"event-lock\",\"title\":\"Bat\\u202Etle\\u0000\",\"message\":\"a\\u200Bb\"}"), Base);
            Assert.Equal("Battle", n.Title);
            Assert.Equal("ab", n.Message);
        }

        [Fact]
        public async Task A_response_without_length_is_cut_at_the_limit()
        {
            // Chunked (or compressed) answers carry no Content-Length: the limit must count the bytes actually read,
            // or a small compressed file on the server could make the updater allocate gigabytes.
            int port;
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            using (var l = new HttpListener())
            {
                l.Prefixes.Add($"http://localhost:{port}/");
                l.Start();
                var serve = Task.Run(async () =>
                {
                    var ctx = await l.GetContextAsync();
                    ctx.Response.SendChunked = true;
                    var block = new byte[16 * 1024];
                    try { for (int i = 0; i < 64; i++) await ctx.Response.OutputStream.WriteAsync(block, 0, block.Length); ctx.Response.Close(); }
                    catch (HttpListenerException) { } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
                });
                using (var http = new HttpClient())
                {
                    var f = new Lotr.Updater.Core.Net.HttpFetcher(http, null, null);
                    var ex = await Assert.ThrowsAsync<Lotr.Updater.Core.Net.HttpFetchException>(() => f.GetBytesAsync(new Uri($"http://localhost:{port}/status.json"), CancellationToken.None, maxBytes: 64 * 1024));
                    Assert.Contains("too large", ex.Message);
                }
                l.Stop();
                await Task.WhenAny(serve, Task.Delay(2000));
            }
        }

        [Fact]
        public async Task Service_reads_status_json_and_survives_its_absence()
        {
            var files = Path.Combine(_dir, "files");
            Directory.CreateDirectory(Path.Combine(files, "site"));
            using (var srv = new TestHttpServer(files))
            {
                var svc = new UpdaterService(new UpdaterConfig { BaseUrl = srv.BaseUrl, Dev = true, AppVersion = "test" }, new Lotr.Updater.Core.Model.UpdaterPaths(Path.Combine(_dir, "home")));
                Assert.Null(await svc.FetchServerNoticeAsync(CancellationToken.None)); // 404 in normal times: no notice
                Assert.Equal(1, srv.Hits("/status.json"));                            // one try only

                File.WriteAllText(Path.Combine(files, "site", "status.json"), Status(null, "https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa"));
                var n = await svc.FetchServerNoticeAsync(CancellationToken.None);
                Assert.NotNull(n);
                Assert.Equal("https://drive.google.com/drive/folders/1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa", n.InstallUrl);
            }
        }
    }
}
