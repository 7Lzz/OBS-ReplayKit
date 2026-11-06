using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    public static class ProcessHost
    {
        private static string Root => Config.PROGRAMFILES_OBS_DIR;
        private static string Backup => PathAt(Root, "bin/64bit/replaykit-host-backup");

        private static string PathAt(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A process host path escapes its folder.");
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Process host files cannot use reparse points: " + current);
            }
            return path;
        }

        private static string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string CurrentVersion() =>
            ObsVersions.InstalledVersion()?.ToString(3) ?? throw new IOException("OBS Studio is missing.");

        private static JObject Read(string folder, string version)
        {
            var manifest = JObject.Parse(File.ReadAllText(PathAt(folder, "manifest.json")));
            var line = ObsVersions.Lines.SingleOrDefault(item => item.Tested.ToString(3) == version)
                ?? throw new IOException("No shared process host is bundled for OBS " + version + ". Install a supported OBS version first.");
            string browser = line.ModernPlugins ? "core/obs-browser/" : "obs-plugins/64bit/";
            var paths = new[] { "bin/64bit/replaykit-host/obs64.exe", browser + "obs-browser.dll", browser + "obs-browser-role.dll" };
            if ((string)manifest["ObsVersion"] != version || !(manifest["Files"] is JArray files) || files.Count != paths.Length)
                throw new InvalidDataException("The process host manifest does not match OBS " + version + ".");
            foreach (string path in paths)
            {
                var matches = files.OfType<JObject>().Where(item => (string)item["Path"] == path).ToArray();
                if (matches.Length != 1 || !ValidHash((string)matches[0]["Sha256"]))
                    throw new InvalidDataException("The process host manifest has an invalid file: " + path);
                bool original = path.EndsWith("/obs-browser.dll", StringComparison.Ordinal);
                if (original ? !ValidHash((string)matches[0]["OriginalSha256"]) : matches[0]["OriginalSha256"]?.Type != JTokenType.Null)
                    throw new InvalidDataException("The process host manifest has an invalid original: " + path);
            }
            return manifest;
        }

        private static bool ValidHash(string hash) => Regex.IsMatch(hash ?? "", "^[0-9A-F]{64}$");

        private static (string Folder, JObject Manifest) Bundle()
        {
            string version = CurrentVersion();
            string folder = PathAt(Config.ASSETS_DIR, "process-host-roles/" + version);
            var manifest = Read(folder, version);
            foreach (var file in (JArray)manifest["Files"])
                if (Hash(PathAt(folder, (string)file["Path"])) != (string)file["Sha256"])
                    throw new InvalidDataException("A bundled process host file failed verification.");
            return (folder, manifest);
        }

        public static void Validate() => Bundle();

        private static void RequireClosed()
        {
            string root = Path.GetFullPath(Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var process in Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs-browser-page")))
            {
                using (process)
                {
                    if (process.HasExited) continue;
                    string image;
                    try { image = process.MainModule.FileName; }
                    catch (InvalidOperationException) when (process.HasExited) { continue; }
                    if (Path.GetFullPath(image).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Close OBS and its browser and helper processes before changing the process host.");
                }
            }
        }

        private static void Copy(string source, string target, string expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary);
                if (Hash(temporary) != expected) throw new IOException("Process host copy verification failed.");
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void Save(string path, JObject manifest)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, manifest.ToString(Formatting.Indented));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void Install(Action<string> log = null)
        {
            var bundle = Bundle();
            string version = (string)bundle.Manifest["ObsVersion"];
            string backup = PathAt(Backup, version);
            string record = PathAt(backup, "manifest.json");
            JObject previous = File.Exists(record) ? Read(backup, version) : null;
            RequireClosed();
            var files = ((JArray)bundle.Manifest["Files"]).OfType<JObject>().ToArray();
            foreach (var file in files)
            {
                string relative = (string)file["Path"];
                string original = (string)file["OriginalSha256"];
                if (original == null) continue;
                string target = PathAt(Root, relative);
                string current = Hash(target);
                string saved = PathAt(backup, relative);
                if (previous != null)
                {
                    var old = ((JArray)previous["Files"]).OfType<JObject>().Single(item => (string)item["Path"] == relative);
                    if ((string)old["OriginalSha256"] != original || Hash(saved) != original)
                        throw new InvalidDataException("The original OBS backup failed verification.");
                    if (current != original && current != (string)old["Sha256"])
                        throw new IOException("An OBS host file changed outside ReplayKit: " + target);
                }
                else
                {
                    if (current != original)
                        throw new IOException("The original OBS " + version + " files are required before installing the shared process host. Repair OBS Studio, then run ReplayKit setup again.");
                    Copy(target, saved, original);
                }
            }
            Change(files, file => Copy(PathAt(bundle.Folder, (string)file["Path"]), PathAt(Root, (string)file["Path"]), (string)file["Sha256"]),
                () => Save(record, bundle.Manifest));
            log?.Invoke("installed the shared OBS " + version + " process host");
        }

        private static void Change(JObject[] files, Action<JObject> action, Action commit)
        {
            string staging = PathAt(Backup, "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var snapshots = new List<(string Target, string Saved, string Hash)>();
            var changed = new List<(string Target, string Saved, string Hash)>();
            bool preserve = false;
            try
            {
                foreach (var file in files)
                {
                    string relative = (string)file["Path"];
                    string target = PathAt(Root, relative);
                    string saved = PathAt(staging, relative);
                    string hash = File.Exists(target) ? Hash(target) : null;
                    if (hash != null) Copy(target, saved, hash);
                    snapshots.Add((target, saved, hash));
                }
                // install the browser last so it always points at complete components
                foreach (var file in files.OrderBy(item => ((string)item["Path"]).EndsWith("/obs-browser.dll", StringComparison.Ordinal)))
                {
                    action(file);
                    changed.Add(snapshots.Single(snapshot => snapshot.Target == PathAt(Root, (string)file["Path"])));
                }
                commit();
            }
            catch (Exception failure)
            {
                var failures = new List<Exception> { failure };
                foreach (var snapshot in changed.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (snapshot.Hash != null) Copy(snapshot.Saved, snapshot.Target, snapshot.Hash);
                        else if (File.Exists(snapshot.Target)) File.Delete(snapshot.Target);
                    }
                    catch (Exception rollback) { failures.Add(rollback); }
                }
                if (failures.Count > 1)
                {
                    preserve = true;
                    throw new AggregateException("The process host update and rollback failed. Recovery files were kept at " + staging, failures);
                }
                throw;
            }
            finally { if (!preserve) Directory.Delete(staging, true); }
        }

        public static bool Restore(Action<string> log = null)
        {
            if (!File.Exists(PathAt(Root, "bin/64bit/obs64.exe"))) return true;
            string version = CurrentVersion();
            string backup = PathAt(Backup, version);
            string record = PathAt(backup, "manifest.json");
            if (!File.Exists(record))
            {
                if (File.Exists(PathAt(Root, "bin/64bit/replaykit-host/obs64.exe")))
                    return RemoveStale(version, log);
                return true;
            }
            RequireClosed();
            var manifest = Read(backup, version);
            var files = ((JArray)manifest["Files"]).OfType<JObject>().ToArray();
            foreach (var file in files)
            {
                string relative = (string)file["Path"];
                string original = (string)file["OriginalSha256"];
                string target = PathAt(Root, relative);
                string current = File.Exists(target) ? Hash(target) : null;
                if (current != (string)file["Sha256"] && current != original)
                    throw new IOException("An OBS host file changed outside ReplayKit: " + target);
                if (original != null && Hash(PathAt(backup, relative)) != original)
                    throw new InvalidDataException("The original OBS backup failed verification.");
            }
            Change(files, file =>
            {
                string target = PathAt(Root, (string)file["Path"]);
                string original = (string)file["OriginalSha256"];
                if (original != null) Copy(PathAt(backup, (string)file["Path"]), target, original);
                else if (File.Exists(target)) File.Delete(target);
            }, () => File.Delete(record));
            foreach (var file in files.Where(item => item["OriginalSha256"]?.Type == JTokenType.String))
                File.Delete(PathAt(backup, (string)file["Path"]));
            log?.Invoke("restored the original OBS " + version + " browser");
            return true;
        }

        private static bool RemoveStale(string version, Action<string> log)
        {
            RequireClosed();
            var current = Bundle().Manifest;
            var browser = ((JArray)current["Files"]).OfType<JObject>().Single(file => file["OriginalSha256"]?.Type == JTokenType.String);
            if (Hash(PathAt(Root, (string)browser["Path"])) != (string)browser["OriginalSha256"])
                throw new IOException("The current OBS browser is not the original supported file. Repair OBS Studio before removing ReplayKit.");
            string launcher = PathAt(Root, "bin/64bit/replaykit-host/obs64.exe");
            string launcherHash = Hash(launcher);
            foreach (var line in ObsVersions.Lines.Where(item => item.Tested.ToString(3) != version))
            {
                string older = line.Tested.ToString(3);
                string folder = PathAt(Backup, older);
                string record = PathAt(folder, "manifest.json");
                if (!File.Exists(record)) continue;
                var manifest = Read(folder, older);
                var files = ((JArray)manifest["Files"]).OfType<JObject>().ToArray();
                if (files.Single(file => (string)file["Path"] == "bin/64bit/replaykit-host/obs64.exe")["Sha256"].Value<string>() != launcherHash)
                    continue;
                foreach (var file in files)
                {
                    string target = PathAt(Root, (string)file["Path"]);
                    string hash = File.Exists(target) ? Hash(target) : null;
                    if (hash != null && hash != (string)file["Sha256"] && hash != (string)file["OriginalSha256"])
                        throw new IOException("A stale process role file changed outside ReplayKit: " + target);
                }
                Change(files, file =>
                {
                    string target = PathAt(Root, (string)file["Path"]);
                    if (File.Exists(target) && Hash(target) == (string)file["Sha256"]) File.Delete(target);
                }, () => File.Delete(record));
                log?.Invoke("removed verified OBS " + older + " process roles; the current OBS " + version + " browser was preserved");
                return true;
            }
            throw new IOException("The process role backup is missing. Repair OBS Studio before removing ReplayKit.");
        }
    }
}
