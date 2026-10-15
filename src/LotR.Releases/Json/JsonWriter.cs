using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace LotR.Releases.Json
{
    /// <summary>
    /// Two output forms:
    /// - canonical: keys sorted by ordinal UTF-16 order (as RFC 8785/JCS), no whitespace at all, minimal string escaping
    ///   (only '"', '\\' and control characters; everything else, including non-ASCII, as raw UTF-8), integers in plain
    ///   decimal, no trailing newline. These are the bytes that get signed.
    /// - pretty: 2-space indented, insertion order, LF line ends, final LF. For humans only, never signed.
    /// </summary>
    public static class JsonWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);

        public static byte[] ToCanonicalBytes(JsonNode node) => Utf8NoBom.GetBytes(ToCanonicalString(node));

        public static string ToCanonicalString(JsonNode node)
        {
            var sb = new StringBuilder(4096);
            WriteCanonical(sb, node ?? JsonNull.Instance);
            return sb.ToString();
        }

        public static byte[] ToPrettyBytes(JsonNode node) => Utf8NoBom.GetBytes(ToPrettyString(node));

        public static string ToPrettyString(JsonNode node)
        {
            var sb = new StringBuilder(4096);
            WritePretty(sb, node ?? JsonNull.Instance, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteCanonical(StringBuilder sb, JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    sb.Append('{');
                    bool first = true;
                    foreach (var key in o.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        WriteString(sb, key);
                        sb.Append(':');
                        WriteCanonical(sb, o[key]);
                    }

                    sb.Append('}');
                    break;
                case JsonArray a:
                    sb.Append('[');
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }

                        WriteCanonical(sb, a[i]);
                    }

                    sb.Append(']');
                    break;
                default:
                    WriteScalar(sb, node);
                    break;
            }
        }

        private static void WritePretty(StringBuilder sb, JsonNode node, int indent)
        {
            switch (node)
            {
                case JsonObject o when o.Count == 0:
                    sb.Append("{}");
                    break;
                case JsonObject o:
                    sb.Append("{\n");
                    int n = 0;
                    foreach (var kv in o)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        WritePretty(sb, kv.Value, indent + 1);
                        if (++n < o.Count)
                        {
                            sb.Append(',');
                        }

                        sb.Append('\n');
                    }

                    sb.Append(' ', indent * 2).Append('}');
                    break;
                case JsonArray a when a.Count == 0:
                    sb.Append("[]");
                    break;
                case JsonArray a:
                    sb.Append("[\n");
                    for (int i = 0; i < a.Count; i++)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        WritePretty(sb, a[i], indent + 1);
                        if (i + 1 < a.Count)
                        {
                            sb.Append(',');
                        }

                        sb.Append('\n');
                    }

                    sb.Append(' ', indent * 2).Append(']');
                    break;
                default:
                    WriteScalar(sb, node);
                    break;
            }
        }

        private static void WriteScalar(StringBuilder sb, JsonNode node)
        {
            switch (node)
            {
                case JsonNull _:
                    sb.Append("null");
                    break;
                case JsonBool b:
                    sb.Append(b.Value ? "true" : "false");
                    break;
                case JsonNumber num:
                    sb.Append(num.ToJsonText());
                    break;
                case JsonString s:
                    WriteString(sb, s.Value);
                    break;
                default:
                    throw new InvalidOperationException("Unknown JSON node " + node.GetType().Name);
            }
        }

        internal static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }
    }
}
