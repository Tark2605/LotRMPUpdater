using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LotR.Releases.Hashing;

namespace LotR.Releases.Signing
{
    /// <summary>
    /// P-256 key encodings without third-party code and without the .NET Core 3+ import/export APIs (absent on net48):
    /// SubjectPublicKeyInfo (RFC 5480) and PKCS#8 PrivateKeyInfo wrapping an RFC 5915 ECPrivateKey, both DER, plus PEM.
    /// </summary>
    public static class EcKeys
    {
        // SEQUENCE { SEQUENCE { OID id-ecPublicKey, OID prime256v1 }, BIT STRING (0x00, 0x04 || X || Y) }
        private static readonly byte[] SpkiPrefix = Sha256Util.FromHex("3059301306072a8648ce3d020106082a8648ce3d030107034200");
        private static readonly byte[] OidEcPublicKey = Sha256Util.FromHex("2a8648ce3d0201");
        private static readonly byte[] OidP256 = Sha256Util.FromHex("2a8648ce3d030107");

        public static ECDsa Generate() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public static byte[] ExportSpki(ECDsa key)
        {
            var p = key.ExportParameters(false);
            return EncodeSpki(p.Q.X, p.Q.Y);
        }

        public static byte[] EncodeSpki(byte[] x, byte[] y)
        {
            var spki = new byte[SpkiPrefix.Length + 65];
            Buffer.BlockCopy(SpkiPrefix, 0, spki, 0, SpkiPrefix.Length);
            spki[SpkiPrefix.Length] = 0x04;
            Buffer.BlockCopy(Pad32(x), 0, spki, SpkiPrefix.Length + 1, 32);
            Buffer.BlockCopy(Pad32(y), 0, spki, SpkiPrefix.Length + 33, 32);
            return spki;
        }

        public static ECDsa ImportSpki(byte[] spki)
        {
            var outer = Der.Read(spki, 0, out int end);
            if (outer.Tag != 0x30 || end != spki.Length)
            {
                throw new CryptographicException("public key: not a DER SubjectPublicKeyInfo");
            }

            var children = Der.Children(spki, outer);
            if (children.Count != 2 || children[0].Tag != 0x30 || children[1].Tag != 0x03)
            {
                throw new CryptographicException("public key: unexpected structure");
            }

            var alg = Der.Children(spki, children[0]);
            if (alg.Count != 2 || !Der.ValueEquals(spki, alg[0], OidEcPublicKey) || !Der.ValueEquals(spki, alg[1], OidP256))
            {
                throw new CryptographicException("public key: not an EC P-256 key");
            }

            var bits = children[1];
            if (bits.Length != 66 || spki[bits.Offset] != 0 || spki[bits.Offset + 1] != 0x04)
            {
                throw new CryptographicException("public key: expected an uncompressed P-256 point");
            }

            var x = new byte[32];
            var y = new byte[32];
            Buffer.BlockCopy(spki, bits.Offset + 2, x, 0, 32);
            Buffer.BlockCopy(spki, bits.Offset + 34, y, 0, 32);
            var key = ECDsa.Create();
            key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
            return key;
        }

        /// <summary>PKCS#8 PrivateKeyInfo { 0, {ecPublicKey, prime256v1}, OCTET STRING ECPrivateKey { 1, d, [1] pub } }.</summary>
        public static byte[] ExportPkcs8(ECDsa key)
        {
            var p = key.ExportParameters(true);
            byte[] pub = new byte[66];
            pub[0] = 0x00;
            pub[1] = 0x04;
            Buffer.BlockCopy(Pad32(p.Q.X), 0, pub, 2, 32);
            Buffer.BlockCopy(Pad32(p.Q.Y), 0, pub, 34, 32);
            byte[] ecPrivateKey = Der.Seq(
                Der.Tlv(0x02, new byte[] { 1 }),
                Der.Tlv(0x04, Pad32(p.D)),
                Der.Tlv(0xA1, Der.Tlv(0x03, pub)));
            return Der.Seq(
                Der.Tlv(0x02, new byte[] { 0 }),
                Der.Seq(Der.Tlv(0x06, OidEcPublicKey), Der.Tlv(0x06, OidP256)),
                Der.Tlv(0x04, ecPrivateKey));
        }

