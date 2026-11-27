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
        private const string ManifestName = "manifest.json";
        private const string PendingManifestName = "manifest.pending.json";
        private static string Root => Config.PROGRAMFILES_OBS_DIR;
        // the role host obs64.exe, relative to the obs folder as the manifest lists it
        private const string LauncherRel = "bin/64bit/" + ReplayKitShared.ObsExit.RoleHostFolder + "/obs64.exe";
        private static string Backup => PathAt(Root, "bin/64bit/replaykit-host-backup");

        private static string PathAt(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(SetupLocale.Message("setup.aProcessHostPathEscapesIts"));
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw SetupLocale.Error("setup.processHostFilesCannotUseALink", m => new IOException(m), "path", current);
            }
            return path;
        }

        private static string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return NetCompat.ToHex(NetCompat.Sha256(stream));
        }

        private static string CurrentVersion() =>
            ObsVersions.InstalledVersion()?.ToString(3) ?? throw new IOException(SetupLocale.Message("setup.obsStudioIsMissing"));

        private static JObject Read(string folder, string version, string name = ManifestName)
        {
            var manifest = JObject.Parse(File.ReadAllText(PathAt(folder, name)));
            var line = ObsVersions.Lines.SingleOrDefault(item => item.Tested.ToString(3) == version)
                ?? throw new IOException(SetupLocale.Message("setup.noProcessHostForObs", "version", version)).WithEnglish(SetupLocale.Pair("setup.noProcessHostForObs", "version", version));
            string browser = line.ModernPlugins ? "core/obs-browser/" : "obs-plugins/64bit/";
            var paths = new[] { LauncherRel, browser + "obs-browser.dll", browser + "obs-browser-role.dll" };
            if ((string)manifest["ObsVersion"] != version || !(manifest["Files"] is JArray files) || files.Count != paths.Length)
                throw SetupLocale.Error("setup.theProcessHostListDoesNotMatchObs", m => new InvalidDataException(m), "version", version);
            foreach (string path in paths)
            {
                var matches = files.OfType<JObject>().Where(item => (string)item["Path"] == path).ToArray();
                if (matches.Length != 1 || !ValidHash((string)matches[0]["Sha256"]))
                    throw SetupLocale.Error("setup.theProcessHostListHasABadFile", m => new InvalidDataException(m), "path", path);
                bool original = path.EndsWith("/obs-browser.dll", StringComparison.Ordinal);
                if (original ? !ValidHash((string)matches[0]["OriginalSha256"]) : matches[0]["OriginalSha256"]?.Type != JTokenType.Null)
                    throw SetupLocale.Error("setup.theProcessHostListHasABadOriginal", m => new InvalidDataException(m), "path", path);
            }
            return manifest;
        }

        private static bool ValidHash(string hash) => Regex.IsMatch(hash ?? "", "^[0-9A-F]{64}$");

        private static JObject Entry(JObject manifest, string path) =>
            (manifest?["Files"] as JArray)?.OfType<JObject>().Single(file => (string)file["Path"] == path);

        private static (string Folder, JObject Manifest) Bundle()
        {
            string version = CurrentVersion();
            string folder = PathAt(Config.ASSETS_DIR, "process-host-roles/" + version);
            var manifest = Read(folder, version);
            foreach (var file in (JArray)manifest["Files"])
                if (Hash(PathAt(folder, (string)file["Path"])) != (string)file["Sha256"])
                    throw new InvalidDataException(SetupLocale.Message("setup.aBundledProcessHostFileFailed"));
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
                        throw new IOException(SetupLocale.Message("setup.closeObsAndItsBrowserAnd"));
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
                if (Hash(temporary) != expected) throw new IOException(SetupLocale.Message("setup.processHostCopyVerificationFailed"));
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
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(manifest.ToString(Formatting.Indented));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static void Install(Action<string> log = null, Action beforeChange = null)
        {
            var bundle = Bundle();
            string version = (string)bundle.Manifest["ObsVersion"];
            string backup = PathAt(Backup, version);
            string record = PathAt(backup, ManifestName);
            string pending = PathAt(backup, PendingManifestName);
            JObject previous = File.Exists(record) ? Read(backup, version) : null;
            JObject interrupted = File.Exists(pending) ? Read(backup, version, PendingManifestName) : null;
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
                    var old = Entry(previous, relative);
                    if ((string)old["OriginalSha256"] != original || Hash(saved) != original)
                        throw new InvalidDataException(SetupLocale.Message("setup.theOriginalObsBackupFailedVerification"));
                    if (current != original && current != (string)old["Sha256"] &&
                        current != (string)Entry(interrupted, relative)?["Sha256"] && current != (string)file["Sha256"])
                        throw SetupLocale.Error("setup.anObsHostFileChangedOutsideReplaykit", m => new IOException(m), "path", target);
                }
                else
                {
                    if (current == original) Copy(target, saved, original);
                    else if ((current != (string)file["Sha256"] && current != (string)Entry(interrupted, relative)?["Sha256"]) ||
                        !File.Exists(saved) || Hash(saved) != original)
                        throw new IOException(SetupLocale.Message("setup.originalObsFilesRequired", "version", version)).WithEnglish(SetupLocale.Pair("setup.originalObsFilesRequired", "version", version));
                }
            }
            var needed = files.Where(file =>
            {
                string target = PathAt(Root, (string)file["Path"]);
                return !File.Exists(target) || Hash(target) != (string)file["Sha256"];
            }).ToArray();
            if (needed.Length == 0)
            {
                if (!JToken.DeepEquals(previous, bundle.Manifest)) Save(record, bundle.Manifest);
                File.Delete(pending);
                log?.Invoke("shared OBS " + version + " process host already current");
                return;
            }
            beforeChange?.Invoke();
            if (interrupted == null) Save(pending, bundle.Manifest);
            else if (!JToken.DeepEquals(interrupted, bundle.Manifest))
                throw new IOException(SetupLocale.Message("setup.finishTheInterruptedProcessHostUpdate"));
            Change(needed, file => Copy(PathAt(bundle.Folder, (string)file["Path"]), PathAt(Root, (string)file["Path"]), (string)file["Sha256"]),
                () => Save(record, bundle.Manifest));
            File.Delete(pending);
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
                    throw SetupLocale.Error("setup.theProcessHostUpdateAndRollbackFailed", m => new AggregateException(m, failures), "path", staging);
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
            string record = PathAt(backup, ManifestName);
            string pending = PathAt(backup, PendingManifestName);
            JObject interrupted = File.Exists(pending) ? Read(backup, version, PendingManifestName) : null;
            JObject manifest;
            if (File.Exists(record)) manifest = Read(backup, version);
            else if (interrupted != null) manifest = interrupted;
            else
            {
                string launcher = PathAt(Root, LauncherRel);
                if (!File.Exists(launcher)) return true;
                if (!ObsVersions.Lines.Any(line => line.Tested.ToString(3) == version)) return RemoveStale(version, log);
                manifest = Bundle().Manifest;
                var browser = ((JArray)manifest["Files"]).OfType<JObject>().Single(file => file["OriginalSha256"]?.Type == JTokenType.String);
                string savedBrowser = PathAt(backup, (string)browser["Path"]);
                if (!File.Exists(savedBrowser)) return RemoveStale(version, log);
            }
            RequireClosed();
            var files = ((JArray)manifest["Files"]).OfType<JObject>().ToArray();
            foreach (var file in files)
            {
                string relative = (string)file["Path"];
                string original = (string)file["OriginalSha256"];
                string target = PathAt(Root, relative);
                string current = File.Exists(target) ? Hash(target) : null;
                if (current != (string)file["Sha256"] && current != original && current != (string)Entry(interrupted, relative)?["Sha256"])
                    throw SetupLocale.Error("setup.anObsHostFileChangedOutsideReplaykit", m => new IOException(m), "path", target);
                if (original != null && Hash(PathAt(backup, relative)) != original)
                    throw new InvalidDataException(SetupLocale.Message("setup.theOriginalObsBackupFailedVerification"));
            }
            Change(files, file =>
            {
                string target = PathAt(Root, (string)file["Path"]);
                string original = (string)file["OriginalSha256"];
                if (original != null) Copy(PathAt(backup, (string)file["Path"]), target, original);
                else if (File.Exists(target)) File.Delete(target);
            }, () => File.Delete(record));
            File.Delete(pending);
            foreach (var file in files.Where(item => item["OriginalSha256"]?.Type == JTokenType.String))
                File.Delete(PathAt(backup, (string)file["Path"]));
            log?.Invoke("restored the original OBS " + version + " browser");
            return true;
        }

        private static bool RemoveStale(string version, Action<string> log)
        {
            RequireClosed();
            string launcher = PathAt(Root, LauncherRel);
            string launcherHash = Hash(launcher);
            foreach (var line in ObsVersions.Lines.Where(item => item.Tested.ToString(3) != version))
            {
                string older = line.Tested.ToString(3);
                string folder = PathAt(Backup, older);
                string record = PathAt(folder, ManifestName);
                if (!File.Exists(record)) continue;
                var manifest = Read(folder, older);
                var files = ((JArray)manifest["Files"]).OfType<JObject>().ToArray();
                if (files.Single(file => (string)file["Path"] == LauncherRel)["Sha256"].Value<string>() != launcherHash)
                    continue;
                foreach (var file in files)
                {
                    string target = PathAt(Root, (string)file["Path"]);
                    string hash = File.Exists(target) ? Hash(target) : null;
                    if (file["OriginalSha256"]?.Type == JTokenType.String && hash == (string)file["Sha256"])
                        throw new IOException(SetupLocale.Message("setup.theCurrentObsBrowserIsNot"));
                }
                Change(files, file =>
                {
                    string target = PathAt(Root, (string)file["Path"]);
                    if (file["OriginalSha256"]?.Type != JTokenType.String &&
                        File.Exists(target) && Hash(target) == (string)file["Sha256"]) File.Delete(target);
                }, () => File.Delete(record));
                log?.Invoke("removed verified OBS " + older + " process roles; the current OBS " + version + " browser was preserved");
                return true;
            }
            throw new IOException(SetupLocale.Message("setup.theProcessRoleBackupIsMissing"));
        }
    }
}
