using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Lotr.Updater.Core.Model;
using Lotr.Updater.Core.Net;
using Lotr.Updater.Core.Objects;

namespace Lotr.Updater.Core.Install
{
    /// <summary>What kind of problem stopped an operation (drives the wording and whether a silent retry makes sense).</summary>
    public enum ProblemKind
    {
        Unknown,
        /// <summary>A local file the plan wanted to reuse (Steam Workshop copy, moved file, current file) vanished or changed.</summary>
        LocalSourceChanged,
        FileLocked,
        DiskFull,
        AccessDenied,
        Network,
        ServerMissingFile,
        DamagedDownload,
        Blocked,
        RollbackIncomplete,
        Cancelled,
    }

    /// <summary>A problem described for a player (short, calm, English) plus the technical detail for the log and the
    /// "Details" expander. .NET exception messages are localized in the player's Windows language and full of jargon: they
    /// only ever go into <see cref="Detail"/>.</summary>
    public sealed class PlayerProblem
    {
        public PlayerProblem(ProblemKind kind, string message, string detail)
        {
            Kind = kind;
            Message = message;
            Detail = detail;
        }

        public ProblemKind Kind { get; }
        /// <summary>What happened + what to do + reassurance. Safe to show as is.</summary>
        public string Message { get; }
        /// <summary>Technical detail (exception types, raw messages, paths). Log / "Details" only.</summary>
        public string Detail { get; }
    }

    /// <summary>Maps exceptions to player-facing sentences (install, check, verify, support).</summary>
    public static class PlayerErrors
    {
        public const string NothingChanged = "Nothing was changed: your previous files are still in place.";

        /// <summary>A file of the plan disappeared from the Steam Workshop folder (unsubscribe, Steam update).</summary>
        public const string WorkshopSourceGone =
            "A file the updater wanted to reuse from your Steam Workshop folder disappeared (Steam removes files when you unsubscribe or updates them). " +
            NothingChanged + " Try again: the updater will download it instead.";

        public const string LocalSourceGone =
            "A file the updater wanted to reuse from your PC changed or disappeared while it was working. " +
            NothingChanged + " Try again: the updater will download it instead.";

        /// <summary>Player text for an exception thrown while installing (after the rollback).</summary>
        public static PlayerProblem ForInstall(Exception ex)
        {
            if (ex is InstallException ie) return new PlayerProblem(ie.Kind, ie.Message, ie.Detail);
            var kind = Classify(ex);
            return new PlayerProblem(kind, InstallText(kind, ex, true), Technical(ex));
        }

        /// <summary>Player text for a failed "check for updates" (nothing is ever changed by a check).</summary>
        public static PlayerProblem ForCheck(Exception ex)
        {
            if (ex is InstallException ie) return new PlayerProblem(ie.Kind, ie.Message, ie.Detail);
            var kind = Classify(ex);
            string text;
            switch (kind)
            {
                case ProblemKind.Network:
                    text = "The updater could not reach the LotR server. Check your internet connection; the updater tries again by itself in a while, or click Check again.";
                    break;
                case ProblemKind.ServerMissingFile:
                    text = "The server is being updated right now. Try again in a few minutes.";
                    break;
                case ProblemKind.DamagedDownload:
                    text = "A file from the server arrived damaged twice. Nothing was changed. Try again in a few minutes.";
                    break;
                case ProblemKind.AccessDenied:
                    text = "Windows did not let the updater read some of your mod files. Nothing was changed. Close the game and click Check again.";
                    break;
                case ProblemKind.FileLocked:
                    text = "Some mod files are in use by another program (often the game or an antivirus scan). Nothing was changed. Close the game and click Check again.";
                    break;
                case ProblemKind.DiskFull:
                    text = "Your disk is full. Nothing was changed. Free some space and click Check again.";
                    break;
                default:
                    // ManifestException messages are written by us in English for players; anything else gets a generic sentence.
                    text = ex is ManifestException ? ex.Message : "Something unexpected stopped the check. Nothing was changed. Click Check again; if it happens again, send a report to the team.";
                    break;
            }
            return new PlayerProblem(kind, text, Technical(ex));
        }

        /// <summary>Player text for a failed "Verify my install".</summary>
        public static PlayerProblem ForVerify(Exception ex)
        {
            if (ex is InstallException ie) return new PlayerProblem(ie.Kind, ie.Message, ie.Detail);
            var p = ForCheck(ex);
            return new PlayerProblem(p.Kind, p.Message.Replace("Click Check again", "Click Verify now again").Replace("click Check again", "click Verify now again").Replace("stopped the check", "stopped the verification"), p.Detail);
        }

        /// <summary>Player text when sending a report did not work.</summary>
        public static PlayerProblem ForSupport(Exception ex)
        {
            var kind = Classify(ex);
            var text = kind == ProblemKind.Network || ex is HttpFetchException
                ? "The report could not be sent: the LotR server did not answer. Try again in a minute, or press \"Copy log\" and paste it in your Discord ticket."
                : "The report could not be sent. Press \"Copy log\" and paste it in your Discord ticket instead.";
            return new PlayerProblem(kind, text, Technical(ex));
        }

