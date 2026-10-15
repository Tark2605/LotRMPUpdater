using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace LotR.Releases.Signing
{
    public enum KeyProtection
    {
        /// <summary>PKCS#8 encrypted with DPAPI, machine scope (default on the server). Only usable on that machine.</summary>
        DpapiMachine,

        /// <summary>Plain PKCS#8 PEM; protection comes from the file ACL only. Portable (backup, other machine).</summary>
        Plain,
    }

    /// <summary>
    /// The release signing key on disk (CONTRACTS §3: &lt;root&gt;\data\secrets\release-signing.key).
    /// <para>DpapiMachine: "-----BEGIN LOTR DPAPI PRIVATE KEY-----" holding ProtectedData.Protect(pkcs8, entropy,
    /// LocalMachine). Machine scope means any process on THIS machine may decrypt, so the ACL still matters; the gain
    /// is that a copied file (backup, leaked disk image without the machine's DPAPI master key) is useless.</para>
    /// <para>Plain: standard "-----BEGIN PRIVATE KEY-----" PKCS#8 (OpenSSL-compatible) for offline backup.</para>
    /// Both are written with an explicit ACL: inheritance removed, full control for BUILTIN\Administrators and
    /// SYSTEM, plus the accounts given (e.g. the service account that runs the admin panel's publish step).
    /// </summary>
    public static class KeyStore
    {
        public const string DpapiLabel = "LOTR DPAPI PRIVATE KEY";
        public const string PlainLabel = "PRIVATE KEY";
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("LotRMPEvent release signing key v1");

        public static void Save(string path, ECDsa key, KeyProtection protection, bool applyAcl, IEnumerable<string> extraAccounts = null, bool overwrite = false)
        {
            if (File.Exists(path) && !overwrite)
            {
                throw new IOException("Refusing to overwrite the existing key " + path + " (use --force, after a backup: every published release is signed with it)");
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(dir);
            byte[] pkcs8 = EcKeys.ExportPkcs8(key);
            string pem;
            try
            {
                pem = protection == KeyProtection.DpapiMachine
                    ? EcKeys.ToPem(DpapiLabel, ProtectedData.Protect(pkcs8, Entropy, DataProtectionScope.LocalMachine))
                    : EcKeys.ToPem(PlainLabel, pkcs8);
            }
            finally
            {
                Array.Clear(pkcs8, 0, pkcs8.Length);
            }

            // Create the file empty, lock it down, then write: the secret never sits under an inherited ACL.
            string tmp = path + ".new";
            File.WriteAllText(tmp, string.Empty);
            if (applyAcl)
            {
                // Phase 1: protected ACL that still lets this process write; phase 2: the final ACL.
                RestrictAcl(tmp, extraAccounts, writerIsCurrentUser: true);
            }

            File.WriteAllText(tmp, pem, new UTF8Encoding(false));
            if (applyAcl)
            {
                RestrictAcl(tmp, extraAccounts, writerIsCurrentUser: false);
            }
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }

        public static ECDsa Load(string path)
        {
            string pem = File.ReadAllText(path);
            if (EcKeys.HasPem(pem, DpapiLabel))
            {
                byte[] pkcs8;
                try
                {
                    pkcs8 = ProtectedData.Unprotect(EcKeys.FromPem(pem, DpapiLabel), Entropy, DataProtectionScope.LocalMachine);
                }
                catch (CryptographicException e)
                {
                    throw new CryptographicException("Cannot decrypt " + path + " with this machine's DPAPI key (DPAPI keys only work on the machine that created them): " + e.Message, e);
                }

                try
                {
                    return EcKeys.ImportPkcs8(pkcs8);
                }
                finally
                {
                    Array.Clear(pkcs8, 0, pkcs8.Length);
                }
            }

            if (EcKeys.HasPem(pem, PlainLabel))
            {
                return EcKeys.ImportPkcs8(EcKeys.FromPem(pem, PlainLabel));
            }

            if (EcKeys.HasPem(pem, "ENCRYPTED PRIVATE KEY") || EcKeys.HasPem(pem, "EC PRIVATE KEY"))
            {
                throw new CryptographicException(path + ": password-encrypted PKCS#8 and SEC1 'EC PRIVATE KEY' are not supported; convert with: openssl pkcs8 -topk8 -nocrypt");
            }

            throw new CryptographicException(path + ": no private key PEM block found");
        }

        public static void RestrictAcl(string path, IEnumerable<string> extraAccounts, bool writerIsCurrentUser = false)
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            if (writerIsCurrentUser)
            {
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            foreach (var account in extraAccounts ?? new string[0])
            {
                IdentityReference id = account.Equals("current", StringComparison.OrdinalIgnoreCase)
                    ? (IdentityReference)WindowsIdentity.GetCurrent().User
                    : new NTAccount(account);
                security.AddAccessRule(new FileSystemAccessRule(id, FileSystemRights.Read | FileSystemRights.Synchronize, AccessControlType.Allow));
            }

#if NET48
            File.SetAccessControl(path, security);
#else
            new FileInfo(path).SetAccessControl(security);
#endif
        }

        public static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
    }
}
