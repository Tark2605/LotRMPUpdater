using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LotR.Releases.Json;
using LotR.Releases.Model;
using LotR.Releases.Signing;
using Xunit;

namespace LotR.Releases.Tests
{
    public class SigningTests
    {
        private static readonly byte[] Manifest = ManifestCodec.ToCanonicalBytes(TestData.SmallManifest());

        [Fact]
        public void Sign_and_verify_round_trip()
        {
            using (var signer = new ReleaseSigner(EcKeys.Generate()))
            {
                var sig = ManifestSignature.Create(Manifest, new[] { signer });
                var check = ManifestSignature.Verify(Manifest, sig, new[] { new TrustedKey(signer.Spki) });
                Assert.True(check.Valid, check.Message);
                Assert.Equal(signer.KeyId, check.KeyId);

                var parsed = JsonParser.Parse(sig).AsObject();
                Assert.Equal(1, parsed["schema"].AsLong());
                var entry = parsed["signatures"].AsArray()[0].AsObject();
                Assert.Equal("ES256", entry["alg"].AsString());
                Assert.Equal(64, Convert.FromBase64String(entry["sig"].AsString()).Length);
            }
        }

        [Fact]
        public void Any_changed_byte_is_detected()
        {
            using (var signer = new ReleaseSigner(EcKeys.Generate()))
            {
                var sig = ManifestSignature.Create(Manifest, new[] { signer });
                var trusted = new[] { new TrustedKey(signer.Spki) };
                foreach (int pos in new[] { 0, Manifest.Length / 3, Manifest.Length / 2, Manifest.Length - 1 })
                {
                    var tampered = (byte[])Manifest.Clone();
                    tampered[pos] ^= 0x01;
                    var check = ManifestSignature.Verify(tampered, sig, trusted);
                    Assert.False(check.Valid);
                    Assert.Contains("does NOT match", check.Message);
                }

                // Same JSON meaning, different bytes (pretty-printed): still rejected, the signature is over bytes.
                var pretty = JsonWriter.ToPrettyBytes(JsonParser.Parse(Manifest));
                Assert.False(ManifestSignature.Verify(pretty, sig, trusted).Valid);

                // Tampered signature value.
                var sigObj = JsonParser.Parse(sig).AsObject();
                var raw = Convert.FromBase64String(sigObj["signatures"].AsArray()[0].AsObject()["sig"].AsString());
                raw[10] ^= 0xFF;
                sigObj["signatures"].AsArray()[0].AsObject()["sig"] = Convert.ToBase64String(raw);
                Assert.False(ManifestSignature.Verify(Manifest, sigObj.ToCanonicalBytes(), trusted).Valid);
                Assert.False(ManifestSignature.Verify(Manifest, Encoding.ASCII.GetBytes("garbage"), trusted).Valid);
            }
        }

        [Fact]
        public void Unknown_key_is_refused_with_a_clear_message()
        {
            using (var a = new ReleaseSigner(EcKeys.Generate()))
            using (var b = new ReleaseSigner(EcKeys.Generate()))
            {
                var sig = ManifestSignature.Create(Manifest, new[] { a });
                var check = ManifestSignature.Verify(Manifest, sig, new[] { new TrustedKey(b.Spki) });
                Assert.False(check.Valid);
                Assert.Contains("unknown key", check.Message);
                Assert.False(ManifestSignature.Verify(Manifest, sig, new TrustedKey[0]).Valid);
            }
        }

        [Fact]
        public void Rotation_old_and_new_keys()
        {
            using (var oldKey = new ReleaseSigner(EcKeys.Generate()))
            using (var newKey = new ReleaseSigner(EcKeys.Generate()))
            {
                // Overlap: sign with both. Old updaters (trust old) and new ones (trust new) both accept.
                var both = ManifestSignature.Create(Manifest, new[] { oldKey, newKey });
                Assert.True(ManifestSignature.Verify(Manifest, both, new[] { new TrustedKey(oldKey.Spki) }).Valid);
                Assert.True(ManifestSignature.Verify(Manifest, both, new[] { new TrustedKey(newKey.Spki) }).Valid);

                // After the switch: new key only; an updater trusting both still accepts.
                var onlyNew = ManifestSignature.Create(Manifest, new[] { newKey });
                Assert.True(ManifestSignature.Verify(Manifest, onlyNew, new[] { new TrustedKey(oldKey.Spki), new TrustedKey(newKey.Spki) }).Valid);
                Assert.False(ManifestSignature.Verify(Manifest, onlyNew, new[] { new TrustedKey(oldKey.Spki) }).Valid);
            }
        }

