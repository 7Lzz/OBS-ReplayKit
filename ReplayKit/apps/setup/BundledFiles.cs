using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace ReplayKitSetup
{
    internal static class BundledFiles
    {
        private static List<(ZipArchiveEntry Entry, string Relative, string Target)> Files(ZipArchive archive, string root, string prefix, Func<string, bool> include)
        {
            string directory = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var files = new List<(ZipArchiveEntry, string, string)>();
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.EndsWith("/", StringComparison.Ordinal)) continue;
                string relative = name.Substring(prefix.Length);
                if (include != null && !include(relative)) continue;
                var parts = relative.Split('/');
                if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException("Unsafe bundled archive path: " + name);
                string target = Path.GetFullPath(Path.Combine(directory, Path.Combine(parts)));
                if (!target.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Bundled file escapes its install directory.");
                files.Add((entry, relative, target));
            }
            if (files.Count == 0) throw new InvalidDataException("The bundled archive contains no installable files.");
            return files;
        }

        private static Stream Expected(ZipArchiveEntry entry, string relative, Func<string, byte[], byte[]> normalize)
        {
            if (normalize == null) return entry.Open();
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            return new MemoryStream(normalize(relative, bytes.ToArray()), false);
        }

        public static bool IsCurrent(string archive, string root, string prefix = "", Func<string, bool> include = null, Func<string, byte[], byte[]> normalize = null)
        {
            if (!File.Exists(archive)) return false;
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                foreach (var file in Files(zip, root, prefix, include))
                {
                    if (!File.Exists(file.Target)) return false;
                    using var expected = Expected(file.Entry, file.Relative, normalize);
                    using var installed = File.OpenRead(file.Target);
                    if (!SHA256.HashData(expected).SequenceEqual(SHA256.HashData(installed))) return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException) { return false; }
        }

        public static bool Install(string archive, string root, Action<string> log = null, string prefix = "", Func<string, bool> include = null, Func<string, byte[], byte[]> normalize = null)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var file in Files(zip, root, prefix, include))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file.Target));
                    string temporary = file.Target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var input = Expected(file.Entry, file.Relative, normalize))
                        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                        File.Move(temporary, file.Target, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            if (IsCurrent(archive, root, prefix, include, normalize)) return true;
            log?.Invoke("warn: installed files do not match the bundled archive.");
            return false;
        }
    }
}
