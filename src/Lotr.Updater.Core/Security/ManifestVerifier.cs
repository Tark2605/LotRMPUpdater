using System;
using System.Collections.Generic;
using System.Linq;
using LotR.Releases.Signing;

namespace Lotr.Updater.Core.Security
{
    public sealed class VerificationResult
    {
        public VerificationResult(bool trusted, string message, string keyFingerprint)
        {
            Trusted = trusted;
            Message = message;
            KeyFingerprint = keyFingerprint;
        }

        public bool Trusted { get; }
        public string Message { get; }
        public string KeyFingerprint { get; }
    }

    /// <summary>Checks manifest.json.sig against the keys built into the updater (CONTRACTS §3, §8).</summary>
    public interface IManifestVerifier
    {
        VerificationResult Verify(byte[] manifestBytes, byte[] signatureFile);
        IReadOnlyList<string> TrustedFingerprints { get; }
    }

    /// <summary>ECDSA P-256 via LotR.Releases <see cref="ManifestSignature"/>: accepted when ANY signature verifies with a trusted key.</summary>
    public sealed class ReleaseSignatureVerifier : IManifestVerifier
    {
        private readonly List<TrustedKey> _keys;

        public ReleaseSignatureVerifier(IEnumerable<string> publicKeysPem)
        {
            _keys = (publicKeysPem ?? Enumerable.Empty<string>()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => TrustedKey.FromPem(p)).ToList();
        }

        public IReadOnlyList<string> TrustedFingerprints => _keys.Select(k => k.Fingerprint).ToList();

        public VerificationResult Verify(byte[] manifestBytes, byte[] signatureFile)
        {
            if (signatureFile == null || signatureFile.Length == 0)
                return new VerificationResult(false, "The release has no signature file (manifest.json.sig). It was refused: nothing was installed.", null);
            if (_keys.Count == 0)
                return new VerificationResult(false, "This updater has no trusted release key yet, so it refuses every release.", null);
            SignatureCheck c;
            try { c = ManifestSignature.Verify(manifestBytes, signatureFile, _keys); }
            catch (Exception ex) when (ex is FormatException || ex is System.Security.Cryptography.CryptographicException || ex is System.IO.InvalidDataException)
            {
                return new VerificationResult(false, "The release signature is damaged (" + ex.Message + "). It was refused: nothing was installed.", null);
            }
            if (!c.Valid)
                return new VerificationResult(false, "The release signature does not match the LotR team key (" + c.Message + "). The download may have been altered: it was refused and nothing was installed.", null);
            var fp = EcKeys.FormatFingerprint(c.KeyId);
            return new VerificationResult(true, "Signature valid (key " + Short(fp) + ").", fp);
        }

        private static string Short(string fp) => fp == null ? "" : fp.Substring(0, Math.Min(19, fp.Length));
    }

    /// <summary>Accepts unsigned manifests. Never used by the app (tests only).</summary>
    public sealed class DevAcceptAllVerifier : IManifestVerifier
    {
        public IReadOnlyList<string> TrustedFingerprints => new List<string>();
        public VerificationResult Verify(byte[] manifestBytes, byte[] signature) => new VerificationResult(true, "TEST: signature not checked.", null);
    }

    /// <summary>Public keys compiled into the updater (CONTRACTS §3: a list, for key rotation).</summary>
    public static class TrustedKeys
    {
        // REVIEW-AFTER(release-key): paste the server's release-signing.pub.pem here once `lotr-publish init-key` ran on the
        // production server. Empty until then: a production build refuses every release, by design. Test keys are only ever
        // passed on the command line of a --dev run (never compiled in).
        public static readonly string[] Production = new string[0];
    }
}
