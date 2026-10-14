using System;
using System.Text;
using LotR.GameDetection.Abstractions;

namespace LotR.GameDetection.Parsing
{
    /// <summary>
    /// Reads Windows .lnk files without COM, following [MS-SHLLINK]: LinkInfo (local base path or network share
    /// + common path suffix, Unicode variants preferred), StringData (relative path, working dir, arguments) and the
    /// EnvironmentVariableDataBlock. Shortcuts that only carry an ID list (rare for games) report an error.
    /// Reads go through <see cref="IFileSystem"/>, so the test bench can feed generated .lnk bytes.
    /// </summary>
    public sealed class LnkShortcutResolver : IShortcutResolver
    {
        private const uint HasLinkTargetIdList = 0x1;
        private const uint HasLinkInfo = 0x2;
        private const uint HasName = 0x4;
        private const uint HasRelativePath = 0x8;
        private const uint HasWorkingDir = 0x10;
        private const uint HasArguments = 0x20;
        private const uint HasIconLocation = 0x40;
        private const uint IsUnicode = 0x80;
        private const uint EnvironmentBlockSignature = 0xA0000001;

        private readonly IFileSystem _fs;
        private readonly ISystemEnvironment _env;

        public LnkShortcutResolver(IFileSystem fs, ISystemEnvironment env)
        {
            _fs = fs;
            _env = env;
        }

        public ShortcutTarget Resolve(string shortcutPath)
        {
            byte[] data;
            try
            {
                data = _fs.ReadAllBytes(shortcutPath);
            }
            catch (Exception ex)
            {
                return new ShortcutTarget { Error = "cannot read shortcut: " + ex.Message };
            }

            try
            {
                var result = Parse(data);
                if (result.Error != null)
                    return result;

                if (result.TargetPath != null && result.TargetPath.IndexOf('%') >= 0 && _env != null)
                    result.TargetPath = _env.ExpandEnvironmentVariables(result.TargetPath);

                if (result.TargetPath == null && !string.IsNullOrEmpty(result.RelativePath))
                    result.TargetPath = PathUtil.Combine(PathUtil.GetParent(shortcutPath), result.RelativePath);

                if (result.TargetPath == null)
                    result.Error = "shortcut has no file system target (ID list only)";
                else
                    result.TargetPath = PathUtil.Normalize(result.TargetPath);
                return result;
            }
            catch (Exception ex)
            {
                return new ShortcutTarget { Error = "invalid shortcut file: " + ex.Message };
            }
        }

        /// <summary>Parses .lnk bytes. TargetPath may still contain %VARIABLES%.</summary>
        public static ShortcutTarget Parse(byte[] d)
        {
            if (d.Length < 76 || U32(d, 0) != 0x4C)
                return new ShortcutTarget { Error = "not a shell link (bad header)" };

            uint flags = U32(d, 20);
            bool unicode = (flags & IsUnicode) != 0;
            int offset = 76;
            string target = null;

            if ((flags & HasLinkTargetIdList) != 0)
                offset += 2 + U16(d, offset);

            if ((flags & HasLinkInfo) != 0)
            {
                int li = offset;
                int liSize = (int)U32(d, li);
                int liHeaderSize = (int)U32(d, li + 4);
                uint liFlags = U32(d, li + 8);
                int localBaseOff = (int)U32(d, li + 16);
                int cnrlOff = (int)U32(d, li + 20);
                int suffixOff = (int)U32(d, li + 24);
                bool hasUnicodeOffsets = liHeaderSize >= 0x24;

                string suffix = hasUnicodeOffsets && U32(d, li + 32) != 0
                    ? Utf16Z(d, li + (int)U32(d, li + 32))
                    : AnsiZ(d, li + suffixOff);

                if ((liFlags & 0x1) != 0)
                {
                    string local = hasUnicodeOffsets && U32(d, li + 28) != 0
                        ? Utf16Z(d, li + (int)U32(d, li + 28))
                        : AnsiZ(d, li + localBaseOff);
                    target = local + suffix;
                }
                else if ((liFlags & 0x2) != 0)
                {
                    int c = li + cnrlOff;
                    int netNameOff = (int)U32(d, c + 8);
                    string net = netNameOff > 0x14 && U32(d, c + 20) != 0
                        ? Utf16Z(d, c + (int)U32(d, c + 20))
                        : AnsiZ(d, c + netNameOff);
                    target = string.IsNullOrEmpty(suffix) ? net : net.TrimEnd('\\') + "\\" + suffix;
                }
                offset = li + liSize;
            }

            string rel = null, workDir = null, args = null;
            if ((flags & HasName) != 0) ReadStringData(d, ref offset, unicode);
            if ((flags & HasRelativePath) != 0) rel = ReadStringData(d, ref offset, unicode);
            if ((flags & HasWorkingDir) != 0) workDir = ReadStringData(d, ref offset, unicode);
            if ((flags & HasArguments) != 0) args = ReadStringData(d, ref offset, unicode);
            if ((flags & HasIconLocation) != 0) ReadStringData(d, ref offset, unicode);

            // Extra data blocks
            while (target == null && offset + 8 <= d.Length)
            {
                int blockSize = (int)U32(d, offset);
                if (blockSize < 8 || offset + blockSize > d.Length)
                    break;
                if (U32(d, offset + 4) == EnvironmentBlockSignature && blockSize >= 0x314)
                {
                    var uni = Utf16Z(d, offset + 268);
                    target = string.IsNullOrEmpty(uni) ? AnsiZ(d, offset + 8) : uni;
                }
                offset += blockSize;
            }

            return new ShortcutTarget { TargetPath = string.IsNullOrEmpty(target) ? null : target, Arguments = args, WorkingDirectory = workDir, RelativePath = rel };
        }

        private static string ReadStringData(byte[] d, ref int offset, bool unicode)
        {
            int count = U16(d, offset);
            offset += 2;
            int bytes = unicode ? count * 2 : count;
            string s = unicode ? Encoding.Unicode.GetString(d, offset, bytes) : Encoding.Default.GetString(d, offset, bytes);
            offset += bytes;
            return s;
        }

        private static uint U32(byte[] d, int o) => o + 4 <= d.Length ? BitConverter.ToUInt32(d, o) : throw new FormatException("truncated");
        private static int U16(byte[] d, int o) => o + 2 <= d.Length ? BitConverter.ToUInt16(d, o) : throw new FormatException("truncated");

        private static string AnsiZ(byte[] d, int o)
        {
            int end = o;
            while (end < d.Length && d[end] != 0) end++;
            return Encoding.Default.GetString(d, o, end - o);
        }

        private static string Utf16Z(byte[] d, int o)
        {
            int end = o;
            while (end + 1 < d.Length && (d[end] != 0 || d[end + 1] != 0)) end += 2;
            return Encoding.Unicode.GetString(d, o, end - o);
        }
    }
}
