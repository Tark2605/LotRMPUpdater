using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LotR.Releases.Model
{
    /// <summary>
    /// Path rules of the operating system the updater runs on. Windows (NTFS) ignores letter case in file names; Linux
    /// (ext4, btrfs, the Steam Deck's SD card) does not, so "ModuleData/a.xml" and "moduledata/a.xml" are two different
    /// files there. Every comparison of full paths or module paths goes through here (docs/LINUX.md, "Letter case").
    /// </summary>
    public static class OsPath
    {
        /// <summary>True on Windows (net48 and netstandard2.0 both have RuntimeInformation).</summary>
        public static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>How file names compare on this OS: ignoring case on Windows, exactly elsewhere.</summary>
        public static StringComparison Comparison => ComparisonFor(IsWindows);

        /// <summary>The <see cref="Comparison"/> as a comparer (dictionaries and sets keyed by paths).</summary>
        public static StringComparer Comparer => ComparerFor(IsWindows);

        public static StringComparison ComparisonFor(bool windows) => windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static StringComparer ComparerFor(bool windows) => windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>Removes trailing separators ('\' and '/' on Windows, '/' elsewhere) but never turns a root into "".</summary>
        public static string TrimEndSeparators(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var t = IsWindows ? path.TrimEnd('\\', '/') : path.TrimEnd('/');
            return t.Length == 0 ? path.Substring(0, 1) : t;
        }

        /// <summary>The folder with exactly one trailing separator of this OS ("C:\Games\" or "/home/deck/").</summary>
        public static string WithTrailingSeparator(string dir)
        {
            var t = TrimEndSeparators(dir);
            return t.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? t : t + Path.DirectorySeparatorChar;
        }

        /// <summary>True when <paramref name="path"/> lies strictly inside <paramref name="root"/> (never the root itself).
        /// Both are made absolute first; case follows <see cref="Comparison"/>. False for anything unparsable.</summary>
        public static bool IsStrictlyUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                var p = WithTrailingSeparator(Path.GetFullPath(path));
                var r = WithTrailingSeparator(Path.GetFullPath(root));
                return p.Length > r.Length && p.StartsWith(r, Comparison);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>Same folder on this OS (absolute, trailing separators ignored). Throws on an unparsable path, like
        /// <see cref="Path.GetFullPath(string)"/>: a caller deleting "everything but this folder" must never guess.</summary>
        public static bool SameFolder(string a, string b) =>
            string.Equals(TrimEndSeparators(Path.GetFullPath(a)), TrimEndSeparators(Path.GetFullPath(b)), Comparison);
    }
}
