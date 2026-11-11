using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Lotr.Updater.Core.News
{
    /// <summary>
    /// The server's notice that downloads are paused on purpose (the "event lock" of a battle night, site
    /// deploy/package/Set-EventNightMode.ps1). While it is on, the server answers <c>/status.json</c> with 200 and this
    /// JSON, and every other updater path with 423: the check fails at once (423 is never retried) and the updater shows
    /// this notice instead of a network error. Play stays available. In normal times /status.json is a 404.
    /// Display only: the notice changes nothing on disk, its texts are plain text (<see cref="ReleaseNews.CleanNotes"/>),
    /// and its link is only kept when it points to the LotR server itself or to one of <see cref="DriveFolders"/>.
    /// </summary>
    public sealed class ServerNotice
    {
        public const long MaxBytes = 16 * 1024;

        /// <summary>
        /// The only links outside the LotR server a notice may open: the team's Google Drive folders where the mod is
        /// mirrored, by exact folder id. Not the whole of drive.google.com: anyone can put a file there, and a hijacked
        /// status.json must not be able to send players to it from inside this trusted app.
        /// </summary>
        public static readonly string[] DriveFolders = { "1NRSlD6-Xq4stAYVUidEB0K9LCB0WPQCa" };

        public string Title { get; set; }
        public string Message { get; set; }
        /// <summary>When the server plans to resume, if it said so.</summary>
        public DateTime? UntilUtc { get; set; }
        /// <summary>A checked https link where players can get the mod meanwhile, or null.</summary>
        public string InstallUrl { get; set; }

        /// <summary>The notice in /status.json (schema 1, state "event-lock"); null for anything else. Never throws.</summary>
        public static ServerNotice Parse(byte[] json, string baseUrl)
        {
            if (json == null || json.Length == 0 || json.Length > MaxBytes) return null;
            try
            {
                var o = Json.Json.ParseObject(Encoding.UTF8.GetString(json));
                if (o.Str("schema") != "1" || o.Str("state") != "event-lock") return null;
                var n = new ServerNotice
                {
                    Title = Line(o.Str("title"), 80) ?? "Downloads are paused",
                    Message = Text(o.Str("message"), 600) ?? "The server has paused downloads for now. Already up to date? Start the game as usual.",
                    InstallUrl = SafeLink(o.Str("installUrl"), baseUrl),
                };
                if (DateTime.TryParse(o.Str("until"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var u))
                {
                    u = DateTime.SpecifyKind(u, DateTimeKind.Utc);
                    // A pause is a battle night, not days: anything else is a typo and is not shown.
                    var now = DateTime.UtcNow;
                    if (u > now.AddMinutes(-30) && u < now.AddHours(24)) n.UntilUtc = u;
                }
                return n;
            }
            catch (Exception) { return null; }
        }

        /// <summary>https only, no user info, default port: the LotR server's host, or one of <see cref="DriveFolders"/>; else null.</summary>
        public static string SafeLink(string url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 500) return null;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !u.IsDefaultPort) return null;
            var host = u.IdnHost;
            if (string.Equals(host, "drive.google.com", StringComparison.OrdinalIgnoreCase))
            {
                var path = u.AbsolutePath.TrimEnd('/');
                var id = DriveFolders.FirstOrDefault(f => string.Equals(path, "/drive/folders/" + f, StringComparison.Ordinal));
                return id == null ? null : "https://drive.google.com/drive/folders/" + id;   // rebuilt: no query, no fragment
            }
            if (Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out var b) && b.Scheme == Uri.UriSchemeHttps && string.Equals(b.IdnHost, host, StringComparison.OrdinalIgnoreCase))
                return u.AbsoluteUri;
            return null;
        }

        /// <summary>"Back after the battle, around 23:30." in the player's local time; null without an end time.</summary>
        public string UntilText()
        {
            if (UntilUtc == null) return null;
            return "Back after the battle, around " + UntilUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + ".";
        }

        private static string Text(string s, int max)
        {
            var c = ReleaseNews.CleanNotes(s);
            if (c == null) return null;
            return c.Length > max ? c.Substring(0, max) + "…" : c;
        }

        private static string Line(string s, int max)
        {
            var c = Text(s, max);
            return c?.Replace(Environment.NewLine, " ").Replace("\n", " ");
        }
    }
}
