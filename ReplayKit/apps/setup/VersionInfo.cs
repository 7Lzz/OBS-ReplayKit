using System;
using System.Reflection;

namespace ReplayKitSetup
{
    // the release version is set only in ReplayKit/version.json: scripts/sync_version.ps1 carries it into Version.props and the csproj stamps it on this assembly as ReplayKitVersion, which is read back here
    public static class VersionInfo
    {
        public static readonly string Version = Read();

        private static string Read()
        {
            foreach (var meta in typeof(VersionInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (meta.Key == "ReplayKitVersion" && !string.IsNullOrEmpty(meta.Value)) return meta.Value;
            }
            throw new InvalidOperationException("This setup was built without its ReplayKit version.");
        }
    }
}
