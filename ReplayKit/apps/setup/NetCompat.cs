using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ReplayKitSetup
{
    // the .net 8 apis the setup was written against that .net framework 4.8 lacks, each doing what its .net 8 namesake does on windows (the path helpers follow the .net runtime source), so the code that calls them reads the same on both
    internal static class NetCompat
    {
        private const uint MOVEFILE_REPLACE_EXISTING = 0x1;
        private const uint MOVEFILE_COPY_ALLOWED = 0x2;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        public static string ProcessPath
        {
            get { using (var self = Process.GetCurrentProcess()) return self.MainModule?.FileName; }
        }

        public static int ProcessId
        {
            get { using (var self = Process.GetCurrentProcess()) return self.Id; }
        }

        public static byte[] Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        public static byte[] Sha256(Stream data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        // upper case like Convert.ToHexString
        public static string ToHex(byte[] data)
        {
            var text = new StringBuilder(data.Length * 2);
            foreach (byte b in data) text.Append(b.ToString("X2"));
            return text.ToString();
        }

        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using var random = RandomNumberGenerator.Create();
            random.GetBytes(bytes);
            return bytes;
        }

        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        // File.Move with overwrite is MoveFileExW with these flags in .net 8
        public static void MoveFile(string sourceFileName, string destFileName, bool overwrite)
        {
            if (sourceFileName == null) throw new ArgumentNullException(nameof(sourceFileName));
            if (destFileName == null) throw new ArgumentNullException(nameof(destFileName));
            string source = Path.GetFullPath(sourceFileName);
            string dest = Path.GetFullPath(destFileName);
            if (!File.Exists(source)) throw new FileNotFoundException("Could not find file '" + source + "'.", source);
            if (MoveFileExW(source, dest, MOVEFILE_COPY_ALLOWED | (overwrite ? MOVEFILE_REPLACE_EXISTING : 0))) return;
            int error = Marshal.GetLastWin32Error();
            string message = new Win32Exception(error).Message + " : '" + source + "'";
            switch (error)
            {
                case 2: throw new FileNotFoundException(message, source);
                case 3: throw new DirectoryNotFoundException(message);
                case 5: throw new UnauthorizedAccessException(message);
                default: throw new IOException(message, unchecked((int)0x80070000 | error));
            }
        }

        public static DirectoryInfo CreateTempSubdirectory(string prefix)
        {
            string root = Path.GetTempPath();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                string path = Path.Combine(root, (prefix ?? "") + Path.GetRandomFileName().Replace(".", ""));
                if (Directory.Exists(path) || File.Exists(path)) continue;
                return Directory.CreateDirectory(path);
            }
            throw new IOException("Could not create a unique temporary folder.");
        }

        private static bool IsSeparator(char c) => c == '\\' || c == '/';

        private static bool IsDriveLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        public static bool IsPathFullyQualified(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (path.Length < 2) return false;
            if (IsSeparator(path[0])) return path[1] == '?' || IsSeparator(path[1]);
            return path.Length >= 3 && path[1] == ':' && IsSeparator(path[2]) && IsDriveLetter(path[0]);
        }

        public static string TrimEndingDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path) || !IsSeparator(path[path.Length - 1])) return path;
            string root = Path.GetPathRoot(path);
            return root != null && root.Length == path.Length ? path : path.Substring(0, path.Length - 1);
        }

        public static string GetRelativePath(string relativeTo, string path)
        {
            if (string.IsNullOrEmpty(relativeTo)) throw new ArgumentException("The path is empty.", nameof(relativeTo));
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("The path is empty.", nameof(path));
            relativeTo = Path.GetFullPath(relativeTo);
            path = Path.GetFullPath(path);
            string fromRoot = Path.GetPathRoot(relativeTo) ?? "";
            string toRoot = Path.GetPathRoot(path) ?? "";
            if (fromRoot.Length != toRoot.Length || string.Compare(relativeTo, 0, path, 0, fromRoot.Length, StringComparison.OrdinalIgnoreCase) != 0) return path;

            int common = CommonPathLength(relativeTo, path);
            if (common == 0) return path;
            int fromLength = relativeTo.Length;
            if (IsSeparator(relativeTo[fromLength - 1])) fromLength--;
            bool toEndsInSeparator = IsSeparator(path[path.Length - 1]);
            int toLength = path.Length;
            if (toEndsInSeparator) toLength--;
            if (fromLength == toLength && common >= fromLength) return ".";

            var result = new StringBuilder(Math.Max(relativeTo.Length, path.Length));
            if (common < fromLength)
            {
                result.Append("..");
                for (int i = common + 1; i < fromLength; i++)
                {
                    if (!IsSeparator(relativeTo[i])) continue;
                    result.Append(Path.DirectorySeparatorChar);
                    result.Append("..");
                }
            }
            else if (IsSeparator(path[common]))
            {
                common++;
            }
            int difference = toLength - common;
            if (toEndsInSeparator) difference++;
            if (difference > 0)
            {
                if (result.Length > 0) result.Append(Path.DirectorySeparatorChar);
                result.Append(path, common, difference);
            }
            return result.ToString();
        }

        private static int CommonPathLength(string first, string second)
        {
            int length = Math.Min(first.Length, second.Length);
            int common = 0;
            while (common < length && char.ToUpperInvariant(first[common]) == char.ToUpperInvariant(second[common])) common++;
            if (common == 0) return 0;
            if (common == first.Length && (common == second.Length || IsSeparator(second[common]))) return common;
            if (common == second.Length && IsSeparator(first[common])) return common;
            while (common > 0 && !IsSeparator(first[common - 1])) common--;
            return common;
        }

        // ZipFile.ExtractToDirectory with overwrite: every entry must land inside the folder, and a file already there is replaced
        public static void ExtractToDirectory(string archivePath, string destination, bool overwrite)
        {
            string root = Path.GetFullPath(destination);
            Directory.CreateDirectory(root);
            string prefix = root.EndsWith("\\", StringComparison.Ordinal) ? root : root + "\\";
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Extracting the zip entry would land outside the target folder.");
                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                entry.ExtractToFile(target, overwrite);
            }
        }
    }
}