        /// <summary>Sentence for an install failure of this kind. <paramref name="rolledBack"/>: the game folder was put back.</summary>
        internal static string InstallText(ProblemKind kind, Exception ex, bool rolledBack)
        {
            string safe = " " + (rolledBack ? NothingChanged : "Nothing was changed in your game folder.");
            switch (kind)
            {
                case ProblemKind.LocalSourceChanged:
                    return IsWorkshopPath(PathOf(ex)) ? WorkshopSourceGone : LocalSourceGone;
                case ProblemKind.FileLocked:
                    return LockedText(FindLocked(ex), true);
                case ProblemKind.DiskFull:
                    return "Your disk became full during the update." + safe + " Free some space, then try again.";
                case ProblemKind.AccessDenied:
                    return "Windows refused the updater access to a file in your game folder." + safe +
                           " Check that the folder is not read-only and that Windows Security \"Controlled folder access\" allows LotRMPUpdater, then try again.";
                case ProblemKind.Network:
                    return "The download stopped because the LotR server could not be reached. Nothing was changed in your game folder, and what was already downloaded is kept. Check your internet connection and try again.";
                case ProblemKind.ServerMissingFile:
                    return "A file of this update is not on the server yet (the team may be publishing right now). Nothing was changed in your game folder. Try again in a few minutes.";
                case ProblemKind.DamagedDownload:
                    return "A file arrived damaged twice from the server, so the updater stopped." + safe + " Try again in a few minutes.";
                default:
                    return "Something unexpected stopped the update." + safe + " Try again; if it happens again, send a report to the team.";
            }
        }

        private static FileLockedException FindLocked(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException) if (e is FileLockedException f) return f;
            return null;
        }

        internal static string LockedText(FileLockedException ex, bool rolledBack)
        {
            var file = ex?.Path != null ? System.IO.Path.GetFileName(ex.Path) : "a mod file";
            var names = ex?.Lockers != null && ex.Lockers.Count > 0 ? string.Join(", ", ex.Lockers.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase)) : null;
            return names != null
                ? $"Windows did not let the updater replace {file}: {names} is using it. Close {names}, then try again." + (rolledBack ? " " + NothingChanged : "")
                : $"Windows did not let the updater replace {file}: another program is using it (often the game, its launcher, an antivirus scan or a File Explorer preview). Close it, wait a minute, then try again." + (rolledBack ? " " + NothingChanged : "");
        }

        public static ProblemKind Classify(Exception ex)
        {
            ex = Unwrap(ex);
            switch (ex)
            {
                case null: return ProblemKind.Unknown;
                case InstallException ie: return ie.Kind;
                case OperationCanceledException _: return ProblemKind.Cancelled;
                case LocalSourceException _: return ProblemKind.LocalSourceChanged;
                case FileLockedException _: return ProblemKind.FileLocked;
                case CorruptObjectException _: return ProblemKind.DamagedDownload;
                case HttpFetchException h: return h.Status == HttpStatusCode.NotFound ? ProblemKind.ServerMissingFile : ProblemKind.Network;
                case HttpRequestException _: return ProblemKind.Network;
                case WebException _: return ProblemKind.Network;
                case UnauthorizedAccessException _: return ProblemKind.AccessDenied;
                case FileNotFoundException _: return ProblemKind.LocalSourceChanged;
                case DirectoryNotFoundException _: return ProblemKind.LocalSourceChanged;
                case IOException io:
                    if (IsDiskFull(io)) return ProblemKind.DiskFull;
                    if (IsSharingViolation(io)) return ProblemKind.FileLocked;
                    return ProblemKind.Unknown;
                default: return ProblemKind.Unknown;
            }
        }

        public static bool IsDiskFull(Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            return ex is IOException && (code == 112 || code == 39);
        }

        public static bool IsSharingViolation(Exception ex)
        {
            int code = ex.HResult & 0xFFFF;
            return ex is IOException && (code == 32 || code == 33);
        }

        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException ag && ag.InnerExceptions.Count == 1) ex = ag.InnerException;
            return ex;
        }

        private static string PathOf(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is LocalSourceException ls) return ls.SourcePath;
                if (e is FileNotFoundException fnf && fnf.FileName != null) return fnf.FileName;
            }
            // DirectoryNotFoundException has no path property: the message carries it (any language), good enough for "workshop".
            return ex?.Message;
        }

        internal static bool IsWorkshopPath(string path) =>
            path != null && path.IndexOf(@"\workshop\content\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Exception chain as "Type: message" lines (log and Details only, never the main text).</summary>
        public static string Technical(Exception ex)
        {
            if (ex == null) return null;
            var sb = new StringBuilder();
            var seen = new HashSet<Exception>();
            for (var e = ex; e != null && seen.Add(e); e = e.InnerException)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(e.GetType().Name);
                if ((e.HResult & 0xFFFF0000) == 0x80070000) sb.Append(" (0x").Append(e.HResult.ToString("X8")).Append(')');
                sb.Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>True when a text looks like raw exception output (tests and a last-chance UI guard).</summary>
        public static bool LooksTechnical(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] markers = { "Exception", "HRESULT", "0x8007", "   at ", "System.", "Impossible de", "Le processus", "Accès", "Zugriff", "Der Prozess", "denied.", "Could not find a part of the path", "The process cannot access" };
            return markers.Any(m => text.IndexOf(m, StringComparison.Ordinal) >= 0);
        }
    }

    /// <summary>A local file the plan reuses (instead of downloading) is gone, unreadable or no longer has the expected content.
    /// The engine reacts by downloading that file instead.</summary>
    public sealed class LocalSourceException : IOException
    {
        public LocalSourceException(string sourcePath, string why, Exception inner = null)
            : base("Local source " + sourcePath + ": " + why, inner)
        {
            SourcePath = sourcePath;
        }

        public string SourcePath { get; }
    }
}
