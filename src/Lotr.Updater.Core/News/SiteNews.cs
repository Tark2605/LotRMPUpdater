using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lotr.Updater.Core.News
{
    /// <summary>One chronicle of the site, as /hub.json lists it (all fields already plain text on the site side).</summary>
    public sealed class SiteNewsItem
    {
        public string Title { get; set; }
        /// <summary>"2026-10-25".</summary>
        public string Date { get; set; }
        /// <summary>Event module version without the leading "v", null for a chronicle that is not a release.</summary>
        public string Version { get; set; }
        public string Summary { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// The patch notes the site publishes in /hub.json (its Chronicles, written by the team), shown on Play and News for the
    /// event module version the player has or is about to get. Display only: nothing here decides what is installed, and
    /// every string goes through <see cref="ReleaseNews.CleanNotes"/> (plain text, never markup). When the site cannot be
    /// read, or has no chronicle for that version, the notes of the signed manifest are shown as before.
    /// </summary>
    public static class SiteNews
    {
        public const long MaxBytes = 512 * 1024;
        public const int MaxItems = 20;

        /// <summary>The "news" list of /hub.json (schema 1); empty for anything else. Never throws.</summary>
        public static List<SiteNewsItem> Parse(byte[] json)
        {
            var list = new List<SiteNewsItem>();
            if (json == null || json.Length == 0 || json.Length > MaxBytes) return list;
            try
            {
                var o = Json.Json.ParseObject(Encoding.UTF8.GetString(json));
                if (o.Str("schema") != "1") return list;
                foreach (var x in o.Objs("news"))
                {
                    var title = Line(x.Str("title"), 120);
                    if (title == null) continue;
                    list.Add(new SiteNewsItem
                    {
                        Title = title,
                        Date = Line(x.Str("date"), 10),
                        Version = NormalizeVersion(x.Str("version")),
                        Summary = ReleaseNews.CleanNotes(x.Str("summary")),
                        Text = ReleaseNews.CleanNotes(x.Str("text")),
                    });
                    if (list.Count >= MaxItems) break;
                }
            }
            catch (Exception) { list.Clear(); }
            return list;
        }

        /// <summary>The chronicle of that event module version ("v1.0.2.5" or "1.0.2.5"), or null.</summary>
        public static SiteNewsItem For(IEnumerable<SiteNewsItem> items, string eventVersion)
        {
            var v = NormalizeVersion(eventVersion);
            if (items == null || v == null) return null;
            return items.FirstOrDefault(i => i.Version == v && (i.Text != null || i.Summary != null));
        }

        /// <summary>"1.0.2.5 · Spears that hold the line".</summary>
        public static string Heading(SiteNewsItem i) => i == null ? null : i.Version == null ? i.Title : i.Version + " · " + i.Title;

        public static string Body(SiteNewsItem i) => i?.Text ?? i?.Summary;

        /// <summary>Up to <paramref name="max"/> earlier release chronicles (by date), newest first, for "Earlier releases".</summary>
        public static List<NewsEntry> Previous(IEnumerable<SiteNewsItem> items, SiteNewsItem current, int max = ReleaseNews.MaxPrevious)
        {
            if (items == null || current == null) return new List<NewsEntry>();
            var cur = current.Date ?? "";
            return items.Where(i => !ReferenceEquals(i, current) && i.Version != null && Body(i) != null && string.CompareOrdinal(i.Date ?? "", cur) < 0)
                .OrderByDescending(i => i.Date ?? "", StringComparer.Ordinal)
                .Take(max)
                .Select(i => new NewsEntry { ReleaseId = "site:" + i.Version, PublishedAt = i.Date, Title = Heading(i), Notes = Body(i) })
                .ToList();
        }

        private static string NormalizeVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.Length > 1 && (v[0] == 'v' || v[0] == 'V')) v = v.Substring(1);
            return v.Length <= 30 && v.All(c => char.IsDigit(c) || c == '.') && v.Length > 0 ? v : null;
        }

        private static string Line(string s, int max)
        {
            var c = ReleaseNews.CleanNotes(s);
            if (c == null) return null;
            c = c.Replace(Environment.NewLine, " ");
            return c.Length > max ? c.Substring(0, max) + "…" : c;
        }
    }
}
