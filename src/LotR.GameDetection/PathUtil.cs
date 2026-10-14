using System;
using System.Collections.Generic;
using System.Text;

namespace LotR.GameDetection
{
    /// <summary>
    /// Pure string helpers for Windows paths. They never touch the disk, so they behave the same
    /// on .NET Framework (where Path.GetFullPath may reject long paths) and on modern .NET.
    /// </summary>
    public static class PathUtil
    {
        public const string ExtendedPrefix = @"\\?\";
        public const string ExtendedUncPrefix = @"\\?\UNC\";

        /// <summary>
        /// Removes quotes, the \\?\ prefix, forward slashes, duplicate separators, "." and ".." segments
        /// and the trailing separator (except for drive roots, which keep "C:\").
        /// Returns null for null/blank input.
        /// </summary>
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string p = path.Trim().Trim('"').Trim();
            if (p.Length == 0)
                return null;

            p = p.Replace('/', '\\');

            bool unc = false;
            if (p.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(ExtendedUncPrefix.Length);
                unc = true;
            }
            else if (p.StartsWith(ExtendedPrefix, StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                p = p.Substring(4);
            }
            else if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                p = p.Substring(2);
                unc = true;
            }

            var parts = p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var stack = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                if (part == ".")
                    continue;
                if (part == "..")
                {
                    // never pop the drive ("C:") or the UNC server/share
                    int floor = unc ? 2 : 1;
                    if (stack.Count > floor)
                        stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(part);
            }

            if (stack.Count == 0)
                return null;

            if (unc)
                return @"\\" + string.Join(@"\", stack);

            if (stack.Count == 1 && IsDriveToken(stack[0]))
                return char.ToUpperInvariant(stack[0][0]) + @":\";

            if (IsDriveToken(stack[0]))
                stack[0] = char.ToUpperInvariant(stack[0][0]) + ":";

            return string.Join(@"\", stack);
        }

        /// <summary>Case-insensitive identity key used for de-duplication.</summary>
        public static string Key(string path)
        {
            var n = Normalize(path);
            return n == null ? string.Empty : n.ToUpperInvariant();
        }

        public static bool PathEquals(string a, string b) => Key(a) == Key(b);

        public static string Combine(string first, params string[] more)
        {
            var sb = new StringBuilder(first ?? string.Empty);
            foreach (var m in more)
            {
                if (string.IsNullOrEmpty(m))
                    continue;
                if (sb.Length > 0 && sb[sb.Length - 1] != '\\' && sb[sb.Length - 1] != '/')
                    sb.Append('\\');
                sb.Append(m.TrimStart('\\', '/'));
            }
            return Normalize(sb.ToString());
        }

        /// <summary>Parent directory, or null for a drive root / share root.</summary>
        public static string GetParent(string path)
        {
            var n = Normalize(path);
            if (n == null)
                return null;
            if (n.Length == 3 && n[1] == ':' && n[2] == '\\')
                return null;
            int idx = n.LastIndexOf('\\');
            if (idx < 0)
                return null;
            if (n.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // \\server\share is the root of a UNC path
                int segments = n.Substring(2).Split('\\').Length;
                if (segments <= 2)
                    return null;
            }
            var parent = n.Substring(0, idx);
            if (parent.Length == 2 && parent[1] == ':')
                parent += @"\";
            return parent;
        }

        public static string GetFileName(string path)
        {
            var n = Normalize(path);
            if (n == null)
                return null;
            int idx = n.LastIndexOf('\\');
            return idx < 0 ? n : n.Substring(idx + 1);
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies below it.</summary>
        public static bool IsUnder(string path, string ancestor)
        {
            var p = Key(path);
            var a = Key(ancestor);
            if (p.Length == 0 || a.Length == 0)
                return false;
            if (p == a)
                return true;
            if (!a.EndsWith(@"\", StringComparison.Ordinal))
                a += @"\";
            return p.StartsWith(a, StringComparison.Ordinal);
        }

        /// <summary>True when any segment of the path equals <paramref name="segment"/> (case-insensitive).</summary>
        public static bool ContainsSegment(string path, string segment)
        {
            var n = Normalize(path);
            if (n == null)
                return false;
            foreach (var part in n.Split('\\'))
            {
                if (string.Equals(part, segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Adds the \\?\ prefix so Win32 accepts paths longer than MAX_PATH even on .NET Framework.
        /// Only applied to absolute drive or UNC paths.
        /// </summary>
        public static string ToExtended(string path)
        {
            var n = Normalize(path);
            if (n == null)
                return path;
            if (n.Length >= 2 && n[1] == ':')
                return ExtendedPrefix + (n.Length == 2 ? n + @"\" : n);
            if (n.StartsWith(@"\\", StringComparison.Ordinal))
                return ExtendedUncPrefix + n.Substring(2);
            return n;
        }

        private static bool IsDriveToken(string s) => s.Length == 2 && s[1] == ':' && char.IsLetter(s[0]);
    }
}
