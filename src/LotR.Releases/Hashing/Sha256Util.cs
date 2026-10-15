using System;
using System.IO;
using System.Security.Cryptography;

namespace LotR.Releases.Hashing
{
    public static class Sha256Util
    {
        public const int BufferSize = 1 << 20;

        public static string Hex(byte[] bytes)
        {
            var c = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                c[i * 2] = HexDigit(b >> 4);
                c[i * 2 + 1] = HexDigit(b & 15);
            }

            return new string(c);
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0)
            {
                throw new FormatException("odd hex length");
            }

            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return b;
        }

        private static char HexDigit(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);

        public static string Of(byte[] data) => Of(data, 0, data.Length);

        public static string Of(byte[] data, int offset, int count)
        {
            using (var sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(data, offset, count));
            }
        }

        public static string Of(ArraySegment<byte> data) => Of(data.Array, data.Offset, data.Count);

        /// <summary>Streams a file through SHA-256 (1 MiB buffer, sequential scan hint).</summary>
        public static string OfFile(string path, Action<long> onBytes = null)
        {
            using (var fs = OpenSequential(path))
            {
                return OfStream(fs, onBytes);
            }
        }

        public static string OfStream(Stream s, Action<long> onBytes = null)
        {
            using (var sha = SHA256.Create())
            {
                var buf = new byte[BufferSize];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    onBytes?.Invoke(n);
                }

                sha.TransformFinalBlock(buf, 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static FileStream OpenSequential(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
    }
}
