using System;
using System.Collections.Generic;
using System.Text;

namespace LotR.GameDetection.Parsing
{
    /// <summary>A node of a Valve KeyValues (VDF/ACF) text file.</summary>
    public sealed class VdfNode
    {
        public string Value { get; set; }

        /// <summary>Children in file order; keys are compared case-insensitively.</summary>
        public List<KeyValuePair<string, VdfNode>> Children { get; } = new List<KeyValuePair<string, VdfNode>>();

        public VdfNode this[string key]
        {
            get
            {
                foreach (var kv in Children)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                return null;
            }
        }

        public string GetString(string key) => this[key]?.Value;
    }

    /// <summary>Parser for Steam's libraryfolders.vdf and appmanifest_*.acf (text KeyValues).</summary>
    public static class Vdf
    {
        public static VdfNode Parse(string text)
        {
            var root = new VdfNode();
            var stack = new Stack<VdfNode>();
            stack.Push(root);
            int i = 0;
            string pendingKey = null;

            while (true)
            {
                var tok = NextToken(text, ref i, out bool quoted);
                if (tok == null)
                    break;

                if (!quoted && tok == "{")
                {
                    var child = new VdfNode();
                    stack.Peek().Children.Add(new KeyValuePair<string, VdfNode>(pendingKey ?? string.Empty, child));
                    stack.Push(child);
                    pendingKey = null;
                }
                else if (!quoted && tok == "}")
                {
                    if (stack.Count > 1)
                        stack.Pop();
                    pendingKey = null;
                }
                else if (!quoted && tok.StartsWith("[", StringComparison.Ordinal))
                {
                    // conditional like [$WIN32]: ignore
                }
                else if (pendingKey == null)
                {
                    pendingKey = tok;
                }
                else
                {
                    stack.Peek().Children.Add(new KeyValuePair<string, VdfNode>(pendingKey, new VdfNode { Value = tok }));
                    pendingKey = null;
                }
            }
            return root;
        }

        private static string NextToken(string s, ref int i, out bool quoted)
        {
            quoted = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                break;
            }
            if (i >= s.Length)
                return null;

            char first = s[i];
            if (first == '{' || first == '}')
            {
                i++;
                return first.ToString();
            }

            var sb = new StringBuilder();
            if (first == '"')
            {
                quoted = true;
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length)
                    {
                        char e = s[i + 1];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append('\\').Append(e); break;
                        }
                        i += 2;
                        continue;
                    }
                    sb.Append(s[i++]);
                }
                i++; // closing quote
                return sb.ToString();
            }

            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}' && s[i] != '"')
                sb.Append(s[i++]);
            return sb.ToString();
        }
    }
}
