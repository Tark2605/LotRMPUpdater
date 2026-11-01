using System;
using System.Collections.Generic;
using System.Linq;
using Lotr.Updater.Core.Model;

namespace Lotr.Updater.Core.Schedule
{
    /// <summary>One battle night as the site lists it in /hub.json (schedule.musters), in UTC.</summary>
    public sealed class Muster
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
    }

    /// <summary>
    /// The weekly event. Since 0.3.2 the battle nights come from the site (/hub.json, set in its admin panel: day, time,
    /// skipped and extra nights); without them, the fallback rule below: Tuesday 20:00, Paris time (handles summer/winter
    /// time; 20:30 until 1 Nov 2026).
    /// </summary>
    public static class EventSchedule
    {
        public static readonly DayOfWeek Day = DayOfWeek.Tuesday;
        public static readonly TimeSpan Start = new TimeSpan(20, 0, 0);
        public static readonly TimeSpan Length = TimeSpan.FromHours(2.5);

        public static TimeZoneInfo Paris
        {
            get
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"); }
                catch (TimeZoneNotFoundException) { }
                try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"); }
                catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
            }
        }

        /// <summary>The next battle (or the one in progress): the first site night not over yet, else the weekly rule.</summary>
        public static Muster Next(DateTime nowUtc, IReadOnlyList<Muster> site = null)
        {
            var m = site?.Where(x => x.EndUtc > nowUtc).OrderBy(x => x.StartUtc).FirstOrDefault();
            if (m != null) return m;
            var s = NextStartUtc(nowUtc);
            return new Muster { StartUtc = s, EndUtc = s + Length };
        }

        /// <summary>Start of the next battle by the weekly rule (or of the one in progress), in UTC.</summary>
        public static DateTime NextStartUtc(DateTime nowUtc)
        {
            var tz = Paris;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
            for (int d = -1; d <= 7; d++)
            {
                var day = local.Date.AddDays(d);
                if (day.DayOfWeek != Day) continue;
                var startLocal = DateTime.SpecifyKind(day + Start, DateTimeKind.Unspecified);
                var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
                if (startUtc + Length > nowUtc) return startUtc;
            }
            throw new InvalidOperationException("unreachable");
        }

        public static bool InProgress(DateTime nowUtc, IReadOnlyList<Muster> site = null)
        {
            var m = Next(nowUtc, site);
            return nowUtc >= m.StartUtc && nowUtc < m.EndUtc;
        }

        public static string Countdown(DateTime nowUtc, IReadOnlyList<Muster> site = null)
        {
            var s = Next(nowUtc, site).StartUtc;
            if (nowUtc >= s) return "The battle is on right now!";
            var left = s - nowUtc;
            if (left.TotalDays >= 1) return $"{(int)left.TotalDays} d {left.Hours} h {left.Minutes:00} min";
            if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes:00} min";
            return $"{left.Minutes} min {left.Seconds:00} s";
        }
    }

    /// <summary>
    /// Background pre-download (PLAN: bandwidth). When a new release is first seen, a random delay of 0..max minutes is drawn so
    /// that the players do not all download at the same second; the download only happens while the app is open (no service).
    /// The install itself always waits for the player's click.
    /// </summary>
    public static class PreDownloadPolicy
    {
        private static readonly Random Rng = new Random();

        /// <summary>Records a newly seen release. Returns true when the state changed.</summary>
        public static bool Observe(UpdaterState s, string latestReleaseId, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(latestReleaseId) || latestReleaseId == s.InstalledReleaseId) return false;
            if (s.PendingReleaseId == latestReleaseId) return false;
            s.PendingReleaseId = latestReleaseId;
            s.PendingSeenAtUtc = nowUtc.ToString("o");
            lock (Rng) s.PendingJitterMinutes = Rng.Next(0, Math.Max(0, s.PreDownloadMaxJitterMinutes) + 1);
            s.PendingDownloaded = false;
            return true;
        }

        public static DateTime? DueUtc(UpdaterState s)
        {
            if (string.IsNullOrEmpty(s.PendingReleaseId) || !DateTime.TryParse(s.PendingSeenAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen)) return null;
            var due = seen.ToUniversalTime().AddMinutes(s.PendingJitterMinutes);
            // Never wait past one hour before the battle.
            var latest = EventSchedule.NextStartUtc(seen.ToUniversalTime()).AddHours(-1);
            return due > latest && latest > seen.ToUniversalTime() ? latest : due;
        }

        public static bool IsDue(UpdaterState s, DateTime nowUtc)
        {
            if (!s.PreDownload || s.PendingDownloaded) return false;
            var due = DueUtc(s);
            return due.HasValue && nowUtc >= due.Value;
        }
    }
}
