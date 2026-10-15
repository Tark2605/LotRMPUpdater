using System;
using System.IO;

namespace LotR.Releases.Chunking
{
    /// <summary>
    /// FastCDC 2020 content-defined chunker (Xia et al., normalised chunking level 1), ported from the reference
    /// fastcdc (Rust, v2020) / pyfastcdc 0.3.0, rolling two bytes per step with the left-shifted gear table.
    /// Contract parameters (CONTRACTS §1): average 256 KiB, min 64 KiB, max 1 MiB, seed 0. Output is pinned by test
    /// vectors generated with pyfastcdc (tests/LotR.Releases.Tests/Data/fastcdc-vectors.txt).
    /// </summary>
    public sealed class FastCdc
    {
        public const int ContractAverage = 256 * 1024;
        public const int ContractMin = 64 * 1024;
        public const int ContractMax = 1024 * 1024;
        public const int NormalizationLevel = 1;

        /// <summary>Files of this size or more are chunked (CONTRACTS §1).</summary>
        public const long ChunkThreshold = 16L * 1024 * 1024;

        public static readonly FastCdc Contract = new FastCdc(ContractAverage, ContractMin, ContractMax);

        private static readonly ulong[] GearLs = BuildGearLs();

        private readonly ulong maskS;
        private readonly ulong maskL;
        private readonly ulong maskSLs;
        private readonly ulong maskLLs;

        public FastCdc(int avg, int min, int max)
        {
            if (!(min <= avg && avg <= max) || min < 64 || max > 16 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(avg), "need 64 <= min <= avg <= max <= 16 MiB");
            }

            Average = avg;
            Min = min;
            Max = max;
            int bits = Log2(avg);
            maskS = FastCdcTables.Masks[bits + NormalizationLevel];
            maskL = FastCdcTables.Masks[bits - NormalizationLevel];
            maskSLs = maskS << 1;
            maskLLs = maskL << 1;
        }

        public int Average { get; }

        public int Min { get; }

        public int Max { get; }

        /// <summary>Length of the next chunk starting at <paramref name="offset"/>, given <paramref name="available"/> bytes.</summary>
        public int Cut(byte[] buf, int offset, int available)
        {
            int remaining = available;
            if (remaining <= Min)
            {
                return remaining;
            }

            int center = Average;
            if (remaining > Max)
            {
                remaining = Max;
            }
            else if (remaining < center)
            {
                center = remaining;
            }

            ulong[] gear = FastCdcTables.Gear;
            ulong hash = 0;
            int startPos = (Min / 2) * 2;
            int midPos = (center / 2) * 2;
            int endPos = (remaining / 2) * 2;
            int pos = startPos;
            for (; pos < midPos; pos += 2)
            {
                hash = (hash << 2) + GearLs[buf[offset + pos]];
                if ((hash & maskSLs) == 0)
                {
                    return pos;
                }

                hash += gear[buf[offset + pos + 1]];
                if ((hash & maskS) == 0)
                {
                    return pos + 1;
                }
            }

            for (; pos < endPos; pos += 2)
            {
                hash = (hash << 2) + GearLs[buf[offset + pos]];
                if ((hash & maskLLs) == 0)
                {
                    return pos;
                }

                hash += gear[buf[offset + pos + 1]];
                if ((hash & maskL) == 0)
                {
                    return pos + 1;
                }
            }

            return remaining;
        }

        /// <summary>
        /// Splits a stream. The callback gets each chunk (valid only during the call) and its file offset.
        /// Memory: one buffer of 2 x max.
        /// </summary>
        public void Split(Stream input, Action<ArraySegment<byte>, long> onChunk)
        {
            var buf = new byte[Max + Math.Max(8 * 1024, Max)];
            int read = 0;  // start of unconsumed data
            int write = 0; // end of data in buffer
            bool eof = false;
            long fileOffset = 0;
            while (true)
            {
                if (!eof && write - read < Max)
                {
                    if (read > 0)
                    {
                        Buffer.BlockCopy(buf, read, buf, 0, write - read);
                        write -= read;
                        read = 0;
                    }

                    while (write < buf.Length)
                    {
                        int n = input.Read(buf, write, buf.Length - write);
                        if (n == 0)
                        {
                            eof = true;
                            break;
                        }

                        write += n;
                    }
                }

                int available = write - read;
                if (available == 0)
                {
                    return;
                }

                int len = Cut(buf, read, available);
                if (len == 0)
                {
                    len = available;
                }

                onChunk(new ArraySegment<byte>(buf, read, len), fileOffset);
                read += len;
                fileOffset += len;
            }
        }

        private static ulong[] BuildGearLs()
        {
            var ls = new ulong[256];
            for (int i = 0; i < 256; i++)
            {
                ls[i] = FastCdcTables.Gear[i] << 1;
            }

            return ls;
        }

        private static int Log2(int v)
        {
            // Same as pyfastcdc utils.logarithm2: round(log2(v)).
            return (int)Math.Round(Math.Log(v, 2));
        }
    }
}
