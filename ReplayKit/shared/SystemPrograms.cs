using System;
using System.IO;

namespace ReplayKitShared
{
    // the windows programs replaykit starts, by full path: a bare name is looked for in the folder of the running exe first, and the setup runs as admin from wherever it was downloaded
    public static class SystemPrograms
    {
        public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        public static string Powercfg => Path.Combine(Environment.SystemDirectory, "powercfg.exe");
        public static string Curl => Path.Combine(Environment.SystemDirectory, "curl.exe");
    }
}
