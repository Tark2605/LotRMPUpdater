using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LotR.Releases.Signing
{
    /// <summary>Authenticode state of a PE file (the updater exe), as Windows sees it.</summary>
    public sealed class AuthenticodeInfo
    {
        /// <summary>The file carries an Authenticode signature (valid or not).</summary>
        public bool Signed { get; set; }

        /// <summary>WinVerifyTrust accepted the signature (chain to a trusted root, file not modified).</summary>
        public bool Valid { get; set; }

        /// <summary>Signer certificate subject (e.g. "CN=LotR MP Event team, ..."), null when unsigned.</summary>
        public string Subject { get; set; }

        public string Thumbprint { get; set; }

        /// <summary>Plain-language status for logs.</summary>
        public string Status { get; set; }

        public static AuthenticodeInfo Unsigned(string status = "not signed") => new AuthenticodeInfo { Status = status };

        /// <summary>
        /// Self-update rule: when the running exe is validly signed, the new one must be validly signed by the SAME
        /// subject (certificates are renewed, so the thumbprint may change; the subject may not). An unsigned running exe
        /// accepts anything here (the release-key signature of latest.json and the SHA-256 do the work).
        /// Returns null when acceptable, else the reason.
        /// </summary>
        public static string CheckSameSigner(AuthenticodeInfo current, AuthenticodeInfo candidate)
        {
            if (current == null || !current.Valid)
            {
                return candidate != null && candidate.Signed && !candidate.Valid ? "the new updater carries an invalid signature (" + candidate.Status + ")" : null;
            }

            if (candidate == null || !candidate.Signed)
            {
                return "the new updater is not signed, but this one is signed by " + current.Subject;
            }

            if (!candidate.Valid)
            {
                return "the new updater's signature is not valid (" + candidate.Status + ")";
            }

            if (!string.Equals(current.Subject, candidate.Subject, StringComparison.Ordinal))
            {
                return "the new updater is signed by '" + candidate.Subject + "', not by '" + current.Subject + "'";
            }

            return null;
        }
    }

    /// <summary>WinVerifyTrust (no network: revocation is not checked) + the signer certificate. Windows only.</summary>
    public static class Authenticode
    {
        private const uint TrustENoSignature = 0x800B0100;
        private const uint TrustESubjectFormUnknown = 0x800B0003;
        private const uint TrustEProviderUnknown = 0x800B0001;
        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static AuthenticodeInfo Inspect(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("file not found", path);
            }

            uint rc;
            try
            {
                rc = Verify(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return AuthenticodeInfo.Unsigned("Authenticode cannot be checked on this system");
            }

            if (rc == TrustENoSignature || rc == TrustESubjectFormUnknown || rc == TrustEProviderUnknown)
            {
                return AuthenticodeInfo.Unsigned();
            }

            var info = new AuthenticodeInfo { Signed = true, Valid = rc == 0, Status = rc == 0 ? "valid signature" : "signature check failed (0x" + rc.ToString("X8") + ")" };
            try
            {
                using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    info.Subject = cert.Subject;
                    info.Thumbprint = cert.Thumbprint;
                }
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException || ex is PlatformNotSupportedException)
            {
                info.Valid = false;
                info.Status = "signer certificate unreadable (" + ex.Message + ")";
            }

            return info;
        }

        private static uint Verify(string path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            IntPtr pGuid = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                Marshal.StructureToPtr(GenericVerifyV2, pGuid, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,            // WTD_UI_NONE
                    fdwRevocationChecks = 0,   // WTD_REVOKE_NONE: works offline, never contacts a CRL server
                    dwUnionChoice = 1,         // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 1,         // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x00001000 | 0x00000080, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(data, pData, false);
                uint rc = (uint)WinVerifyTrust(new IntPtr(-1), pGuid, pData);
                data = (WINTRUST_DATA)Marshal.PtrToStructure(pData, typeof(WINTRUST_DATA));
                data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(data, pData, true);
                WinVerifyTrust(new IntPtr(-1), pGuid, pData);
                return rc;
            }
            finally
            {
                if (pData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(pData);
                }

                Marshal.FreeHGlobal(pGuid);
                Marshal.DestroyStructure(pFile, typeof(WINTRUST_FILE_INFO));
                Marshal.FreeHGlobal(pFile);
            }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, IntPtr pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
