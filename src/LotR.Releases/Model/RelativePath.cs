using System;
using System.IO;
using System.Linq;

namespace LotR.Releases.Model
{
    /// <summary>
    /// Path rules for manifest file paths. A path must stay inside the module folder whatever Windows does with it:
    /// no rooting, no '..', no drive letters or alternate data streams (':'), no device names, no trailing dot/space
    /// (Win32 silently strips them, which could make two entries collide).
    /// </summary>
    public static class RelativePath
    {
        public const int MaxLength = 240;

        private static readonly char[] Forbidden = { '\\', ':', '*', '?', '"', '<', '>', '|' };

        private static readonly string[] Devices =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        public static bool IsSafe(string path) => IsSafe(path, out _);

        public static bool IsSafe(string path, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(path))
            {
                reason = "empty";
                return false;
            }

            if (path.Length > MaxLength)
            {
                reason = "longer than " + MaxLength;
                return false;
            }

            if (path[0] == '/')
            {
                reason = "rooted";
                return false;
            }

            foreach (var seg in path.Split('/'))
            {
                if (!IsSafeSegment(seg, out reason))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsSafeSegment(string seg) => IsSafeSegment(seg, out _);

        public static bool IsSafeSegment(string seg, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(seg))
            {
                reason = "empty segment";
                return false;
            }

            if (seg == "." || seg == "..")
            {
                reason = "dot segment";
                return false;
            }

            if (seg.IndexOfAny(Forbidden) >= 0 || seg.IndexOf('/') >= 0 || seg.Any(c => c < 0x20))
            {
                reason = "forbidden character in '" + seg + "'";
                return false;
            }

            if (seg.EndsWith(".", StringComparison.Ordinal) || seg.EndsWith(" ", StringComparison.Ordinal) || seg.StartsWith(" ", StringComparison.Ordinal))
            {
                reason = "leading/trailing space or trailing dot in '" + seg + "'";
                return false;
            }

            int dot = seg.IndexOf('.');
            string stem = dot < 0 ? seg : seg.Substring(0, dot);
            if (Devices.Contains(stem.TrimEnd(), StringComparer.OrdinalIgnoreCase))
            {
                reason = "device name '" + seg + "'";
                return false;
            }

            return true;
        }

        public static bool IsExecutable(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? string.Empty);
            return ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Converts a file under <paramref name="root"/> to a manifest path ('/' separators).</summary>
        public static string FromFileSystem(string root, string fullPath)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(fullPath);
            if (!f.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("'" + fullPath + "' is not under '" + root + "'");
            }

            return f.Substring(r.Length).Replace('\\', '/');
        }

        /// <summary>
        /// Resolves a manifest path under a module folder and re-checks the result stays inside it (defence in depth for
        /// the updater: never write outside Modules\&lt;folder&gt;).
        /// </summary>
        public static string ToFileSystem(string moduleFolder, string manifestPath)
        {
            if (!IsSafe(manifestPath, out string why))
            {
                throw new InvalidDataException("Unsafe manifest path '" + manifestPath + "': " + why);
            }

            string root = Path.GetFullPath(moduleFolder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, manifestPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Manifest path '" + manifestPath + "' escapes the module folder");
            }

            return full;
        }
    }
}
