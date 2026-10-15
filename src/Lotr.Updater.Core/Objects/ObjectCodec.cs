using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LotR.Releases.Model;
using LotR.Releases.Transport;

namespace Lotr.Updater.Core.Objects
{
    /// <summary>
    /// Object decoding for the updater, on top of LotR.Releases transports (CONTRACTS §1: ".zst" with the strict frame checks
    /// and windowLog &lt;= 27, ".raw"; ".zpatch" reserved and refused). Object names are "&lt;sha256 of the DECODED bytes&gt;.&lt;ext&gt;".
    /// </summary>
    public static class ObjectCodec
    {
        /// <summary>Objects up to this size are decoded in memory with the full structural check (whole files are &lt;= 16 MiB, chunks &lt;= 1 MiB).</summary>
        public const long InMemoryLimit = 32L << 20;

        public static bool IsSupported(string ext) => TransportCodecs.Find(ext) != null;

        public static string ExtensionOf(string objectName) => (ObjectNames.Extension(objectName) ?? "").ToLowerInvariant();

        public static string ShaOf(string objectName) => ObjectNames.Sha(objectName).ToLowerInvariant();

        public static ITransportCodec CodecFor(string ext)
        {
            var c = TransportCodecs.Find(ext);
            if (c == null)
                throw new NotSupportedException("This release uses a file format (." + ext + ") that this version of the updater does not know. Please update the updater from the website.");
            return c;
        }

        /// <summary>Wraps an encoded stream into a stream of decoded bytes. Disposes <paramref name="encoded"/> with it.</summary>
        public static Stream OpenDecoded(Stream encoded, string ext) => CodecFor(ext).OpenDecoded(encoded);

        /// <summary>
        /// Decodes an object to <paramref name="destination"/> (null = verify only) and returns its decoded length and SHA-256.
        /// With a known <paramref name="expectedSize"/> and a small object, the strict in-memory decoder of LotR.Releases is used
        /// (frame structure, content size, checksum, window); otherwise the streaming decoder.
        /// </summary>
        public static DecodeResult DecodeTo(Stream encoded, string ext, Stream destination, Action<long> decodedProgress = null, CancellationToken ct = default, long expectedSize = -1)
        {
            var codec = CodecFor(ext);
            if (expectedSize >= 0 && encoded.CanSeek && encoded.Length <= InMemoryLimit)
            {
                byte[] raw;
                using (encoded)
                {
                    raw = new byte[encoded.Length];
                    int read = 0;
                    while (read < raw.Length) { int n = encoded.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
                }
                byte[] dec;
                try { dec = codec.Decode(raw, expectedSize); }
                catch (InvalidDataException ex) { return new DecodeResult(-1, "invalid object: " + ex.Message); }
                ct.ThrowIfCancellationRequested();
                destination?.Write(dec, 0, dec.Length);
                decodedProgress?.Invoke(dec.Length);
                return new DecodeResult(dec.Length, Sha256Hex(dec, 0, dec.Length));
            }
            using (var sha = SHA256.Create())
            using (var dec = codec.OpenDecoded(encoded))
            {
                var buf = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = dec.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    destination?.Write(buf, 0, n);
                    total += n;
                    decodedProgress?.Invoke(n);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                if (expectedSize >= 0 && total != expectedSize) return new DecodeResult(total, "wrong size " + total);
                return new DecodeResult(total, Hex(sha.Hash));
            }
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static string Sha256Hex(Stream s, CancellationToken ct = default, Action<long> progress = null)
        {
            using (var sha = SHA256.Create())
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    progress?.Invoke(n);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Sha256Hex(byte[] data, int offset, int count)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data, offset, count));
        }
    }

    public struct DecodeResult
    {
        public DecodeResult(long length, string sha256) { Length = length; Sha256 = sha256; }
        public long Length { get; }
        public string Sha256 { get; }
    }
}
