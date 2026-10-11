using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    // keeps a verified copy of this setup exe where the in-app uninstall and the discord screenshare buttons can run it; the sidecar next to the copy (sha256, size, version) is what the helper checks before it starts the copy elevated, so a half written or damaged file is told apart from a good one
    public static class SetupCache
    {
        // the floor the updater also puts on a download: a dotnet host or a build without its payload falls far under it
        public const long MinExeBytes = 10L * 1024 * 1024;

        public static string MetaPath(string exePath) => exePath + ".json";

        // the running exe itself, not BUNDLE_ROOT: with IncludeAllContentForSelfExtract the app base directory is the temp extraction folder, which never holds the exe; ProcessPath also survives a renamed download
        public static bool Provision(Action<string> log = null) => Provision(Environment.ProcessPath, Config.REPLAYKIT_SETUP_EXE, log);

        internal static bool Provision(string source, string target, Action<string> log)
        {
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
            {
                log?.Invoke("warn: the running setup exe was not found, so it was not cached for in-app cleanup");
                return false;
            }
            long size = new FileInfo(source).Length;
            if (size < MinExeBytes)
            {
                log?.Invoke("warn: " + source + " is not the packaged setup exe (" + size + " bytes), so it was not cached for in-app cleanup");
                return false;
            }
            try
            {
                string hash = Sha256(source);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                bool same = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
                    || (File.Exists(target) && new FileInfo(target).Length == size && string.Equals(Sha256(target), hash, StringComparison.OrdinalIgnoreCase));
                if (!same) CopyVerified(source, target, hash, size);
                WriteMeta(target, hash, size);
                log?.Invoke((same ? "setup executable already cached -> " : "cached setup executable -> ") + target);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: setup executable cache failed: " + ex.Message);
                return false;
            }
        }

        // the copy is written beside the target, checked against the source, and only then swapped in, so a copy that stops half way never replaces a good file
        private static void CopyVerified(string source, string target, string hash, long size)
        {
            string temp = target + ".new";
            try
            {
                File.Copy(source, temp, true);
                if (new FileInfo(temp).Length != size || !string.Equals(Sha256(temp), hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("the copy of the setup exe did not match the original");
                Installer.WriteWithRetry(() =>
                {
                    if (File.Exists(target)) File.Replace(temp, target, null, true);
                    else File.Move(temp, target);
                });
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        private static void WriteMeta(string exePath, string hash, long size)
        {
            var meta = new JObject
            {
                ["sha256"] = hash,
                ["size"] = size,
                ["version"] = VersionInfo.Version,
                ["cachedAt"] = DateTime.UtcNow.ToString("o"),
            };
            string path = MetaPath(exePath);
            string temp = path + ".new";
            Installer.WriteWithRetry(() =>
            {
                File.WriteAllText(temp, meta.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            });
        }

        internal static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
    }
}