        [Fact]
        public void Spki_pkcs8_pem_round_trip_and_fingerprint()
        {
            using (var key = EcKeys.Generate())
            {
                byte[] spki = EcKeys.ExportSpki(key);
                Assert.Equal(91, spki.Length);
                string pem = EcKeys.ToPem("PUBLIC KEY", spki);
                Assert.StartsWith("-----BEGIN PUBLIC KEY-----\n", pem);
                Assert.Equal(spki, EcKeys.SpkiFromPublicPem(pem));

                byte[] pkcs8 = EcKeys.ExportPkcs8(key);
                using (var back = EcKeys.ImportPkcs8(pkcs8))
                {
                    Assert.Equal(spki, EcKeys.ExportSpki(back));
                    var sig = back.SignData(Manifest, HashAlgorithmName.SHA256);
                    using (var pub = EcKeys.ImportSpki(spki))
                    {
                        Assert.True(pub.VerifyData(Manifest, sig, HashAlgorithmName.SHA256));
                    }
                }

                string fp = EcKeys.Fingerprint(spki);
                Assert.Equal(16, fp.Split(' ').Length);
                Assert.Equal(EcKeys.KeyId(spki), fp.Replace(" ", string.Empty));

#if NET8_0_OR_GREATER
                // Cross-check the hand-written DER against the platform encoders.
                using (var platform = ECDsa.Create())
                {
                    platform.ImportPkcs8PrivateKey(pkcs8, out int read);
                    Assert.Equal(pkcs8.Length, read);
                    Assert.Equal(spki, platform.ExportSubjectPublicKeyInfo());
                    using (var fromPlatform = EcKeys.ImportPkcs8(platform.ExportPkcs8PrivateKey()))
                    {
                        Assert.Equal(spki, EcKeys.ExportSpki(fromPlatform));
                    }
                }
#endif
            }
        }

        [Fact]
        public void Wrong_curve_or_garbage_keys_are_refused()
        {
            using (var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384))
            {
                var p = p384.ExportParameters(false);
                Assert.ThrowsAny<Exception>(() => EcKeys.EncodeSpki(p.Q.X, p.Q.Y));
            }

            Assert.ThrowsAny<CryptographicException>(() => EcKeys.ImportSpki(new byte[] { 0x30, 0x03, 0x02, 0x01, 0x00 }));
            Assert.ThrowsAny<CryptographicException>(() => EcKeys.ImportPkcs8(Encoding.ASCII.GetBytes("not der")));
        }

        [Theory]
        [InlineData(KeyProtection.DpapiMachine, false)]
        [InlineData(KeyProtection.Plain, false)]
        [InlineData(KeyProtection.DpapiMachine, true)]
        public void Key_store_save_and_load(KeyProtection protection, bool acl)
        {
            using (var t = new TempDir())
            using (var key = EcKeys.Generate())
            {
                string path = t.Sub("secrets", "release-signing.key");
                KeyStore.Save(path, key, protection, acl, acl ? new[] { "current" } : null);
                string text = File.ReadAllText(path);
                Assert.Contains(protection == KeyProtection.Plain ? "BEGIN PRIVATE KEY" : "BEGIN LOTR DPAPI PRIVATE KEY", text);
                using (var back = KeyStore.Load(path))
                {
                    Assert.Equal(EcKeys.ExportSpki(key), EcKeys.ExportSpki(back));
                }

                Assert.Throws<IOException>(() => KeyStore.Save(path, key, protection, false));
                if (acl)
                {
#if NET48
                    var rules = File.GetAccessControl(path).GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
#else
                    var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
#endif
                    var sids = rules.Cast<System.Security.AccessControl.FileSystemAccessRule>().Select(r => r.IdentityReference.Value).ToList();
                    Assert.Contains("S-1-5-18", sids); // SYSTEM
                    Assert.Contains("S-1-5-32-544", sids); // Administrators
                    Assert.DoesNotContain("S-1-5-32-545", sids); // Users
                    Assert.DoesNotContain("S-1-1-0", sids); // Everyone
                    Assert.All(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(), r => Assert.False(r.IsInherited));

                    // Cleanup: as owner we may rewrite the DACL, then the temp folder can be deleted.
                    var open = new System.Security.AccessControl.FileSecurity();
                    open.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
#if NET48
                    File.SetAccessControl(path, open);
#else
                    new FileInfo(path).SetAccessControl(open);
#endif
                }
            }
        }
    }
}
