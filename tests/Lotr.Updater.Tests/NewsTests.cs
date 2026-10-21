using System;
using System.IO;
using System.Linq;
using Lotr.Updater.Core.News;
using LotR.Releases.Model;
using Xunit;

namespace Lotr.Updater.Tests
{
    public sealed class NewsTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "lotr-news-tests", Guid.NewGuid().ToString("N").Substring(0, 10));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private static ReleaseManifest Manifest(string id, string publishedAt, string notes, string eventVersion = "v1.0.2.3")
        {
            var m = new ReleaseManifest { ReleaseId = id, PublishedAt = publishedAt };
            m.Modules.Add(new ManifestModule { Id = "LotRMPCore", Name = "LotRMPCore", Version = "v1.4.7.5", Role = ModuleRoles.Core });
            m.Modules.Add(new ManifestModule { Id = "OrderMiddleEarthEvent", Name = "LotRMPEvent", Version = eventVersion, Role = ModuleRoles.Event });
            if (notes != null) m.Notes["en"] = notes;
            return m;
        }

        [Fact]
        public void Heading_names_the_event_module_and_version()
        {
            var e = ReleaseNews.FromManifest(Manifest("2026-10-20.1", "2026-10-20T18:00:00Z", "New map: Osgiliath."));
            Assert.Equal("What's new in LotRMPEvent v1.0.2.3", ReleaseNews.Heading(e));
            Assert.Equal("New map: Osgiliath.", ReleaseNews.Body(e));
        }

        [Fact]
        public void No_notes_gives_the_fallback_text()
        {
            var e = ReleaseNews.FromManifest(Manifest("r1", "2026-10-20T18:00:00Z", null));
            Assert.False(e.HasNotes);
            Assert.Equal(ReleaseNews.NoNotesText, ReleaseNews.Body(e));
            Assert.Equal(ReleaseNews.NoNotesText, ReleaseNews.Body(ReleaseNews.FromManifest(Manifest("r2", "2026-10-20T18:00:00Z", "  \r\n\t \n"))));
        }

        [Fact]
        public void Other_language_falls_back_to_english_then_any()
        {
            var m = Manifest("r1", "2026-10-20T18:00:00Z", "English text");
            m.Notes["fr"] = "Texte français";
            Assert.Equal("English text", ReleaseNews.FromManifest(m, "de").Notes);
            Assert.Equal("Texte français", ReleaseNews.FromManifest(m, "fr").Notes);
            m.Notes.Remove("en");
            Assert.Equal("Texte français", ReleaseNews.FromManifest(m, "de").Notes);
        }

        [Fact]
        public void Title_falls_back_without_an_event_module()
        {
            var m = new ReleaseManifest { ReleaseId = "r9" };
            Assert.Equal("r9", ReleaseNews.TitleOf(m));
            m.Modules.Add(new ManifestModule { Id = "LotRMPAdminClient", Version = "v1", Role = ModuleRoles.AdminClient });
            m.Modules.Add(new ManifestModule { Id = "LotRMPCore", Version = "v1.4.7.5", Role = ModuleRoles.Core });
            Assert.Equal("LotRMPCore v1.4.7.5", ReleaseNews.TitleOf(m));
        }

        [Fact]
        public void Server_text_is_kept_as_plain_text_and_cleaned()
        {
            var raw = "Line one\r\n\r\n\r\n\r\nLine two\twith tab\u0007‮evil​  \n<b>not bold</b> <script>x</script>";
            var c = ReleaseNews.CleanNotes(raw);
            var nl = Environment.NewLine;
            Assert.Equal("Line one" + nl + nl + "Line two with tabevil" + nl + "<b>not bold</b> <script>x</script>", c);
        }

        [Fact]
        public void Long_notes_are_capped()
        {
            var c = ReleaseNews.CleanNotes(new string('a', ReleaseNews.MaxNotesLength + 500));
            Assert.Equal(ReleaseNews.MaxNotesLength + 1, c.Length);
            Assert.EndsWith("…", c);
        }

        [Theory]
        [InlineData("https://middleearthevent.com", "https://middleearthevent.com/chronicles/")]
        [InlineData("https://middleearthevent.com/", "https://middleearthevent.com/chronicles/")]
        [InlineData("https://lotrmp.example", null)]           // placeholder build
        [InlineData("http://127.0.0.1:8080", null)]            // dev server
        [InlineData("https://user:pw@evil.test", null)]
        [InlineData("javascript:alert(1)", null)]
        [InlineData("", null)]
        public void Chronicles_link_only_for_a_real_https_site(string baseUrl, string expected)
        {
            Assert.Equal(expected, ReleaseNews.ChroniclesUrl(baseUrl));
        }

        [Fact]
        public void History_keeps_previous_notes_newest_first_and_survives_a_restart()
        {
            var file = Path.Combine(_dir, "news.json");
            var h = new NewsHistory(file);
            Assert.True(h.Record(ReleaseNews.FromManifest(Manifest("r1", "2026-09-01T18:00:00Z", "First", "v1.0.1.4"))));
            h.Record(ReleaseNews.FromManifest(Manifest("r2", "2026-09-15T18:00:00Z", null, "v1.0.1.7")));      // no notes: not listed
            h.Record(ReleaseNews.FromManifest(Manifest("r3", "2026-10-01T18:00:00Z", "Third", "v1.0.2.0")));
            h.Record(ReleaseNews.FromManifest(Manifest("r4", "2026-10-10T18:00:00Z", "Fourth", "v1.0.2.1")));
            h.Record(ReleaseNews.FromManifest(Manifest("r5", "2026-10-15T18:00:00Z", "Fifth", "v1.0.2.2")));
            var cur = ReleaseNews.FromManifest(Manifest("r6", "2026-10-20T18:00:00Z", "Sixth"));
            h.Record(cur);
            Assert.False(h.Record(cur)); // unchanged
            h.Save();

            var again = new NewsHistory(file);
            var prev = again.Previous(cur);
            Assert.Equal(new[] { "r5", "r4", "r3" }, prev.Select(x => x.ReleaseId));
            Assert.Equal("LotRMPEvent v1.0.2.2", prev[0].Title);
            Assert.Equal("Fifth", prev[0].Notes);
            // An older current release only shows what came before it.
            Assert.Equal(new[] { "r1" }, again.Previous(again.Entries.Single(x => x.ReleaseId == "r3")).Select(x => x.ReleaseId));
        }

        [Fact]
        public void History_is_capped_and_a_damaged_file_starts_fresh()
        {
            var file = Path.Combine(_dir, "news.json");
            var h = new NewsHistory(file);
            for (int i = 1; i <= NewsHistory.MaxEntries + 4; i++)
                h.Record(ReleaseNews.FromManifest(Manifest("r" + i, $"2026-10-{i:00}T18:00:00Z", "n" + i)));
            Assert.Equal(NewsHistory.MaxEntries, h.Entries.Count);
            Assert.Equal("r14", h.Entries[0].ReleaseId);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(file, "{ broken");
            Assert.Empty(new NewsHistory(file).Entries);
        }
    }
}
