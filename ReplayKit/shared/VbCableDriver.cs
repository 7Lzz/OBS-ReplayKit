using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ReplayKitShared
{
    // whether the vb-audio cable that carries the share preview audio is installed, read from the driver descriptions of the sound device setup class so neither app needs System.Management
    internal static class VbCableDriver
    {
        // one NNNN subkey per installed audio driver; DriverDesc is the name Win32_SoundDevice gives
        private const string MediaClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
        private const string DriverDescription = "VB-Audio Virtual Cable";

        public static bool IsInstalled()
        {
            try
            {
                using (var classKey = Registry.LocalMachine.OpenSubKey(MediaClassKey))
                {
                    if (classKey == null) return false;
                    foreach (string sub in classKey.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                        using (var dev = classKey.OpenSubKey(sub))
                        {
                            string desc = dev?.GetValue("DriverDesc") as string;
                            if (!string.IsNullOrEmpty(desc) && desc.IndexOf(DriverDescription, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            return false;
        }
    }
}
