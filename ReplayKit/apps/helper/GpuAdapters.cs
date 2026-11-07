using System;
using System.Collections.Generic;
using System.Management;
using Newtonsoft.Json.Linq;

namespace ReplayKitShared
{
    // the helper half of shared/Gpu.cs: where the adapter rows come from in the helper, which queries wmi directly since it references System.Management and is not trimmed
    public static partial class Gpu
    {
        private static List<JObject> QueryVideoControllers()
        {
            var result = new List<JObject>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController"))
                using (var rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        long? ram = null;
                        try { if (row["AdapterRAM"] != null) ram = Convert.ToInt64(row["AdapterRAM"]); } catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException) { }
                        result.Add(new JObject
                        {
                            ["Name"] = row["Name"] as string,
                            ["AdapterRAM"] = ram,
                            ["DriverVersion"] = row["DriverVersion"] as string,
                        });
                    }
                }
            }
            catch (ManagementException) { }
            return result;
        }
    }
}