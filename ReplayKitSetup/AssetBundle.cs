using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ReplayKitSetup
{
    internal static class AssetBundle
    {
        private const string ResourceName = "ReplayKitSetup.assets.bundle.zip";
        private static readonly object Gate = new object();
        private static string extractedPath;

        public static string LastError { get; private set; }

        public static string TryExtract()
        {
            lock (Gate)
            {
                if (extractedPath != null) return extractedPath;
                LastError = null;
                string destination = null;
                try
                {
                    using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
                    if (resource == null) return null;

                    // Rebuilt releases can share a version. Each process owns fresh files
                    // instead of trusting a reusable temp directory and completion marker.
                    destination = Directory.CreateTempSubdirectory("OBSReplayKit-bundle-").FullName;
                    string prefix = destination.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                    using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
                    foreach (var entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Bundled entry is outside the unpack folder.");
                        if (entry.Name.Length == 0)
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target);
                    }
                    extractedPath = destination;
                    return extractedPath;
                }
                catch (Exception ex)
                {
                    LastError = "Unpacking the bundled ReplayKit files failed: " + ex.Message;
                    if (destination != null) DeleteExtracted(destination);
                    return null;
                }
            }
        }

        public static void Cleanup()
        {
            lock (Gate)
            {
                if (extractedPath == null) return;
                DeleteExtracted(extractedPath);
                extractedPath = null;
            }
        }

        private static void DeleteExtracted(string path)
        {
            try { Directory.Delete(path, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
