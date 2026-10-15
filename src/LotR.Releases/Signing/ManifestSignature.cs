using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LotR.Releases.Json;

namespace LotR.Releases.Signing
{
    /// <summary>A public key the updater (or the verify command) trusts.</summary>
    public sealed class TrustedKey
    {
        public TrustedKey(byte[] spki, string label = null)
        {
            Spki = spki;
            KeyId = EcKeys.KeyId(spki);
            Label = label ?? KeyId.Substring(0, 16);
            using (EcKeys.ImportSpki(spki))
            {
                // validates the encoding once
            }
        }

        public byte[] Spki { get; }

        public string KeyId { get; }

        public string Label { get; }

        public string Fingerprint => EcKeys.FormatFingerprint(KeyId);

        public static TrustedKey FromPem(string pem, string label = null) => new TrustedKey(EcKeys.SpkiFromPublicPem(pem), label);

        public static TrustedKey FromPemFile(string path) => FromPem(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>A private key loaded for signing.</summary>
    public sealed class ReleaseSigner : IDisposable
    {
        private readonly ECDsa key;

        public ReleaseSigner(ECDsa key)
        {
            this.key = key ?? throw new ArgumentNullException(nameof(key));
            Spki = EcKeys.ExportSpki(key);
            KeyId = EcKeys.KeyId(Spki);
        }

        public byte[] Spki { get; }

        public string KeyId { get; }

        public byte[] SignData(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);

        public void Dispose() => key.Dispose();
    }

    public sealed class SignatureCheck
    {
        public bool Valid { get; set; }

        public string KeyId { get; set; }

        public string Message { get; set; }

        public override string ToString() => Message;
    }

    /// <summary>
    /// manifest.json.sig: detached ECDSA P-256 / SHA-256 signatures over the EXACT bytes of manifest.json.
    /// File format (canonical JSON, UTF-8):
    /// <code>{"schema":1,"signatures":[{"alg":"ES256","keyId":"&lt;sha256 of SPKI, hex&gt;","sig":"&lt;base64 r||s, 64 bytes&gt;"}]}</code>
    /// Several signatures are allowed (key rotation: sign with old and new key during the overlap). A manifest is
    /// accepted when at least one signature verifies with a trusted key.
    /// REVIEW-AFTER(sig-format): this .sig JSON format is defined here; fold it into CONTRACTS §3 so the updater matches.
    /// </summary>
    public static class ManifestSignature
    {
        public const string Algorithm = "ES256";
        public const int Schema = 1;

        public static byte[] Create(byte[] manifestBytes, IEnumerable<ReleaseSigner> signers)
        {
            var list = new JsonArray();
            foreach (var s in signers)
            {
                byte[] sig = s.SignData(manifestBytes);
                list.Add(new JsonObject { ["alg"] = Algorithm, ["keyId"] = s.KeyId, ["sig"] = Convert.ToBase64String(sig) });
            }

            if (list.Count == 0)
            {
                throw new InvalidOperationException("No signing key given");
            }

            return new JsonObject { ["schema"] = Schema, ["signatures"] = list }.ToCanonicalBytes();
        }

        public static IReadOnlyList<(string Alg, string KeyId, byte[] Sig)> Parse(byte[] sigFile)
        {
            var o = JsonParser.Parse(sigFile) as JsonObject ?? throw new FormatException("signature file is not a JSON object");
            if (o["schema"]?.AsLong() != Schema)
            {
                throw new FormatException("signature file: unsupported schema");
            }

            var arr = o["signatures"] as JsonArray ?? throw new FormatException("signature file: no signatures array");
            var result = new List<(string, string, byte[])>();
            foreach (var n in arr)
            {
                var s = n as JsonObject ?? throw new FormatException("signature file: entry is not an object");
                string alg = s["alg"]?.AsString();
                string keyId = s["keyId"]?.AsString();
                string b64 = s["sig"]?.AsString();
                if (alg == null || keyId == null || b64 == null)
                {
                    throw new FormatException("signature file: entry lacks alg, keyId or sig");
                }

                result.Add((alg, keyId, Convert.FromBase64String(b64)));
            }

            return result;
        }

        /// <summary>Never throws: any defect is a failed check with a message fit for a player-facing log.</summary>
        public static SignatureCheck Verify(byte[] manifestBytes, byte[] sigFile, IEnumerable<TrustedKey> trusted)
        {
            var keys = trusted?.ToList() ?? new List<TrustedKey>();
            if (keys.Count == 0)
            {
                return new SignatureCheck { Valid = false, Message = "no trusted public key configured" };
            }

            IReadOnlyList<(string Alg, string KeyId, byte[] Sig)> sigs;
            try
            {
                sigs = Parse(sigFile);
            }
            catch (Exception e) when (e is FormatException || e is JsonParseException)
            {
                return new SignatureCheck { Valid = false, Message = "signature file unreadable: " + e.Message };
            }

            var unknown = new List<string>();
            foreach (var s in sigs)
            {
                if (s.Alg != Algorithm)
                {
                    continue;
                }

                var key = keys.FirstOrDefault(k => string.Equals(k.KeyId, s.KeyId, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                {
                    unknown.Add(s.KeyId);
                    continue;
                }

                using (var ec = EcKeys.ImportSpki(key.Spki))
                {
                    if (s.Sig.Length == 64 && ec.VerifyData(manifestBytes, s.Sig, HashAlgorithmName.SHA256))
                    {
                        return new SignatureCheck { Valid = true, KeyId = key.KeyId, Message = "signed by trusted key " + key.Label + " (" + key.Fingerprint + ")" };
                    }

                    return new SignatureCheck { Valid = false, KeyId = key.KeyId, Message = "signature by key " + key.Label + " does NOT match the manifest bytes (manifest altered or corrupted)" };
                }
            }

            return new SignatureCheck
            {
                Valid = false,
                Message = unknown.Count > 0
                    ? "manifest signed only by unknown key(s) " + string.Join(", ", unknown.Select(k => k.Length > 16 ? k.Substring(0, 16) : k)) + "; update the utility if the team rotated its key"
                    : "no ES256 signature found",
            };
        }
    }
}
