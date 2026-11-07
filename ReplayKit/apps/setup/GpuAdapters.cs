using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    // the setup half of shared/Gpu.cs: where the adapter rows come from in the installer
    public static partial class Gpu
    {
        // one row per installed display adapter, read from the registry instead of Win32_VideoController -- System.Management is not trim-safe (WmiNetUtilsHelper throws under the linker) and this project publishes trimmed. DriverDesc is the same marketing string wmis Name returns; qwMemorySize (REG_QWORD, newer drivers) or MemorySize (older) is the vram. returns [] on any failure.
        private static List<JObject> QueryVideoControllers()
        {
            var result = new List<JObject>();
            try
            {
                foreach (string path in ReplayKitSetup.Display.AdapterRegistryPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    using (var adapter = Registry.LocalMachine.OpenSubKey(path))
                    {
                        string name = adapter?.GetValue("DriverDesc") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        long? ram = ReadVram(adapter);
                        result.Add(new JObject
                        {
                            ["Name"] = name,
                            ["AdapterRAM"] = ram,
                            ["DriverVersion"] = adapter.GetValue("DriverVersion") as string,
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is System.IO.IOException) { }
            return result;
        }

        private static long? ReadVram(RegistryKey adapter)
        {
            try
            {
                object q = adapter.GetValue("HardwareInformation.qwMemorySize");
                if (q != null) return Convert.ToInt64(q);
                object m = adapter.GetValue("HardwareInformation.MemorySize");
                if (m is int mi) return mi == -1 ? (long?)null : unchecked((uint)mi);
                if (m is byte[] mb && mb.Length == 4) return BitConverter.ToUInt32(mb, 0);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException || ex is FormatException) { }
            return null;
        }
    }
}