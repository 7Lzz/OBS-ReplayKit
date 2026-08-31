using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace ReplayKitSetup
{
    // assets\ is embedded in the release exe as one zip resource (see PackAssetBundle in ReplayKitSetup.csproj). the auto-updater downloads a bare OBSReplayKit.exe into %temp% and runs it there, so without an embedded copy the update closes obs and then has nothing to install -- which is exactly what a sibling-folder-only build did. unpacked once per version and reused after that.
    internal static class AssetBundle
    {
        private const string ResourceName = "ReplayKitSetup.assets.bundle.zip";

        // why the embedded copy could not be unpacked, for the caller to put in front of the user instead of a bare "assets not found".
        public static string LastError { get; private set; }

        private static string BundleRoot() => Path.Combine(Path.GetTempPath(), "ReplayKit", "bundle");

        // returns the unpacked assets dir, or null when this exe has no embedded bundle (a plain dotnet build) or unpacking failed -- LastError carries the reason in the failure case.
        public static string TryExtract()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var resource = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (resource == null) return null;

                    string root = BundleRoot();
                    string dest = Path.Combine(root, VersionInfo.Version);
                    string marker = dest + ".complete";
                    if (File.Exists(marker) && Directory.Exists(dest)) return dest;

                    Directory.CreateDirectory(root);
                    // one unpack at a time -- two setup processes (a double-clicked exe, or an update spawned while setup is open) would otherwise interleave writes into the same folder and produce a half-unpacked bundle neither of them can tell apart from a finished one.
                    using (var gate = new Mutex(false, @"Local\OBSReplayKit-asset-bundle"))
                    {
                        bool held;
                        try { held = gate.WaitOne(TimeSpan.FromMinutes(2)); }
                        catch (AbandonedMutexException) { held = true; }
                        if (!held) throw new IOException("Timed out waiting for another OBS ReplayKit process to finish unpacking its bundled files.");
                        try
                        {
                            if (File.Exists(marker) && Directory.Exists(dest)) return dest;
                            ExtractTo(resource, dest, marker);
                        }
                        finally { gate.ReleaseMutex(); }
                    }

                    PruneOtherVersions(root, dest);
                    return dest;
                }
            }
            catch (Exception ex)
            {
                LastError = "Unpacking the bundled ReplayKit files failed: " + ex.Message;
                return null;
            }
        }

        private static void ExtractTo(Stream resource, string dest, string marker)
        {
            // a leftover dest with no marker is a partial unpack from a run that died midway, so it is never reused.
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.CreateDirectory(dest);

            string prefix = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
            using (var archive = new ZipArchive(resource, ZipArchiveMode.Read, true))
            {
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(dest, entry.FullName));
                    if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Bundled entry resolved outside the unpack folder: " + entry.FullName);
                    if (entry.Name.Length == 0)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }

            // written last: its presence is what marks dest as a complete unpack.
            File.WriteAllText(marker, VersionInfo.Version, new System.Text.UTF8Encoding(false));
        }

        // best-effort: drop bundles unpacked by older releases so %temp% doesnt collect a copy per version.
        private static void PruneOtherVersions(string root, string keep)
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                    try { Directory.Delete(dir, true); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                    try { File.Delete(dir + ".complete"); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
