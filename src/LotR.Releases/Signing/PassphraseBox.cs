using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LotR.Releases.Signing
{
    /// <summary>
    /// Passphrase encryption for the OFFLINE BACKUP of the release key (docs/TEAM-SIGNING.md): the backup file can sit on
    /// the PC and in a cloud drive, because without the passphrase it is useless.
    /// <para>Format (version 1), all inside the PEM block "LOTR PASSPHRASE PRIVATE KEY":
    /// "LPK1" | iterations (uint32 big-endian) | salt (16) | iv (16) | AES-256-CBC(PKCS#7) ciphertext | HMAC-SHA256 (32)
    /// over everything before it. Keys: PBKDF2-HMAC-SHA256(UTF-8 passphrase, salt, iterations) -> 64 bytes, the first 32
    /// for AES, the last 32 for the HMAC (encrypt-then-MAC). The MAC is checked in constant time before decrypting.</para>
    /// <para>PBKDF2 is written here (RFC 8018 §5.2) because netstandard2.0 has no SHA-256 Rfc2898DeriveBytes.</para>
    /// </summary>
    public static class PassphraseBox
    {
        public const int DefaultIterations = 600000; // OWASP 2023 figure for PBKDF2-HMAC-SHA256
        public const int MinPassphraseLength = 20;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LPK1");

        /// <summary>Returns null when the passphrase is acceptable, else the reason (shown to the user).</summary>
        public static string CheckPassphrase(string passphrase)
        {
            if (passphrase == null || passphrase.Trim().Length < MinPassphraseLength)
            {
                return "the passphrase must have at least " + MinPassphraseLength + " characters (5 or 6 words you can remember, e.g. a short sentence)";
            }

            return null;
        }

        public static byte[] Seal(byte[] secret, string passphrase, int iterations = DefaultIterations)
        {
            string why = CheckPassphrase(passphrase);
            if (why != null)
            {
                throw new ArgumentException(why);
            }

            byte[] salt = Random(16);
            byte[] iv = Random(16);
            byte[] keys = Pbkdf2Sha256(Encoding.UTF8.GetBytes(passphrase), salt, iterations, 64);
            try
            {
                byte[] cipher;
                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Slice(keys, 0, 32);
                    aes.IV = iv;
                    using (var enc = aes.CreateEncryptor())
                    {
                        cipher = enc.TransformFinalBlock(secret, 0, secret.Length);
                    }
                }

                var ms = new MemoryStream();
                ms.Write(Magic, 0, 4);
                ms.Write(new[] { (byte)(iterations >> 24), (byte)(iterations >> 16), (byte)(iterations >> 8), (byte)iterations }, 0, 4);
                ms.Write(salt, 0, 16);
                ms.Write(iv, 0, 16);
                ms.Write(cipher, 0, cipher.Length);
                byte[] body = ms.ToArray();
                byte[] mac;
                using (var h = new HMACSHA256(Slice(keys, 32, 32)))
                {
                    mac = h.ComputeHash(body);
                }

                ms.Write(mac, 0, mac.Length);
                return ms.ToArray();
            }
            finally
            {
                Array.Clear(keys, 0, keys.Length);
            }
        }

        public static byte[] Open(byte[] box, string passphrase)
        {
            const int header = 4 + 4 + 16 + 16;
            if (box == null || box.Length < header + 16 + 32 || box[0] != Magic[0] || box[1] != Magic[1] || box[2] != Magic[2] || box[3] != Magic[3])
            {
                throw new CryptographicException("not a LotR passphrase-protected key (or a newer format)");
            }

            int iterations = (box[4] << 24) | (box[5] << 16) | (box[6] << 8) | box[7];
            if (iterations < 1000 || iterations > 50000000)
            {
                throw new CryptographicException("damaged passphrase-protected key (iteration count " + iterations + ")");
            }

            byte[] salt = Slice(box, 8, 16);
            byte[] iv = Slice(box, 24, 16);
            int bodyLen = box.Length - 32;
            byte[] keys = Pbkdf2Sha256(Encoding.UTF8.GetBytes(passphrase ?? string.Empty), salt, iterations, 64);
            try
            {
                byte[] mac;
                using (var h = new HMACSHA256(Slice(keys, 32, 32)))
                {
                    mac = h.ComputeHash(box, 0, bodyLen);
                }

                int diff = 0;
                for (int i = 0; i < 32; i++)
                {
                    diff |= mac[i] ^ box[bodyLen + i];
                }

                if (diff != 0)
                {
                    throw new CryptographicException("wrong passphrase (or the backup file was altered)");
                }

                using (var aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = Slice(keys, 0, 32);
                    aes.IV = iv;
                    using (var dec = aes.CreateDecryptor())
                    {
                        return dec.TransformFinalBlock(box, header, bodyLen - header);
                    }
                }
            }
            finally
            {
                Array.Clear(keys, 0, keys.Length);
            }
        }

        /// <summary>PBKDF2 with HMAC-SHA256 (RFC 8018 §5.2).</summary>
        public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            var output = new byte[length];
            using (var hmac = new HMACSHA256(password))
            {
                var block = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, block, 0, salt.Length);
                int done = 0;
                for (uint index = 1; done < length; index++)
                {
                    block[salt.Length] = (byte)(index >> 24);
                    block[salt.Length + 1] = (byte)(index >> 16);
                    block[salt.Length + 2] = (byte)(index >> 8);
                    block[salt.Length + 3] = (byte)index;
                    byte[] u = hmac.ComputeHash(block);
                    byte[] t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++)
                        {
                            t[j] ^= u[j];
                        }
                    }

                    int take = Math.Min(t.Length, length - done);
                    Buffer.BlockCopy(t, 0, output, done, take);
                    done += take;
                }
            }

            return output;
        }

        private static byte[] Random(int n)
        {
            var b = new byte[n];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(b);
            }

            return b;
        }

        private static byte[] Slice(byte[] a, int offset, int count)
        {
            var r = new byte[count];
            Buffer.BlockCopy(a, offset, r, 0, count);
            return r;
        }
    }
}