        public static ECDsa ImportPkcs8(byte[] pkcs8)
        {
            var outer = Der.Read(pkcs8, 0, out _);
            var top = Der.Children(pkcs8, outer);
            if (outer.Tag != 0x30 || top.Count < 3 || top[1].Tag != 0x30 || top[2].Tag != 0x04)
            {
                throw new CryptographicException("private key: not a PKCS#8 PrivateKeyInfo");
            }

            var alg = Der.Children(pkcs8, top[1]);
            if (alg.Count < 2 || !Der.ValueEquals(pkcs8, alg[0], OidEcPublicKey) || !Der.ValueEquals(pkcs8, alg[1], OidP256))
            {
                throw new CryptographicException("private key: not an EC P-256 key");
            }

            var inner = Der.Read(pkcs8, top[2].Offset, out _);
            var fields = Der.Children(pkcs8, inner);
            if (fields.Count < 2 || fields[1].Tag != 0x04)
            {
                throw new CryptographicException("private key: bad ECPrivateKey");
            }

            byte[] d = new byte[fields[1].Length];
            Buffer.BlockCopy(pkcs8, fields[1].Offset, d, 0, d.Length);
            d = Pad32(d);
            byte[] x = null, y = null;
            foreach (var f in fields.Skip(2))
            {
                if (f.Tag == 0xA1)
                {
                    var bits = Der.Read(pkcs8, f.Offset, out _);
                    if (bits.Length == 66 && pkcs8[bits.Offset + 1] == 0x04)
                    {
                        x = new byte[32];
                        y = new byte[32];
                        Buffer.BlockCopy(pkcs8, bits.Offset + 2, x, 0, 32);
                        Buffer.BlockCopy(pkcs8, bits.Offset + 34, y, 0, 32);
                    }
                }
            }

            if (x == null)
            {
                throw new CryptographicException("private key: the public point is missing (re-export with the public key)");
            }

            var key = ECDsa.Create();
            key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = new ECPoint { X = x, Y = y } });
            return key;
        }

        /// <summary>SHA-256 of the SPKI DER, lowercase hex (64 chars). Used as keyId in signature files.</summary>
        public static string KeyId(byte[] spki) => Sha256Util.Of(spki);

        /// <summary>Human form of <see cref="KeyId"/>: 16 groups of 4 hex characters (what the Security page shows).</summary>
        public static string Fingerprint(byte[] spki) => FormatFingerprint(KeyId(spki));

        public static string FormatFingerprint(string keyId)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < keyId.Length; i += 4)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(keyId, i, Math.Min(4, keyId.Length - i));
            }

            return sb.ToString();
        }

        public static string ToPem(string label, byte[] der)
        {
            string b64 = Convert.ToBase64String(der);
            var sb = new StringBuilder();
            sb.Append("-----BEGIN ").Append(label).Append("-----\n");
            for (int i = 0; i < b64.Length; i += 64)
            {
                sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
            }

            sb.Append("-----END ").Append(label).Append("-----\n");
            return sb.ToString();
        }

        public static byte[] FromPem(string pem, string label)
        {
            string begin = "-----BEGIN " + label + "-----";
            string end = "-----END " + label + "-----";
            int a = pem.IndexOf(begin, StringComparison.Ordinal);
            int b = pem.IndexOf(end, StringComparison.Ordinal);
            if (a < 0 || b < a)
            {
                throw new CryptographicException("PEM block '" + label + "' not found");
            }

            string body = pem.Substring(a + begin.Length, b - a - begin.Length);
            return Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray()));
        }

        public static bool HasPem(string pem, string label) => pem.IndexOf("-----BEGIN " + label + "-----", StringComparison.Ordinal) >= 0;

        public static ECDsa ImportPublicPem(string pem) => ImportSpki(FromPem(pem, "PUBLIC KEY"));

        public static byte[] SpkiFromPublicPem(string pem) => FromPem(pem, "PUBLIC KEY");

        private static byte[] Pad32(byte[] v)
        {
            if (v.Length == 32)
            {
                return v;
            }

            if (v.Length > 32)
            {
                int skip = v.Length - 32;
                if (v.Take(skip).Any(x => x != 0))
                {
                    throw new CryptographicException("value longer than 32 bytes");
                }

                return v.Skip(skip).ToArray();
            }

            var r = new byte[32];
            Buffer.BlockCopy(v, 0, r, 32 - v.Length, v.Length);
            return r;
        }
    }

    /// <summary>Just enough DER for the fixed P-256 structures above.</summary>
    internal static class Der
    {
        internal struct Node
        {
            public byte Tag;
            public int Offset; // start of value
            public int Length;
        }

        internal static Node Read(byte[] b, int pos, out int end)
        {
            if (pos + 2 > b.Length)
            {
                throw new CryptographicException("DER: truncated");
            }

            byte tag = b[pos++];
            int len = b[pos++];
            if (len >= 0x80)
            {
                int n = len & 0x7F;
                if (n == 0 || n > 3 || pos + n > b.Length)
                {
                    throw new CryptographicException("DER: bad length");
                }

                len = 0;
                for (int i = 0; i < n; i++)
                {
                    len = (len << 8) | b[pos++];
                }
            }

            if (pos + len > b.Length)
            {
                throw new CryptographicException("DER: truncated value");
            }

            end = pos + len;
            return new Node { Tag = tag, Offset = pos, Length = len };
        }

        internal static System.Collections.Generic.List<Node> Children(byte[] b, Node parent)
        {
            var list = new System.Collections.Generic.List<Node>();
            int pos = parent.Offset;
            int stop = parent.Offset + parent.Length;
            while (pos < stop)
            {
                list.Add(Read(b, pos, out pos));
            }

            if (pos != stop)
            {
                throw new CryptographicException("DER: child overruns parent");
            }

            return list;
        }

        internal static bool ValueEquals(byte[] b, Node n, byte[] expected)
        {
            if (n.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < expected.Length; i++)
            {
                if (b[n.Offset + i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        internal static byte[] Tlv(byte tag, byte[] value)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(tag);
                int len = value.Length;
                if (len < 0x80)
                {
                    ms.WriteByte((byte)len);
                }
                else if (len < 0x100)
                {
                    ms.WriteByte(0x81);
                    ms.WriteByte((byte)len);
                }
                else
                {
                    ms.WriteByte(0x82);
                    ms.WriteByte((byte)(len >> 8));
                    ms.WriteByte((byte)len);
                }

                ms.Write(value, 0, value.Length);
                return ms.ToArray();
            }
        }

        internal static byte[] Seq(params byte[][] items) => Tlv(0x30, items.SelectMany(x => x).ToArray());
    }
}
