using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace LotR.GameDetection.Parsing
{
    /// <summary>
    /// Hidden "&lt;drive&gt;:\.GamingRoot" file written by the Xbox app on every drive it installs games to.
    /// Observed layout: ASCII magic "RGBX", a little-endian uint32, then NUL-terminated UTF-16LE folder names
    /// relative to the drive root (e.g. "XboxGames").
    /// </summary>
    public static class GamingRootFile
    {
        public static IReadOnlyList<string> Parse(byte[] data)
        {
            var result = new List<string>();
            if (data == null || data.Length < 8 || data[0] != (byte)'R' || data[1] != (byte)'G' || data[2] != (byte)'B' || data[3] != (byte)'X')
                return result;

            int i = 8;
            var sb = new StringBuilder();
            while (i + 1 < data.Length)
            {
                char c = (char)(data[i] | (data[i + 1] << 8));
                i += 2;
                if (c == '\0')
                {
                    if (sb.Length > 0) result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) result.Add(sb.ToString());
            return result;
        }

        /// <summary>Builds the bytes (used by the test bench).</summary>
        public static byte[] Build(params string[] folders)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(Encoding.ASCII.GetBytes("RGBX"), 0, 4);
                ms.Write(BitConverter.GetBytes((uint)folders.Length), 0, 4);
                foreach (var f in folders)
                {
                    var b = Encoding.Unicode.GetBytes(f + "\0");
                    ms.Write(b, 0, b.Length);
                }
                return ms.ToArray();
            }
        }
    }

    /// <summary>
    /// bin\&lt;platform&gt;\Version.xml: &lt;Version&gt;&lt;Singleplayer Value="v1.4.8"/&gt;&lt;/Version&gt;.
    /// Also reads the Version element of a module SubModule.xml as a fallback.
    /// </summary>
    public static class VersionFiles
    {
        public static string ReadGameVersionXml(string xml)
        {
            var doc = Load(xml);
            var root = doc?.DocumentElement;
            if (root == null)
                return null;
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement e && string.Equals(e.Name, "Singleplayer", StringComparison.OrdinalIgnoreCase))
                    return Attr(e, "Value");
            }
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement e && Attr(e, "Value") != null)
                    return Attr(e, "Value");
            }
            return null;
        }

        public static string ReadSubModuleVersion(string xml)
        {
            var doc = Load(xml);
            var root = doc?.DocumentElement;
            if (root == null)
                return null;
            foreach (XmlNode n in root.ChildNodes)
            {
                if (n is XmlElement e && string.Equals(e.Name, "Version", StringComparison.OrdinalIgnoreCase))
                    return Attr(e, "value");
            }
            return null;
        }

        private static XmlDocument Load(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return null;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿')), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                {
                    doc.Load(reader);
                }
                return doc;
            }
            catch
            {
                return null;
            }
        }

        private static string Attr(XmlElement e, string name)
        {
            foreach (XmlAttribute a in e.Attributes)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    return a.Value?.Trim();
            }
            return null;
        }
    }

    /// <summary>Internet shortcut (.url): [InternetShortcut] URL=steam://rungameid/261550.</summary>
    public static class UrlShortcutFile
    {
        public static string ReadUrl(string text)
        {
            if (text == null)
                return null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring(4).Trim();
            }
            return null;
        }
    }
}
