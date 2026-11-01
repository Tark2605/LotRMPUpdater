using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lotr.Updater.Core;
using Lotr.Updater.Core.News;
using Lotr.Updater.Testing;
using Xunit;

namespace Lotr.Updater.Tests
{
    public sealed class SiteNewsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lotr-sitenews-tests", Guid.NewGuid().ToString("N").Substring(0, 10));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        // Shape of the site's /hub.json (site/src/pages/hub.json.ts), trimmed to what the updater reads.
        private const string Hub = @"{
  ""schema"": 1,
  ""version"": { ""event"": ""1.0.2.5"" },
  ""news"": [
    { ""title"": ""Spears that hold the line"", ""date"": ""2026-10-25"", ""version"": ""1.0.2.5"",
      ""summary"": ""Spears and lances now thrust only."", ""text"": ""A short balance update.\n\nSpears and lances\n\n• Thrust only."", ""url"": ""https://x/"" },
    { ""title"": ""Wargs for warg riders"", ""date"": ""2026-10-21"", ""version"": ""1.0.2.4"", ""summary"": ""Wargs."", ""text"": null },
    { ""title"": ""Server notice"", ""date"": ""2026-10-22"", ""version"": null, ""summary"": ""Not a release."", ""text"": ""Maintenance."" },
    { ""title"": ""The Harad arsenal"", ""date"": ""2026-10-16"", ""version"": ""1.0.2.3"", ""summary"": ""Harad."", ""text"": ""Harad text."" }
  ]
}";

        private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

        [Fact]
        public void Picks_the_chronicle_of_the_event_version_with_or_without_v()
        {
            var items = SiteNews.Parse(B(Hub));
            Assert.Equal(4, items.Count);
            var i = SiteNews.For(items, "v1.0.2.5");
            Assert.NotNull(i);
            Assert.Equal("1.0.2.5 · Spears that hold the line", SiteNews.Heading(i));
            Assert.StartsWith("A short balance update.", SiteNews.Body(i));
            Assert.Contains("• Thrust only.", SiteNews.Body(i));
            Assert.Same(i, SiteNews.For(items, "1.0.2.5"));
        }

        [Fact]
        public void Summary_stands_in_when_there_is_no_text_and_unknown_versions_give_null()
        {
            var items = SiteNews.Parse(B(Hub));
            Assert.Equal("Wargs.", SiteNews.Body(SiteNews.For(items, "v1.0.2.4")));
            Assert.Null(SiteNews.For(items, "v1.0.2.6"));
            Assert.Null(SiteNews.For(items, null));
            Assert.Null(SiteNews.For(null, "v1.0.2.5"));
        }

        [Fact]
        public void Previous_lists_earlier_releases_only_newest_first()
        {
            var items = SiteNews.Parse(B(Hub));
            var prev = SiteNews.Previous(items, SiteNews.For(items, "1.0.2.5"));
            Assert.Equal(new[] { "1.0.2.4 · Wargs for warg riders", "1.0.2.3 · The Harad arsenal" }, prev.Select(p => p.Title).ToArray());
            Assert.Equal("16 October 2026", prev[1].DateLabel);
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ broken")]
        [InlineData("[1,2]")]
        [InlineData("{\"schema\":2,\"news\":[{\"title\":\"x\",\"version\":\"1.0.2.5\",\"text\":\"y\"}]}")]
        [InlineData("{\"schema\":1}")]
        public void Anything_unexpected_gives_an_empty_list(string json)
        {
            Assert.Empty(SiteNews.Parse(B(json)));
        }

        [Fact]
        public void Text_is_cleaned_and_odd_versions_are_ignored()
        {
            var json = "{\"schema\":1,\"news\":[" +
                       "{\"title\":\"Bad\\u202Eone\",\"version\":\"1.0.2.5\",\"text\":\"a\\u0000b\\u200Bc\"}," +
                       "{\"title\":\"Weird\",\"version\":\"1.0.2.5; rm\",\"text\":\"t\"}," +
                       "{\"title\":\"\",\"version\":\"1.0.2.7\",\"text\":\"no title\"}]}";
            var items = SiteNews.Parse(B(json));
            Assert.Equal(2, items.Count);
            Assert.Equal("Badone", items[0].Title);
            Assert.Equal("abc", items[0].Text);
            Assert.Null(items[1].Version);
            Assert.Null(SiteNews.For(items, "1.0.2.7"));
        }

        [Fact]
        public void Too_large_is_refused()
        {
            var big = "{\"schema\":1,\"news\":[{\"title\":\"x\",\"version\":\"1.0.2.5\",\"text\":\"" + new string('a', (int)SiteNews.MaxBytes) + "\"}]}";
            Assert.Empty(SiteNews.Parse(B(big)));
        }

        // schedule.musters as the site writes them (site/src/lib/schedule.ts upcomingMusters), plus entries it must ignore.
        private const string HubWithMusters = @"{
  ""schema"": 1,
  ""schedule"": { ""confirmed"": true, ""musters"": [
    { ""start"": ""2026-11-10T19:00:00.000Z"", ""end"": ""2026-11-10T21:30:00.000Z"" },
    { ""start"": ""2026-11-03T19:00:00.000Z"", ""end"": ""2026-11-03T21:30:00.000Z"", ""map"": ""scn_HD_MP9"" },
    { ""start"": ""not a date"", ""end"": ""2026-11-03T21:30:00.000Z"" },
    { ""start"": ""2026-11-05T19:00:00.000Z"", ""end"": ""2026-11-05T18:00:00.000Z"" },
    { ""start"": ""2026-11-06T19:00:00.000Z"", ""end"": ""2026-11-08T19:00:00.000Z"" }
  ] },
  ""news"": []
}";

