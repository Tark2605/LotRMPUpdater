using System;
using System.Collections.Generic;
using System.IO;

namespace LotR.Releases.Transport
{
    /// <summary>
    /// How an object is stored and sent (CONTRACTS §1, decided 15/10, docs/COMPRESSION.md). Object identity is ALWAYS
    /// the SHA-256 of the uncompressed bytes; the codec only decides the bytes on the wire and the object extension.
    /// Known: "zst" (zstd 19, windowLog &lt;= 27, single frame, content size + checksum) and "raw".
    /// "zpatch" is reserved for phase 2 and deliberately NOT registered: the updater refuses unknown extensions.
    /// </summary>
    public interface ITransportCodec
    {
        /// <summary>File extension and manifest identifier, lowercase, without dot.</summary>
        string Extension { get; }

        /// <summary>Encodes a whole object held in memory (objects are at most 16 MiB: bigger files are chunked).</summary>
        byte[] Encode(ArraySegment<byte> data);

        /// <summary>Decodes a whole object held in memory; throws <see cref="InvalidDataException"/> on any defect.</summary>
        byte[] Decode(byte[] objectBytes, long expectedSize);

        /// <summary>Streaming decoder (updater side). The caller hashes what it reads.</summary>
        Stream OpenDecoded(Stream objectStream);

        /// <summary>Structural checks that do not need decoding (frame rules for zst); throws InvalidDataException.</summary>
        void CheckStructure(byte[] objectBytes, long expectedSize);
    }

    public sealed class RawCodec : ITransportCodec
    {
        public const string Ext = "raw";

        public static readonly RawCodec Instance = new RawCodec();

        public string Extension => Ext;

        public byte[] Encode(ArraySegment<byte> data)
        {
            var copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            return copy;
        }

        public byte[] Decode(byte[] objectBytes, long expectedSize)
        {
            CheckStructure(objectBytes, expectedSize);
            return objectBytes;
        }

        public Stream OpenDecoded(Stream objectStream) => objectStream;

        public void CheckStructure(byte[] objectBytes, long expectedSize)
        {
            if (objectBytes.LongLength != expectedSize)
            {
                throw new InvalidDataException("raw object is " + objectBytes.LongLength + " bytes, expected " + expectedSize);
            }
        }
    }

    public static class TransportCodecs
    {
        private static readonly Dictionary<string, ITransportCodec> Codecs = new Dictionary<string, ITransportCodec>(StringComparer.Ordinal)
        {
            [RawCodec.Ext] = RawCodec.Instance,
            [ZstdCodec.Ext] = ZstdCodec.Instance,
        };

        /// <summary>Reserved for zstd --patch-from binary patches (phase 2). Never produced, never accepted yet.</summary>
        public const string ReservedPatchExt = "zpatch";

        /// <summary>CONTRACTS §1: an object is stored raw when zstd saves less than 3 %.</summary>
        public const double MinSaving = 0.03;

        public static ITransportCodec Find(string extension) => extension != null && Codecs.TryGetValue(extension, out var c) ? c : null;

        public static ITransportCodec Get(string extension) => Find(extension) ?? throw new InvalidDataException("Unknown object transport '" + extension + "'");

        public static IEnumerable<string> Known => Codecs.Keys;

        /// <summary>
        /// Publish-side rule: compress with zstd, keep it only when it saves at least 3 %; otherwise raw.
        /// Empty input is always raw.
        /// </summary>
        public static EncodedObject EncodeBest(ArraySegment<byte> data)
        {
            if (data.Count == 0)
            {
                return new EncodedObject(RawCodec.Ext, new byte[0]);
            }

            byte[] z = ZstdCodec.Instance.Encode(data);
            if (z.LongLength <= (long)Math.Floor(data.Count * (1.0 - MinSaving)))
            {
                return new EncodedObject(ZstdCodec.Ext, z);
            }

            return new EncodedObject(RawCodec.Ext, RawCodec.Instance.Encode(data));
        }
    }

    public sealed class EncodedObject
    {
        public EncodedObject(string extension, byte[] bytes)
        {
            Extension = extension;
            Bytes = bytes;
        }

        public string Extension { get; }

        public byte[] Bytes { get; }
    }
}
