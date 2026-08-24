using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ReplayKitHelper
{
    // aes-256-gcm decrypt via CNG (bcrypt.dll) -- net48 has no System.Security.Cryptography.AesGcm (that needs netstandard2.1+), so this p/invoke stays even in the compiled port. used to decrypt chrome/edge's v10/v11-prefixed encrypted cookie values (the aes key itself comes from dpapi, see BrowserCookies.cs). ported from obs_replaykit helper modules/30_native.ps1's BCryptAesGcm Add-Type block.
    internal static class NativeCrypto
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;
        }

        [DllImport("bcrypt.dll")] private static extern uint BCryptOpenAlgorithmProvider(out IntPtr phAlgorithm, [MarshalAs(UnmanagedType.LPWStr)] string pszAlgId, [MarshalAs(UnmanagedType.LPWStr)] string pszImplementation, uint dwFlags);
        [DllImport("bcrypt.dll")] private static extern uint BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);
        [DllImport("bcrypt.dll")] private static extern uint BCryptSetProperty(IntPtr hObject, [MarshalAs(UnmanagedType.LPWStr)] string pszProperty, byte[] pbInput, int cbInput, int dwFlags);
        [DllImport("bcrypt.dll")] private static extern uint BCryptGenerateSymmetricKey(IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject, byte[] pbSecret, int cbSecret, int dwFlags);
        [DllImport("bcrypt.dll")] private static extern uint BCryptDestroyKey(IntPtr hKey);
        [DllImport("bcrypt.dll")]
        private static extern uint BCryptDecrypt(IntPtr hKey, byte[] pbInput, int cbInput, ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            byte[] pbIV, int cbIV, byte[] pbOutput, int cbOutput, out int pcbResult, int dwFlags);

        private const int cbSizeOfInfo = 48; // sizeof(BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO) on x64

        private static void ThrowIfError(uint status, string what)
        {
            if (status != 0) throw new InvalidOperationException(what + " failed, NTSTATUS=0x" + status.ToString("X8"));
        }

        // no aad is used -- matches chromium's cookie encryption, which has no associated data.
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
        {
            IntPtr hAlg = IntPtr.Zero, hKey = IntPtr.Zero;
            try
            {
                ThrowIfError(BCryptOpenAlgorithmProvider(out hAlg, "AES", null, 0), "BCryptOpenAlgorithmProvider");
                byte[] chainingMode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                ThrowIfError(BCryptSetProperty(hAlg, "ChainingMode", chainingMode, chainingMode.Length, 0), "BCryptSetProperty(ChainingMode)");
                ThrowIfError(BCryptGenerateSymmetricKey(hAlg, out hKey, IntPtr.Zero, 0, key, key.Length, 0), "BCryptGenerateSymmetricKey");

                GCHandle nonceHandle = GCHandle.Alloc(nonce, GCHandleType.Pinned);
                GCHandle tagHandle = GCHandle.Alloc(tag, GCHandleType.Pinned);
                try
                {
                    var info = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
                    {
                        cbSize = cbSizeOfInfo,
                        dwInfoVersion = 1,
                        pbNonce = nonceHandle.AddrOfPinnedObject(),
                        cbNonce = nonce.Length,
                        pbAuthData = IntPtr.Zero,
                        cbAuthData = 0,
                        pbTag = tagHandle.AddrOfPinnedObject(),
                        cbTag = tag.Length,
                        pbMacContext = IntPtr.Zero,
                        cbMacContext = 0,
                        cbAAD = 0,
                        cbData = 0,
                        dwFlags = 0
                    };
                    byte[] output = new byte[ciphertext.Length];
                    uint rc = BCryptDecrypt(hKey, ciphertext, ciphertext.Length, ref info, null, 0, output, output.Length, out int written, 0);
                    ThrowIfError(rc, "BCryptDecrypt");
                    if (written != output.Length) Array.Resize(ref output, written);
                    return output;
                }
                finally
                {
                    nonceHandle.Free();
                    tagHandle.Free();
                }
            }
            finally
            {
                if (hKey != IntPtr.Zero) BCryptDestroyKey(hKey);
                if (hAlg != IntPtr.Zero) BCryptCloseAlgorithmProvider(hAlg, 0);
            }
        }
    }
}
