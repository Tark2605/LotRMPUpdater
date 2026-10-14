using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LotR.GameDetection
{
    /// <summary>Serialises a <see cref="DetectionReport"/> to indented JSON (no external dependency).</summary>
    public static class ReportJson
    {
        /// <param name="maskUserName">Replaces the Windows user name in paths by "&lt;user&gt;" (for support logs).</param>
        public static string Serialize(DetectionReport r, bool maskUserName = false, string toolVersion = null)
        {
            string userName = maskUserName ? Environment.UserName : null;
            string P(string s) => Mask(s, userName);

            var root = new Dictionary<string, object>
            {
                ["tool"] = "lotr-detect",
                ["toolVersion"] = toolVersion,
                ["generatedUtc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["found"] = r.Found,
                ["summary"] = P(r.Summary),
                ["durationMs"] = (long)r.Duration.TotalMilliseconds,
                ["sourcesRun"] = r.SourcesRun.Select(s => (object)s.ToString()).ToList(),
                ["installations"] = r.Installations.Select(i => (object)new Dictionary<string, object>
                {
                    ["store"] = i.Store.ToString(),
                    ["storeEvidence"] = P(i.StoreEvidence),
                    ["gameVersion"] = i.GameVersion,
                    ["gameVersionSource"] = P(i.GameVersionSource),
                    ["rootPath"] = P(i.RootPath),
                    ["canonicalRootPath"] = P(i.CanonicalRootPath),
                    ["modulesPath"] = P(i.ModulesPath),
                    ["executablePath"] = P(i.ExecutablePath),
                    ["binFolder"] = i.BinFolderName,
                    ["isWritable"] = i.IsWritable,
                    ["writeCheck"] = i.WriteCheckMode.ToString(),
                    ["writeCheckDetail"] = i.WriteCheckDetail,
                    ["isProtectedLocation"] = i.IsProtectedLocation,
                    ["primarySource"] = i.PrimarySource.ToString(),
                    ["foundBy"] = i.Discoveries.Select(d => (object)new Dictionary<string, object>
                    {
                        ["source"] = d.Source.ToString(),
                        ["storeHint"] = d.StoreHint.ToString(),
                        ["path"] = P(d.Path),
                        ["detail"] = P(d.Detail),
                    }).ToList(),
                    ["warnings"] = i.Warnings.Select(w => (object)P(w)).ToList(),
                }).ToList(),
                ["rejected"] = r.Rejected.Select(x => (object)new Dictionary<string, object>
                {
                    ["source"] = x.Source.ToString(),
                    ["path"] = P(x.Path),
                    ["reason"] = x.Reason.ToString(),
                    ["message"] = P(x.Message),
                    ["detail"] = P(x.Detail),
                }).ToList(),
                ["hints"] = r.Hints.Select(h => (object)P(h)).ToList(),
                ["sourceErrors"] = r.SourceErrors.Select(e => (object)new Dictionary<string, object>
                {
                    ["source"] = e.Source.ToString(),
                    ["message"] = P(e.Message),
                }).ToList(),
            };

            var sb = new StringBuilder();
            Write(sb, root, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        internal static string Mask(string s, string userName)
        {
            if (s == null || string.IsNullOrEmpty(userName))
                return s;
            // only mask it as a path segment (\Users\name\ ...), case-insensitive
            var needle = "\\" + userName;
            int idx = 0;
            var sb = new StringBuilder();
            while (true)
            {
                int hit = s.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    sb.Append(s, idx, s.Length - idx);
                    return sb.ToString();
                }
                int end = hit + needle.Length;
                bool boundary = end == s.Length || s[end] == '\\' || s[end] == '"' || s[end] == ' ';
                sb.Append(s, idx, hit - idx);
                sb.Append(boundary ? "\\<user>" : s.Substring(hit, needle.Length));
                idx = end;
            }
        }

        private static void Write(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case Dictionary<string, object> d:
                    if (d.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    int k = 0;
                    foreach (var kv in d)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        Write(sb, kv.Value, indent + 1);
                        if (++k < d.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent * 2).Append('}');
                    break;
                case List<object> list:
                    if (list.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int i = 0; i < list.Count; i++)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        Write(sb, list[i], indent + 1);
                        if (i < list.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent * 2).Append(']');
                    break;
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
