using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace LotR.Releases.Json
{
    /// <summary>
    /// Minimal JSON tree. Self-contained on purpose: the canonical (signed) byte form must be fully under our control
    /// and the updater (net48) must not drag System.Text.Json and its dependency chain.
    /// </summary>
    public abstract class JsonNode
    {
        public string AsString() => (this as JsonString)?.Value;

        public long? AsLong() => (this as JsonNumber)?.TryLong();

        public bool? AsBool() => (this as JsonBool)?.Value;

        public JsonObject AsObject() => this as JsonObject;

        public JsonArray AsArray() => this as JsonArray;

        public bool IsNull => this is JsonNull;

        public string ToCanonicalString() => JsonWriter.ToCanonicalString(this);

        public byte[] ToCanonicalBytes() => JsonWriter.ToCanonicalBytes(this);

        public string ToPrettyString() => JsonWriter.ToPrettyString(this);

        public override string ToString() => ToCanonicalString();

        public static implicit operator JsonNode(string value) => value == null ? (JsonNode)JsonNull.Instance : new JsonString(value);

        public static implicit operator JsonNode(long value) => new JsonNumber(value);

        public static implicit operator JsonNode(int value) => new JsonNumber(value);

        public static implicit operator JsonNode(bool value) => value ? JsonBool.True : JsonBool.False;
    }

    public sealed class JsonNull : JsonNode
    {
        public static readonly JsonNull Instance = new JsonNull();

        private JsonNull()
        {
        }
    }

    public sealed class JsonBool : JsonNode
    {
        public static readonly JsonBool True = new JsonBool(true);
        public static readonly JsonBool False = new JsonBool(false);

        private JsonBool(bool value)
        {
            Value = value;
        }

        public bool Value { get; }
    }

    public sealed class JsonString : JsonNode
    {
        public JsonString(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Value { get; }
    }

    /// <summary>
    /// A number. Integers are kept exactly as <see cref="long"/>; anything with a fraction or exponent is kept as its
    /// original text and as a double. The manifest schema only uses integers.
    /// </summary>
    public sealed class JsonNumber : JsonNode
    {
        private readonly long? integer;
        private readonly double real;

        public JsonNumber(long value)
        {
            integer = value;
            real = value;
        }

        public JsonNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "JSON has no NaN or infinity.");
            }

            if (Math.Floor(value) == value && Math.Abs(value) < 9e15)
            {
                integer = (long)value;
            }

            real = value;
        }

        public bool IsInteger => integer.HasValue;

        public long? TryLong() => integer;

        public double AsDouble() => real;

        internal string ToJsonText()
        {
            if (integer.HasValue)
            {
                return integer.Value.ToString(CultureInfo.InvariantCulture);
            }

            return real.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    public sealed class JsonArray : JsonNode, IEnumerable<JsonNode>
    {
        private readonly List<JsonNode> items = new List<JsonNode>();

        public JsonArray()
        {
        }

        public JsonArray(IEnumerable<JsonNode> values)
        {
            foreach (var v in values)
            {
                Add(v);
            }
        }

        public int Count => items.Count;

        public JsonNode this[int index] => items[index];

        public void Add(JsonNode value) => items.Add(value ?? JsonNull.Instance);

        public IEnumerator<JsonNode> GetEnumerator() => items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    }

    /// <summary>JSON object. Keeps insertion order for display; the canonical writer sorts keys (ordinal, UTF-16).</summary>
    public sealed class JsonObject : JsonNode, IEnumerable<KeyValuePair<string, JsonNode>>
    {
        private readonly List<string> order = new List<string>();
        private readonly Dictionary<string, JsonNode> map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        public int Count => order.Count;

        public IEnumerable<string> Keys => order;

        /// <summary>Gets a member or null when absent. Setting null stores JSON null; use <see cref="Remove"/> to drop.</summary>
        public JsonNode this[string key]
        {
            get => map.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (!map.ContainsKey(key))
                {
                    order.Add(key);
                }

                map[key] = value ?? JsonNull.Instance;
            }
        }

        public bool ContainsKey(string key) => map.ContainsKey(key);

        public bool Remove(string key)
        {
            if (map.Remove(key))
            {
                order.Remove(key);
                return true;
            }

            return false;
        }

        /// <summary>Sets the member only when the value is not null (keeps optional members out of the document).</summary>
        public JsonObject SetIfNotNull(string key, JsonNode value)
        {
            if (value != null)
            {
                this[key] = value;
            }

            return this;
        }

        internal void AddFromParser(string key, JsonNode value)
        {
            if (map.ContainsKey(key))
            {
                throw new JsonParseException("Duplicate key \"" + key + "\" (refused: ambiguous in signed content).", -1);
            }

            order.Add(key);
            map[key] = value;
        }

        public IEnumerator<KeyValuePair<string, JsonNode>> GetEnumerator()
        {
            foreach (var k in order)
            {
                yield return new KeyValuePair<string, JsonNode>(k, map[k]);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public JsonObject Clone() => (JsonObject)JsonParser.Parse(ToCanonicalString());
    }
}