        [Fact]
        public void Battle_nights_come_from_the_site_and_bad_entries_are_dropped()
        {
            var hub = SiteNews.ParseHub(B(HubWithMusters));
            Assert.Equal(2, hub.Musters.Count);
            Assert.All(hub.Musters, m => Assert.Equal(DateTimeKind.Utc, m.StartUtc.Kind));

            var now = new DateTime(2026, 11, 1, 16, 0, 0, DateTimeKind.Utc);
            var next = Lotr.Updater.Core.Schedule.EventSchedule.Next(now, hub.Musters);
            Assert.Equal(new DateTime(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc), next.StartUtc); // earliest, not the first listed
            // During the night it stays the current one; after it, the next site night.
            Assert.True(Lotr.Updater.Core.Schedule.EventSchedule.InProgress(new DateTime(2026, 11, 3, 20, 0, 0, DateTimeKind.Utc), hub.Musters));
            Assert.Equal(new DateTime(2026, 11, 10, 19, 0, 0, DateTimeKind.Utc), Lotr.Updater.Core.Schedule.EventSchedule.Next(new DateTime(2026, 11, 3, 22, 0, 0, DateTimeKind.Utc), hub.Musters).StartUtc);
        }

        [Fact]
        public void Without_site_nights_the_weekly_rule_is_used()
        {
            var after = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc); // every site night is over
            var hub = SiteNews.ParseHub(B(HubWithMusters));
            Assert.Equal(new DateTime(2026, 11, 24, 19, 0, 0, DateTimeKind.Utc), Lotr.Updater.Core.Schedule.EventSchedule.Next(after, hub.Musters).StartUtc);
            Assert.Empty(SiteNews.ParseHub(B(Hub)).Musters); // no schedule block
            Assert.Empty(SiteNews.ParseHub(B("{\"schema\":2,\"schedule\":{\"musters\":[{\"start\":\"2026-11-03T19:00:00Z\",\"end\":\"2026-11-03T21:00:00Z\"}]}}")).Musters);
            Assert.Equal(new DateTime(2026, 11, 3, 19, 0, 0, DateTimeKind.Utc), Lotr.Updater.Core.Schedule.EventSchedule.Next(new DateTime(2026, 11, 1, 16, 0, 0, DateTimeKind.Utc)).StartUtc);
        }

        [Fact]
        public async Task Service_reads_hub_json_from_the_server_and_survives_its_absence()
        {
            var files = Path.Combine(_dir, "files");
            Directory.CreateDirectory(Path.Combine(files, "site"));
            using (var srv = new TestHttpServer(files))
            {
                var svc = new UpdaterService(new UpdaterConfig { BaseUrl = srv.BaseUrl, Dev = true, AppVersion = "test" }, new Lotr.Updater.Core.Model.UpdaterPaths(Path.Combine(_dir, "home")));
                Assert.Null(await svc.FetchSiteNewsAsync(CancellationToken.None)); // 404: nothing, no exception

                File.WriteAllText(Path.Combine(files, "site", "hub.json"), Hub);
                var items = await svc.FetchSiteNewsAsync(CancellationToken.None);
                Assert.Equal(4, items.Count);
                Assert.Equal(2, srv.Hits("/hub.json"));
            }
        }
    }
}
