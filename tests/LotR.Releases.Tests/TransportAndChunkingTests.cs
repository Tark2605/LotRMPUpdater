using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LotR.Releases.Chunking;
using LotR.Releases.Hashing;
using LotR.Releases.Transport;
using Xunit;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace LotR.Releases.Tests
{
    public class FastCdcTests
    {
        public static IEnumerable<object[]> Vectors()
        {
            foreach (var line in File.ReadAllLines(TestData.DataFile("fastcdc-vectors.txt")))
            {
                var p = line.Split(' ');
                if (p.Length == 7)
                {
                    yield return new object[] { p[0], int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture), int.Parse(p[3], CultureInfo.InvariantCulture), int.Parse(p[4], CultureInfo.InvariantCulture), p[5], p[6] };
                }
            }
        }

        private static byte[] Input(string name, int length)
        {
            switch (name)
            {
                case "rand4m": return TestData.SplitMix(1, length);
                case "rand3m_odd": return TestData.SplitMix(2, length);
                case "zero3m": return new byte[length];
                case "small_params": return TestData.SplitMix(3, length);
                case "tiny": return TestData.SplitMix(4, length);
                default: throw new ArgumentException(name);
            }
        }

        [Fact]
        public void SplitMix_generator_matches_the_python_one()
        {
            var expected = File.ReadAllLines(TestData.DataFile("fastcdc-vectors.txt")).Single(l => l.StartsWith("SPLITMIX_SHA_rand4m", StringComparison.Ordinal)).Split(' ')[1];
            Assert.Equal(expected, Sha256Util.Of(TestData.SplitMix(1, 4 * 1024 * 1024)));
        }

        /// <summary>Vectors produced by pyfastcdc 0.3.0 (FastCDC 2020, nc=1, seed 0): Data/gen-fastcdc-vectors.py.</summary>
        [Theory]
        [MemberData(nameof(Vectors))]
        public void Cuts_exactly_like_the_reference(string name, int length, int avg, int min, int max, string lengths, string digest)
        {
            var data = Input(name, length);
            var cdc = new FastCdc(avg, min, max);
            var got = new List<int>();
            var hashes = new List<byte[]>();
            long expectedOffset = 0;
            cdc.Split(new MemoryStream(data), (seg, offset) =>
            {
                Assert.Equal(expectedOffset, offset);
                expectedOffset += seg.Count;
                got.Add(seg.Count);
                hashes.Add(Sha256Util.FromHex(Sha256Util.Of(seg)));
            });
            Assert.Equal(lengths, string.Join(",", got));
            Assert.Equal(digest, Sha256Util.Of(hashes.SelectMany(h => h).ToArray()));
        }

        [Fact]
        public void Stream_reads_in_small_pieces_give_the_same_cuts()
        {
            var data = TestData.SplitMix(1, 4 * 1024 * 1024);
            var a = new List<int>();
            var b = new List<int>();
            FastCdc.Contract.Split(new MemoryStream(data), (s, o) => a.Add(s.Count));
            FastCdc.Contract.Split(new TrickleStream(data, 777), (s, o) => b.Add(s.Count));
            Assert.Equal(a, b);
        }

        [Fact]
        public void An_insertion_only_changes_nearby_chunks()
        {
            var data = TestData.SplitMix(9, 8 * 1024 * 1024);
            var edited = data.Take(3_000_000).Concat(Encoding.ASCII.GetBytes("inserted bytes")).Concat(data.Skip(3_000_000)).ToArray();
            var before = Chunks(data);
            var after = Chunks(edited);
            int shared = after.Count(before.Contains);
            Assert.True(shared >= after.Count - 3, shared + "/" + after.Count + " chunks shared");
        }

        private static HashSet<string> Chunks(byte[] d)
        {
            var set = new HashSet<string>();
            FastCdc.Contract.Split(new MemoryStream(d), (s, o) => set.Add(Sha256Util.Of(s)));
            return set;
        }

        private sealed class TrickleStream : MemoryStream
        {
            private readonly int step;

            public TrickleStream(byte[] data, int step)
                : base(data)
            {
                this.step = step;
            }

            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, step));
        }
    }

    public class TransportTests
    {
        [Fact]
        public void Text_is_stored_as_zst_and_round_trips()
        {
            var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<item id=\"lotr_sword\" weight=\"1.5\"/>\n", 5000)));
            var enc = TransportCodecs.EncodeBest(new ArraySegment<byte>(text));
            Assert.Equal("zst", enc.Extension);
            Assert.True(enc.Bytes.Length < text.Length / 10);
            var info = ZstdFrame.Inspect(enc.Bytes);
            Assert.Equal(text.Length, info.ContentSize);
            Assert.True(info.HasChecksum);
            Assert.Equal(text, ZstdCodec.Instance.Decode(enc.Bytes, text.Length));
            using (var s = ZstdCodec.Instance.OpenDecoded(new MemoryStream(enc.Bytes)))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                Assert.Equal(text, ms.ToArray());
            }
        }

        [Fact]
        public void Incompressible_data_is_stored_raw_by_the_3_percent_rule()
        {
            var noise = TestData.SplitMix(5, 300000);
            Assert.Equal("raw", TransportCodecs.EncodeBest(new ArraySegment<byte>(noise)).Extension);

            // 96 % noise + 4 % zeros: zstd saves a bit more than 3 % -> zst; 98/2 -> raw.
            var mostlyNoise = noise.Take(288000).Concat(new byte[12000]).ToArray();
            var almostNoise = noise.Take(294000).Concat(new byte[6000]).ToArray();
            Assert.Equal("zst", TransportCodecs.EncodeBest(new ArraySegment<byte>(mostlyNoise)).Extension);
            Assert.Equal("raw", TransportCodecs.EncodeBest(new ArraySegment<byte>(almostNoise)).Extension);
            Assert.Equal("raw", TransportCodecs.EncodeBest(new ArraySegment<byte>(new byte[0])).Extension);
        }

        [Fact]
        public void Frame_without_checksum_is_refused()
        {
            var data = Encoding.ASCII.GetBytes(new string('a', 10000));
            using (var c = new Compressor(3))
            {
                c.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
                var bytes = c.Wrap(data).ToArray();
                var e = Assert.Throws<InvalidDataException>(() => ZstdCodec.Instance.Decode(bytes, data.Length));
                Assert.Contains("checksum", e.Message);
            }
        }

        [Fact]
        public void Two_frames_or_trailing_bytes_are_refused()
        {
            var data = Encoding.ASCII.GetBytes(new string('b', 5000));
            var one = ZstdCodec.Instance.Encode(new ArraySegment<byte>(data));
            var two = one.Concat(one).ToArray();
            Assert.Throws<InvalidDataException>(() => ZstdCodec.Instance.Decode(two, data.Length * 2));
            Assert.Throws<InvalidDataException>(() => ZstdCodec.Instance.Decode(one.Concat(new byte[] { 0 }).ToArray(), data.Length));
        }

        [Fact]
        public void Wrong_declared_size_huge_window_and_dictionary_frames_are_refused()
        {
            var data = Encoding.ASCII.GetBytes(new string('c', 5000));
            var one = ZstdCodec.Instance.Encode(new ArraySegment<byte>(data));
            Assert.Throws<InvalidDataException>(() => ZstdCodec.Instance.Decode(one, data.Length + 1));

            // Hand-made header: no single-segment flag, window descriptor exponent 18 -> windowLog 28 (> 27).
            var big = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x84 /* FCS 4 bytes? no: 0x84 = fcs flag 2, checksum */, 18 << 3, 0, 0, 1, 0, 0x01, 0, 0, 0, 0, 0, 0 };
            var e = Assert.Throws<InvalidDataException>(() => ZstdFrame.Inspect(big));
            Assert.Contains("window", e.Message);

            var dict = (byte[])one.Clone();
            dict[4] |= 0x01;
            Assert.Throws<InvalidDataException>(() => ZstdFrame.Inspect(dict));
        }

        [Fact]
        public void Corrupted_payload_is_refused()
        {
            var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("corrupt me please ", 4000)));
            var z = ZstdCodec.Instance.Encode(new ArraySegment<byte>(text));
            z[z.Length / 2] ^= 0x55;
            Assert.ThrowsAny<Exception>(() =>
            {
                var d = ZstdCodec.Instance.Decode(z, text.Length);
                if (Sha256Util.Of(d) != Sha256Util.Of(text))
                {
                    throw new InvalidDataException("hash differs");
                }
            });
        }

        [Fact]
        public void Raw_size_mismatch_is_refused()
        {
            Assert.Throws<InvalidDataException>(() => RawCodec.Instance.Decode(new byte[3], 4));
            Assert.Null(TransportCodecs.Find("zpatch"));
        }
    }
}
