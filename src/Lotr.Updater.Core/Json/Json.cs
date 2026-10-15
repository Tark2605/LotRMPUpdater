using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Lotr.Updater.Core.Json
{
    /// <summary>
    /// Minimal JSON reader/writer (no dependency, keeps the exe small). Objects become
    /// <see cref="JsonObject"/> (ordinal key order preserved), arrays List&lt;object&gt;, numbers double or long,
    /// strings, bools, null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            var p = new Parser(text);
            p.SkipWs();
            var v = p.ReadValue(0);
            p.SkipWs();
            if (!p.End) throw new FormatException("Trailing characters after JSON value at " + p.Pos);
            return v;
        }

        public static JsonObject ParseObject(string text)
        {
            return Parse(text) as JsonObject ?? throw new FormatException("JSON root is not an object");
        }

        /// <summary>Writes a value. <paramref name="indent"/> false gives the compact form (with sorted keys when
        /// <paramref name="sortKeys"/> is true: the canonical form of CONTRACTS §2).</summary>
        public static string Write(object value, bool indent = true, bool sortKeys = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indent, sortKeys, 0);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, bool indent, bool sort, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    if (Math.Abs(d % 1) < double.Epsilon && Math.Abs(d) < 9e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonObject o:
                    {
                        var keys = sort ? o.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : o.Keys.ToList();
                        if (keys.Count == 0) { sb.Append("{}"); break; }
                        sb.Append('{');
                        for (int k = 0; k < keys.Count; k++)
                        {
                            if (k > 0) sb.Append(',');
                            NewLine(sb, indent, depth + 1);
                            WriteString(sb, keys[k]);
                            sb.Append(indent ? ": " : ":");
                            WriteValue(sb, o[keys[k]], indent, sort, depth + 1);
                        }
                        NewLine(sb, indent, depth);
                        sb.Append('}');
                        break;
                    }
                case System.Collections.IEnumerable list:
                    {
                        var items = list.Cast<object>().ToList();
                        if (items.Count == 0) { sb.Append("[]"); break; }
                        sb.Append('[');
                        for (int k = 0; k < items.Count; k++)
                        {
                            if (k > 0) sb.Append(',');
                            NewLine(sb, indent, depth + 1);
                            WriteValue(sb, items[k], indent, sort, depth + 1);
                        }
                        NewLine(sb, indent, depth);
                        sb.Append(']');
                        break;
                    }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Parser
        {
            private readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length && (_s[Pos] == ' ' || _s[Pos] == '\t' || _s[Pos] == '\n' || _s[Pos] == '\r' || _s[Pos] == '﻿')) Pos++;
            }

            public object ReadValue(int depth)
            {
                if (depth > 64) throw new FormatException("JSON nested too deeply");
                if (End) throw new FormatException("Unexpected end of JSON");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw new FormatException($"Unexpected '{c}' at {Pos}");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0) throw new FormatException("Invalid literal at " + Pos);
                Pos += word.Length;
            }

            private JsonObject ReadObject(int depth)
            {
                var o = new JsonObject();
                Pos++;
                SkipWs();
                if (!End && _s[Pos] == '}') { Pos++; return o; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[Pos] != '"') throw new FormatException("Expected key at " + Pos);
                    var key = ReadString();
                    SkipWs();
                    if (End || _s[Pos] != ':') throw new FormatException("Expected ':' at " + Pos);
                    Pos++;
                    SkipWs();
                    o[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (End) throw new FormatException("Unterminated object");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return o; }
                    throw new FormatException("Expected ',' or '}' at " + Pos);
                }
            }

            private List<object> ReadArray(int depth)
            {
                var a = new List<object>();
                Pos++;
                SkipWs();
                if (!End && _s[Pos] == ']') { Pos++; return a; }
                while (true)
                {
                    SkipWs();
                    a.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (End) throw new FormatException("Unterminated array");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return a; }
                    throw new FormatException("Expected ',' or ']' at " + Pos);
                }
            }

            private string ReadString()
            {
                Pos++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (End) throw new FormatException("Unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new FormatException("Bad escape");
                    char e = _s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new FormatException("Bad \\u escape");
                            sb.Append((char)int.Parse(_s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException("Bad escape \\" + e);
                    }
                }
            }

            private object ReadNumber()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                bool isFloat = false;
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (c >= '0' && c <= '9') { Pos++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { isFloat = true; Pos++; continue; }
                    break;
                }
                var t = _s.Substring(start, Pos - start);
                if (!isFloat && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>JSON object with typed accessors that tolerate missing keys.</summary>
    public sealed class JsonObject : Dictionary<string, object>
    {
        public JsonObject() : base(StringComparer.Ordinal) { }

        public string Str(string key) => TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        public long Long(string key, long fallback = 0)
        {
            if (!TryGetValue(key, out var v) || v == null) return fallback;
            if (v is long l) return l;
            if (v is double d) return (long)d;
            return long.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : fallback;
        }

        public bool Bool(string key, bool fallback = false) => TryGetValue(key, out var v) && v is bool b ? b : fallback;

        public JsonObject Obj(string key) => TryGetValue(key, out var v) ? v as JsonObject : null;

        public List<object> Arr(string key) => TryGetValue(key, out var v) ? v as List<object> : null;

        public IEnumerable<JsonObject> Objs(string key) => (Arr(key) ?? new List<object>()).OfType<JsonObject>();
    }
}
