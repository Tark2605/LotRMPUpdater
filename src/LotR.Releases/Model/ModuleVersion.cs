using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LotR.Releases.Model
{
    /// <summary>
    /// Bannerlord application/module version: optional type letter (a=alpha, b=beta, e=early access, v=release,
    /// d=development, as TaleWorlds ApplicationVersionType), then up to four numbers major.minor.revision.changeSet.
    /// Real-world strings seen: "v1.0.2.2", "v1.4.7.5", "e1.8.0", "v1.2.12.66233", "1.0", "v0.2.0", " v1.0 ".
    /// The manifest always keeps the original string; this type is only for comparisons and checks.
    /// </summary>
    public sealed class ModuleVersion : IComparable<ModuleVersion>
    {
        private static readonly Regex Pattern = new Regex(@"^([abdev])?(\d{1,9})(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?(?:\.(\d{1,9}))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private ModuleVersion(char type, int major, int minor, int revision, int changeSet, string text)
        {
            Type = type;
            Major = major;
            Minor = minor;
            Revision = revision;
            ChangeSet = changeSet;
            Text = text;
        }

        /// <summary>Lowercase type letter, or '\0' when absent.</summary>
        public char Type { get; }

        public int Major { get; }

        public int Minor { get; }

        public int Revision { get; }

        public int ChangeSet { get; }

        public string Text { get; }

        /// <summary>Returns null for anything that is not a recognisable version (never throws).</summary>
        public static ModuleVersion TryParse(string text)
        {
            if (text == null)
            {
                return null;
            }

            var m = Pattern.Match(text.Trim());
            if (!m.Success)
            {
                return null;
            }

            int Part(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
            char type = m.Groups[1].Success ? char.ToLowerInvariant(m.Groups[1].Value[0]) : '\0';
            return new ModuleVersion(type, Part(2), Part(3), Part(4), Part(5), text.Trim());
        }

        /// <summary>
        /// Numeric comparison; the type letter is ignored (Bannerlord itself compares numbers; a missing letter equals 'v').
        /// </summary>
        public int CompareTo(ModuleVersion other)
        {
            if (other == null)
            {
                return 1;
            }

            int c = Major.CompareTo(other.Major);
            if (c == 0) c = Minor.CompareTo(other.Minor);
            if (c == 0) c = Revision.CompareTo(other.Revision);
            if (c == 0) c = ChangeSet.CompareTo(other.ChangeSet);
            return c;
        }

        /// <summary>Same comparison but ignoring the change set (how the game matches module versions in practice).</summary>
        public bool SameRelease(ModuleVersion other) => other != null && Major == other.Major && Minor == other.Minor && Revision == other.Revision;

        public override string ToString() => Text;
    }
}
