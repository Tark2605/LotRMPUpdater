using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Model;
using Lotr.Updater.Core.Json;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Core.News
{
    /// <summary>The player-facing notes of one signed release ("What's new").</summary>
    public sealed class NewsEntry
    {
        public string ReleaseId { get; set; }
        /// <summary>UTC ISO 8601 from the manifest (used for ordering only).</summary>
        public string PublishedAt { get; set; }
        /// <summary>"LotRMPEvent v1.0.2.3": the event module's name and version.</summary>
        public string Title { get; set; }
        /// <summary>Plain text, already cleaned by <see cref="ReleaseNews.CleanNotes"/>; null when the release has none.</summary>
        public string Notes { get; set; }

        public bool HasNotes => !string.IsNullOrEmpty(Notes);

        /// <summary>"12 October 2026" (en-GB), empty when the date is unknown.</summary>
        public string DateLabel
        {
            get
            {
                return DateTime.TryParse(PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                    ? d.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
                    : "";
            }
        }
    }

    /// <summary>
    /// Decisions for the "What's new" card: which text to show, how server text is made safe (plain text only, never
    /// rendered as HTML or markup), and the link to the site's news page. The notes come from the signed manifest
    /// (manifest.notes, filled by lotr-publish --notes / the admin Modules screen); nothing extra is downloaded.
    /// </summary>
    public static class ReleaseNews
    {
        public const int MaxNotesLength = 4000;
        public const int MaxPrevious = 3;
        public const string NoNotesText = "No notes for this version.";

        public static NewsEntry FromManifest(ReleaseManifest m, string lang = "en")
        {
            if (m == null) return null;
            string notes = null;
            if (m.Notes != null && m.Notes.Count > 0)
                notes = m.Notes.TryGetValue(lang, out var t) ? t : m.Notes.TryGetValue("en", out var en) ? en : m.Notes.Values.First();
            return new NewsEntry { ReleaseId = m.ReleaseId, PublishedAt = m.PublishedAt, Title = TitleOf(m), Notes = CleanNotes(notes) };
        }

        /// <summary>The event module (role "event") names the release; else the first player module; else the release id.</summary>
        public static string TitleOf(ReleaseManifest m)
        {
            var mod = m.Modules.FirstOrDefault(x => string.Equals(x.Role, ModuleRoles.Event, StringComparison.Ordinal))
                      ?? m.Modules.FirstOrDefault(x => !string.Equals(x.Role, ModuleRoles.AdminClient, StringComparison.Ordinal))
                      ?? m.Modules.FirstOrDefault();
            if (mod == null) return m.ReleaseId ?? "this release";
            var name = Clean(mod.DisplayName(), 60);
            var v = Clean(mod.Version, 30);
            return string.IsNullOrEmpty(v) ? name : name + " " + v;
        }

        public static string Heading(NewsEntry e) => e == null ? null : "What's new in " + e.Title;

        public static string Body(NewsEntry e) => e == null ? null : e.HasNotes ? e.Notes : NoNotesText;

        /// <summary>Plain text for a TextBlock: newlines normalised, control and bidi-override characters removed, at most two
        /// blank lines in a row, trimmed, capped at <see cref="MaxNotesLength"/>. Null when nothing is left.</summary>
        public static string CleanNotes(string s)
        {
            if (s == null) return null;
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder(Math.Min(s.Length, MaxNotesLength + 16));
            foreach (var c in s)
            {
                if (c == '\n' || c == '\t') { sb.Append(c == '\t' ? ' ' : c); continue; }
                if (char.IsControl(c) || IsBidiOrInvisible(c)) continue;
                sb.Append(c);
            }
            var lines = sb.ToString().Split('\n').Select(l => l.TrimEnd()).ToList();
            var outLines = new List<string>();
            int blank = 0;
            foreach (var l in lines)
            {
                if (l.Length == 0) { if (++blank > 1) continue; }
                else blank = 0;
                outLines.Add(l);
            }
            var text = string.Join("\n", outLines).Trim('\n', ' ');
            if (text.Length == 0) return null;
            if (text.Length > MaxNotesLength) text = text.Substring(0, MaxNotesLength).TrimEnd() + "…";
            return text.Replace("\n", Environment.NewLine);
        }

        private static string Clean(string s, int max)
        {
            var c = CleanNotes(s);
            if (c == null) return "";
            c = c.Replace(Environment.NewLine, " ");
            return c.Length > max ? c.Substring(0, max) + "…" : c;
        }

        private static bool IsBidiOrInvisible(char c) =>
            (c >= '​' && c <= '‏') || (c >= '‪' && c <= '‮') || (c >= '⁦' && c <= '⁩') || c == '﻿';

        /// <summary>https://&lt;site&gt;/chronicles/ for the baked production base; null for the placeholder, a dev (http) server
        /// or anything that is not a plain https origin, so the link never points somewhere odd.</summary>
        public static string ChroniclesUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return null;
            if (u.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)) return null;
            return u.GetLeftPart(UriPartial.Authority) + "/chronicles/";
        }
    }

    /// <summary>
    /// Notes of the releases this PC has seen (installed or offered), kept in %LOCALAPPDATA%\LotRMPUpdater\news.json so the card
    /// can show a few previous ones without any extra download. Only text that came from a signature-checked manifest is recorded.
    /// </summary>
    public sealed class NewsHistory
    {
        public const int MaxEntries = 10;

        private readonly string _file;
        private readonly List<NewsEntry> _entries = new List<NewsEntry>();

        public NewsHistory(string file)
        {
            _file = file;
            Load();
        }

        public static string DefaultFile(UpdaterPaths p) => Path.Combine(p.Home, "news.json");

        public IReadOnlyList<NewsEntry> Entries => _entries;

        /// <summary>Adds or refreshes one release; returns true when something changed (the caller saves).</summary>
        public bool Record(NewsEntry e)
        {
            if (e == null || string.IsNullOrEmpty(e.ReleaseId)) return false;
            var old = _entries.FirstOrDefault(x => x.ReleaseId == e.ReleaseId);
            if (old != null && old.Notes == e.Notes && old.Title == e.Title && old.PublishedAt == e.PublishedAt) return false;
            if (old != null) _entries.Remove(old);
            _entries.Add(new NewsEntry { ReleaseId = e.ReleaseId, PublishedAt = e.PublishedAt, Title = e.Title, Notes = e.Notes });
            Sort();
            while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            return true;
        }

        /// <summary>Up to <paramref name="max"/> releases published before <paramref name="current"/>, newest first, notes only.</summary>
        public List<NewsEntry> Previous(NewsEntry current, int max = ReleaseNews.MaxPrevious)
        {
            if (current == null) return new List<NewsEntry>();
            var cur = Key(current.PublishedAt);
            return _entries.Where(x => x.ReleaseId != current.ReleaseId && x.HasNotes && string.CompareOrdinal(Key(x.PublishedAt), cur) < 0)
                .Take(max).ToList();
        }

        public void Save()
        {
            try
            {
                var arr = new List<object>();
                foreach (var e in _entries)
                    arr.Add(new JsonObject { ["releaseId"] = e.ReleaseId, ["publishedAt"] = e.PublishedAt, ["title"] = e.Title, ["notes"] = e.Notes });
                var o = new JsonObject { ["schema"] = 1L, ["releases"] = arr };
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, Json.Json.Write(o), new UTF8Encoding(false));
                if (File.Exists(_file)) File.Replace(tmp, _file, null);
                else File.Move(tmp, _file);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var o = Json.Json.ParseObject(File.ReadAllText(_file, Encoding.UTF8));
                foreach (var x in o.Objs("releases"))
                {
                    var id = x.Str("releaseId");
                    if (string.IsNullOrEmpty(id) || _entries.Any(e => e.ReleaseId == id)) continue;
                    // Re-clean on load: the file is ours, but it lives in a user-writable folder.
                    _entries.Add(new NewsEntry { ReleaseId = id, PublishedAt = x.Str("publishedAt"), Title = ReleaseNewsTitle(x.Str("title"), id), Notes = ReleaseNews.CleanNotes(x.Str("notes")) });
                }
                Sort();
                while (_entries.Count > MaxEntries) _entries.RemoveAt(_entries.Count - 1);
            }
            catch (Exception) { _entries.Clear(); } // damaged file: start again, it is only a cache
        }

        private static string ReleaseNewsTitle(string t, string id)
        {
            var c = ReleaseNews.CleanNotes(t);
            if (c == null) return id;
            c = c.Replace(Environment.NewLine, " ");
            return c.Length > 100 ? c.Substring(0, 100) + "…" : c;
        }

        private void Sort() => _entries.Sort((a, b) => string.CompareOrdinal(Key(b.PublishedAt), Key(a.PublishedAt)));

        private static string Key(string publishedAt) => publishedAt ?? "";
    }
}
