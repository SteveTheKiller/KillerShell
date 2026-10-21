using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KillerShell.Services
{
    /// <summary>
    /// Authenticode state and content hash of the running exe.
    ///
    /// Reading a certificate out of a file only proves a certificate is embedded; it does not prove
    /// the signature validates. WinVerifyTrust does the full chain check, so a tampered or badly
    /// signed build cannot present its publisher as verified.
    /// </summary>
    internal static class CodeSignature
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint   cbStruct;
            public IntPtr pcwszFilePath;   // LPCWSTR
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint   cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint   dwUIChoice;          // 2 = WTD_UI_NONE
            public uint   fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
            public uint   dwUnionChoice;       // 1 = WTD_CHOICE_FILE
            public IntPtr pUnion;              // -> WINTRUST_FILE_INFO
            public uint   dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint   dwProvFlags;         // 0x1000 = WTD_CACHE_ONLY_URL_RETRIEVAL
            public uint   dwUIContext;
            public IntPtr pSignatureSettings;
        }

        private static readonly Guid WTD_VERIFY_GENERIC =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false,
                   CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        /// <summary>Reads the running exe's Authenticode certificate for display and validates the
        /// signature. Valid is false for unsigned, expired or tampered files. Retrieval is cache-only
        /// (0x1000), so nothing here waits on the network: a signed exe carries its own intermediates,
        /// so the chain still builds offline.</summary>
        internal static (bool valid, string subject, string thumb) GetSignerInfo()
        {
            string exePath;
            try { exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty; }
            catch { exePath = string.Empty; }
            if (exePath.Length == 0) return (false, "(unavailable)", "(none)");
            var (valid, subject, thumb, _) = Inspect(exePath);
            return (valid, subject, thumb);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> may replace the signed exe at
        /// <paramref name="current"/>. A copy with a valid signature only accepts a replacement
        /// whose signature also validates and names the same publisher: the certificate subject,
        /// which stays the same across certificate renewals. An unsigned copy (a local or
        /// development build) has no publisher to hold a replacement to, and accepts it as before.
        /// </summary>
        internal static bool MayReplace(string current, string candidate)
        {
            var (currentValid, _, _, currentPublisher) = Inspect(current);
            if (!currentValid) return true;
            var (candidateValid, _, _, candidatePublisher) = Inspect(candidate);
            return candidateValid && currentPublisher.Length > 0
                && string.Equals(currentPublisher, candidatePublisher, StringComparison.Ordinal);
        }

        private static (bool valid, string subject, string thumb, string publisher) Inspect(string exePath)
        {
            var subject = "(not signed)";
            var thumb   = "(none)";
            string publisher;

            try
            {
                using var sourceCertificate = X509Certificate.CreateFromSignedFile(exePath);
                using var cert = new X509Certificate2(sourceCertificate);
                var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
                subject   = string.IsNullOrEmpty(subj) ? cert.Subject : subj;
                thumb     = string.IsNullOrEmpty(cert.Thumbprint) ? "(none)" : cert.Thumbprint;
                publisher = cert.Subject ?? string.Empty;
            }
            catch { return (false, "(not signed)", "(none)", string.Empty); }

            var pathPtr     = Marshal.StringToHGlobalUni(exePath);
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var dataPtr     = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(new WINTRUST_FILE_INFO
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pathPtr
                }, fileInfoPtr, false);

                Marshal.StructureToPtr(new WINTRUST_DATA
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice    = 2,       // WTD_UI_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pUnion        = fileInfoPtr,
                    dwProvFlags   = 0x1000   // WTD_CACHE_ONLY_URL_RETRIEVAL, never hit the network
                }, dataPtr, false);

                var actionId = WTD_VERIFY_GENERIC;
                return (WinVerifyTrust(IntPtr.Zero, ref actionId, dataPtr) == 0, subject, thumb, publisher);
            }
            catch { return (false, subject, thumb, publisher); }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        /// <summary>SHA-256 of the running exe as lower-case hex. Slow on a large file, so callers
        /// run it off the UI thread.</summary>
        internal static string ExeSha256()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(unavailable)";
                using var sha = SHA256.Create();
                using var fs  = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "(unavailable)"; }
        }
    }
}
