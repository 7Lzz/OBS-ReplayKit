using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ReplayKitSetup
{
    // whether a downloaded installer carries an intact authenticode signature that chains to a trusted root, and who signed it: the WinVerifyTrust check windows itself makes, the same one Get-AuthenticodeSignature reports as Valid, without starting powershell for it
    internal static class Authenticode
    {
        internal sealed class Result
        {
            public bool Valid;
            public string Status = "";
            public string Subject = "";
            public string Issuer = "";
        }

        internal static Result Check(string path)
        {
            IntPtr pathText = IntPtr.Zero;
            IntPtr fileInfo = IntPtr.Zero;
            try
            {
                pathText = Marshal.StringToHGlobalUni(path);
                fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
                Marshal.StructureToPtr(new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pathText }, fileInfo, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = fileInfo,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                };
                Guid action = GenericVerifyV2;
                // 0 when the signature is intact and trusted, otherwise the error windows gives for it
                int status = WinVerifyTrust(NoWindow, ref action, ref data);
                try
                {
                    var result = new Result { Valid = status == 0, Status = status == 0 ? "Valid" : "0x" + status.ToString("X8") };
                    // the signer is taken from the state windows has just verified, so the name that is checked is the name on that very signature
                    if (result.Valid && !ReadSigner(data.hWVTStateData, result))
                    {
                        result.Valid = false;
                        result.Status = "no signer certificate";
                    }
                    return result;
                }
                finally
                {
                    data.dwStateAction = WTD_STATEACTION_CLOSE;
                    WinVerifyTrust(NoWindow, ref action, ref data);
                }
            }
            finally
            {
                if (fileInfo != IntPtr.Zero) Marshal.FreeHGlobal(fileInfo);
                if (pathText != IntPtr.Zero) Marshal.FreeHGlobal(pathText);
            }
        }

        private static bool ReadSigner(IntPtr state, Result result)
        {
            IntPtr provider = state == IntPtr.Zero ? IntPtr.Zero : WTHelperProvDataFromStateData(state);
            IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            IntPtr chainEntry = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            // CRYPT_PROVIDER_CERT is a dword size followed by the certificate context, which alignment puts one pointer in
            IntPtr context = chainEntry == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(chainEntry, IntPtr.Size);
            if (context == IntPtr.Zero) return false;
            try
            {
                // the certificate object takes a reference of its own, so it outlives the state it came from
                using (var certificate = new X509Certificate2(context))
                {
                    result.Subject = certificate.Subject ?? "";
                    result.Issuer = certificate.Issuer ?? "";
                }
                return true;
            }
            catch (CryptographicException) { return false; }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        // INVALID_HANDLE_VALUE as the window: nothing may be shown for the check
        private static readonly IntPtr NoWindow = new IntPtr(-1);
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
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

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
    }
}
