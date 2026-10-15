using System;
using System.IO;
using System.Threading;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace LotR.Releases.Transport
{
    /// <summary>
    /// zstd transport via ZstdSharp.Port (MIT, managed). Publish: level 19, windowLog 27 + long distance matching,
    /// content size and checksum in the frame. Decode: windowLogMax 27 (about 130-150 MB per decoder at most), and the
    /// frame must be a single standard frame with content size and checksum (<see cref="ZstdFrame"/>).
    /// </summary>
    public sealed class ZstdCodec : ITransportCodec
    {
        public const string Ext = "zst";
        public const int Level = 19;
        public const int MaxWindowLog = 27;

        public static readonly ZstdCodec Instance = new ZstdCodec();

        // Level-19 contexts are big; one per thread, reused across objects.
        private static readonly ThreadLocal<Compressor> Compressors = new ThreadLocal<Compressor>(() =>
        {
            var c = new Compressor(Level);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_windowLog, MaxWindowLog);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
            c.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
            return c;
        });

        public string Extension => Ext;

        public byte[] Encode(ArraySegment<byte> data)
        {
            var c = Compressors.Value;
            var dst = new byte[Compressor.GetCompressBound(data.Count)];
            int n = c.Wrap(data, new ArraySegment<byte>(dst));
            Array.Resize(ref dst, n);
            return dst;
        }

        public byte[] Decode(byte[] objectBytes, long expectedSize)
        {
            CheckStructure(objectBytes, expectedSize);
            var output = new byte[expectedSize];
            using (var d = new Decompressor())
            {
                d.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
                int n;
                try
                {
                    n = d.Unwrap(objectBytes, 0, objectBytes.Length, output, 0, output.Length);
                }
                catch (ZstdException e)
                {
                    throw new InvalidDataException("zstd object does not decode: " + e.Message, e);
                }

                if (n != expectedSize)
                {
                    throw new InvalidDataException("zstd object decoded to " + n + " bytes, expected " + expectedSize);
                }
            }

            return output;
        }

        public Stream OpenDecoded(Stream objectStream)
        {
            var z = new DecompressionStream(objectStream, 1 << 20, false, false);
            z.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog);
            return z;
        }

        public void CheckStructure(byte[] objectBytes, long expectedSize)
        {
            var info = ZstdFrame.Inspect(objectBytes);
            if (info.ContentSize != expectedSize)
            {
                throw new InvalidDataException("zstd frame declares " + info.ContentSize + " bytes, expected " + expectedSize);
            }
        }
    }

    /// <summary>
    /// Reads a zstd frame header and walks its block headers (RFC 8878 §3.1.1) without decoding, to enforce the
    /// contract: exactly one standard frame, no dictionary, content size present, checksum present,
    /// window &lt;= 2^27, nothing after the frame.
    /// </summary>
    public static class ZstdFrame
    {
        public const uint Magic = 0xFD2FB528;

        public sealed class Info
        {
            public long ContentSize { get; set; }

            public long WindowSize { get; set; }

            public bool SingleSegment { get; set; }

            public bool HasChecksum { get; set; }

            public long FrameLength { get; set; }

            public int Blocks { get; set; }
        }

        public static Info Inspect(byte[] b)
        {
            if (b.Length < 6)
            {
                throw Bad("too short for a zstd frame");
            }

            uint magic = (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
            if (magic != Magic)
            {
                throw Bad("not a zstd frame (magic 0x" + magic.ToString("X8") + ")");
            }

            int fhd = b[4];
            int fcsFlag = fhd >> 6;
            bool single = ((fhd >> 5) & 1) == 1;
            bool reserved = ((fhd >> 3) & 1) == 1;
            bool checksum = ((fhd >> 2) & 1) == 1;
            int dictFlag = fhd & 3;
            if (reserved)
            {
                throw Bad("reserved header bit set");
            }

            if (dictFlag != 0)
            {
                throw Bad("dictionary frames are not allowed");
            }

            if (!checksum)
            {
                throw Bad("frame has no content checksum");
            }

            int pos = 5;
            long window = 0;
            if (!single)
            {
                int wd = b[pos++];
                int exponent = wd >> 3;
                int mantissa = wd & 7;
                int windowLog = 10 + exponent;
                if (windowLog > ZstdCodec.MaxWindowLog)
                {
                    throw Bad("window log " + windowLog + " exceeds " + ZstdCodec.MaxWindowLog);
                }

                long baseSize = 1L << windowLog;
                window = baseSize + (baseSize / 8) * mantissa;
                if (window > (1L << ZstdCodec.MaxWindowLog))
                {
                    throw Bad("window " + window + " exceeds 2^" + ZstdCodec.MaxWindowLog);
                }
            }

            int fcsSize = fcsFlag == 0 ? (single ? 1 : 0) : fcsFlag == 1 ? 2 : fcsFlag == 2 ? 4 : 8;
            if (fcsSize == 0)
            {
                throw Bad("frame does not declare its content size");
            }

            if (pos + fcsSize > b.Length)
            {
                throw Bad("truncated header");
            }

            ulong fcs = 0;
            for (int i = 0; i < fcsSize; i++)
            {
                fcs |= (ulong)b[pos + i] << (8 * i);
            }

            if (fcsSize == 2)
            {
                fcs += 256;
            }

            pos += fcsSize;
            if (fcs > long.MaxValue)
            {
                throw Bad("absurd content size");
            }

            if (single)
            {
                window = (long)fcs;
                if (window > (1L << ZstdCodec.MaxWindowLog))
                {
                    throw Bad("single-segment window " + window + " exceeds 2^" + ZstdCodec.MaxWindowLog);
                }
            }

            int blocks = 0;
            while (true)
            {
                if (pos + 3 > b.Length)
                {
                    throw Bad("truncated block header");
                }

                int bh = b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16);
                pos += 3;
                bool last = (bh & 1) == 1;
                int type = (bh >> 1) & 3;
                int size = bh >> 3;
                long advance;
                switch (type)
                {
                    case 0: advance = size; break; // raw
                    case 1: advance = 1; break; // RLE
                    case 2: advance = size; break; // compressed
                    default: throw Bad("reserved block type");
                }

                if (pos + advance > b.Length)
                {
                    throw Bad("truncated block");
                }

                pos += (int)advance;
                blocks++;
                if (last)
                {
                    break;
                }
            }

            pos += 4; // checksum
            if (pos > b.Length)
            {
                throw Bad("missing checksum");
            }

            if (pos != b.Length)
            {
                throw Bad((b.Length - pos) + " bytes after the frame (only one frame allowed)");
            }

            return new Info { ContentSize = (long)fcs, WindowSize = window, SingleSegment = single, HasChecksum = checksum, FrameLength = pos, Blocks = blocks };
        }

        private static InvalidDataException Bad(string why) => new InvalidDataException("zstd object rejected: " + why);
    }
}
