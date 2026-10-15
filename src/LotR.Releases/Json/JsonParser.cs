using System;
using System.Globalization;
using System.Text;

namespace LotR.Releases.Json
{
    public sealed class JsonParseException : FormatException
    {
        public JsonParseException(string message, int position)
            : base(position >= 0 ? message + " (at character " + position + ")" : message)
        {
            Position = position;
        }

        public int Position { get; }
    }

    /// <summary>
    /// Strict RFC 8259 parser: no comments, no trailing commas, no duplicate keys, nesting limited. A UTF-8 BOM is
    /// accepted by <see cref="Parse(byte[])"/> only (Notepad adds one); signed manifests never carry one.
    /// </summary>
    public static class JsonParser
    {
        private const int MaxDepth = 64;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static JsonNode Parse(byte[] utf8)
        {
            if (utf8 == null)
            {
                throw new ArgumentNullException(nameof(utf8));
            }

            int offset = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
            string text;
            try
            {
                text = StrictUtf8.GetString(utf8, offset, utf8.Length - offset);
            }
            catch (DecoderFallbackException e)
            {
                throw new JsonParseException("Not valid UTF-8: " + e.Message, -1);
            }

            return Parse(text);
        }

        public static JsonNode Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var p = new Reader(text);
            p.SkipWs();
            var v = p.ReadValue(0);
            p.SkipWs();
            if (p.Pos != text.Length)
            {
                throw new JsonParseException("Unexpected content after the JSON value", p.Pos);
            }

            return v;
        }

        private sealed class Reader
        {
            private readonly string s;

            public Reader(string s)
            {
                this.s = s;
            }

            public int Pos;

            public void SkipWs()
            {
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    {
                        Pos++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            private JsonParseException Error(string message) => new JsonParseException(message, Pos);

            public JsonNode ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("Nesting too deep");
                }

                if (Pos >= s.Length)
                {
                    throw Error("Unexpected end of input");
                }

                char c = s[Pos];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return new JsonString(ReadString());
                    case 't':
                        Expect("true");
                        return JsonBool.True;
                    case 'f':
                        Expect("false");
                        return JsonBool.False;
                    case 'n':
                        Expect("null");
                        return JsonNull.Instance;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw Error("Unexpected character '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, Pos, word, 0, word.Length) != 0)
                {
                    throw Error("Invalid literal");
                }

                Pos += word.Length;
            }

            private JsonObject ReadObject(int depth)
            {
                var obj = new JsonObject();
                Pos++; // {
                SkipWs();
                if (Pos < s.Length && s[Pos] == '}')
                {
                    Pos++;
                    return obj;
                }

                while (true)
                {
                    SkipWs();
                    if (Pos >= s.Length || s[Pos] != '"')
                    {
                        throw Error("Expected a string key");
                    }

                    int keyPos = Pos;
                    string key = ReadString();
                    SkipWs();
                    if (Pos >= s.Length || s[Pos] != ':')
                    {
                        throw Error("Expected ':'");
                    }

                    Pos++;
                    SkipWs();
                    var value = ReadValue(depth + 1);
                    try
                    {
                        obj.AddFromParser(key, value);
                    }
                    catch (JsonParseException e)
                    {
                        throw new JsonParseException(e.Message, keyPos);
                    }

                    SkipWs();
                    if (Pos >= s.Length)
                    {
                        throw Error("Unterminated object");
                    }

                    if (s[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }

                    if (s[Pos] == '}')
                    {
                        Pos++;
                        return obj;
                    }

                    throw Error("Expected ',' or '}'");
                }
            }

            private JsonArray ReadArray(int depth)
            {
                var arr = new JsonArray();
                Pos++; // [
                SkipWs();
                if (Pos < s.Length && s[Pos] == ']')
                {
                    Pos++;
                    return arr;
                }

                while (true)
                {
                    SkipWs();
                    arr.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (Pos >= s.Length)
                    {
                        throw Error("Unterminated array");
                    }

                    if (s[Pos] == ',')
                    {
                        Pos++;
                        continue;
                    }

                    if (s[Pos] == ']')
                    {
                        Pos++;
                        return arr;
                    }

                    throw Error("Expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                Pos++; // opening quote
                StringBuilder sb = null;
                int runStart = Pos;
                while (true)
                {
                    if (Pos >= s.Length)
                    {
                        throw Error("Unterminated string");
                    }

                    char c = s[Pos];
                    if (c == '"')
                    {
                        string result;
                        if (sb == null)
                        {
                            result = s.Substring(runStart, Pos - runStart);
                        }
                        else
                        {
                            sb.Append(s, runStart, Pos - runStart);
                            result = sb.ToString();
                        }

                        Pos++;
                        return result;
                    }

                    if (c < 0x20)
                    {
                        throw Error("Control character in string");
                    }

                    if (c != '\\')
                    {
                        Pos++;
                        continue;
                    }

                    if (sb == null)
                    {
                        sb = new StringBuilder();
                    }

                    sb.Append(s, runStart, Pos - runStart);
                    Pos++;
                    if (Pos >= s.Length)
                    {
                        throw Error("Unterminated escape");
                    }

                    char e = s[Pos++];
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
                            if (Pos + 4 > s.Length || !int.TryParse(s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw Error("Invalid \\u escape");
                            }

                            sb.Append((char)code);
                            Pos += 4;
                            break;
                        default:
                            throw Error("Invalid escape");
                    }

                    runStart = Pos;
                }
            }

            private JsonNode ReadNumber()
            {
                int start = Pos;
                if (s[Pos] == '-')
                {
                    Pos++;
                }

                if (Pos >= s.Length)
                {
                    throw Error("Invalid number");
                }

                if (s[Pos] == '0')
                {
                    Pos++;
                }
                else if (s[Pos] >= '1' && s[Pos] <= '9')
                {
                    while (Pos < s.Length && char.IsDigit(s[Pos]) && s[Pos] < 128)
                    {
                        Pos++;
                    }
                }
                else
                {
                    throw Error("Invalid number");
                }

                bool real = false;
                if (Pos < s.Length && s[Pos] == '.')
                {
                    real = true;
                    Pos++;
                    int digits = Pos;
                    while (Pos < s.Length && s[Pos] >= '0' && s[Pos] <= '9')
                    {
                        Pos++;
                    }

                    if (Pos == digits)
                    {
                        throw Error("Invalid number fraction");
                    }
                }

                if (Pos < s.Length && (s[Pos] == 'e' || s[Pos] == 'E'))
                {
                    real = true;
                    Pos++;
                    if (Pos < s.Length && (s[Pos] == '+' || s[Pos] == '-'))
                    {
                        Pos++;
                    }

                    int digits = Pos;
                    while (Pos < s.Length && s[Pos] >= '0' && s[Pos] <= '9')
                    {
                        Pos++;
                    }

                    if (Pos == digits)
                    {
                        throw Error("Invalid number exponent");
                    }
                }

                string text = s.Substring(start, Pos - start);
                if (!real && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long l))
                {
                    return new JsonNumber(l);
                }

                return new JsonNumber(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
        }
    }
}
