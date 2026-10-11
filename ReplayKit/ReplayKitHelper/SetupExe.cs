using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    // a setup exe that is missing, damaged or could not be fetched; code is what the pages key on, message is what the user reads
    internal sealed class SetupExeException : InvalidOperationException
    {
        public string Code { get; }

        public SetupExeException(string code, string message, Exception inner = null) : base(message, inner)
        {
            Code = code;
        }
    }

    // the cached copy of the setup exe that the in-app uninstall and the discord screenshare buttons run. the setup exe puts it in place at every install and update, with a sidecar (sha256, size, version) beside it; this class looks at the copy before it is started elevated, and downloads it again from the latest release, checked against the published sha256, when it is gone or damaged
    internal static class SetupExe
    {
        // the same floor the setup exe and the updater use: a file under it cannot be the packaged exe
        private const long MinBytes = 10L * 1024 * 1024;
        private const string NoticeKey = "setup-file-missing";
        private static readonly object RepairGate = new object();

        public static string ExePath => Uninstall.GetSetupExecutable();
        private static string MetaPath => ExePath + ".json";

        public sealed class Inspection
        {
            public bool Valid;
            // ok, missing, incomplete or damaged
            public string State = "ok";
            public long Size;
            public string Version = "";
            // whether the sidecar vouched for the file; a copy made by a setup exe from before the sidecar existed is accepted but unverified
            public bool Verified;
        }

        public static Inspection Inspect()
        {
            string exe = ExePath;
            if (!File.Exists(exe)) return new Inspection { State = "missing" };
            long size = new FileInfo(exe).Length;
            if (size < MinBytes) return new Inspection { State = "incomplete", Size = size };
            if (!File.Exists(MetaPath)) return new Inspection { Valid = true, Size = size };
            try
            {
                var meta = JObject.Parse(File.ReadAllText(MetaPath));
                long metaSize = meta["size"]?.Value<long?>() ?? -1;
                string hash = meta["sha256"]?.Value<string>() ?? "";
                if (metaSize != size || !string.Equals(Sha256(exe), hash, StringComparison.OrdinalIgnoreCase))
                    return new Inspection { State = "damaged", Size = size };
                return new Inspection { Valid = true, Size = size, Verified = true, Version = meta["version"]?.Value<string>() ?? "" };
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return new Inspection { State = "damaged", Size = size };
            }
        }

        public static JObject GetStatus()
        {
            var seen = Inspect();
            return new JObject
            {
                ["ok"] = true,
                ["valid"] = seen.Valid,
                ["state"] = seen.State,
                ["verified"] = seen.Verified,
                ["size"] = seen.Size,
                ["version"] = seen.Version,
            };
        }

        // the path of a setup exe that is fit to run. a missing or damaged one is downloaded again first when allowDownload says so, otherwise it is reported so the caller can tell the user
        public static string Ensure(bool allowDownload)
        {
            var seen = Inspect();
            if (seen.Valid) return ExePath;
            if (!allowDownload) throw new SetupExeException("setup-missing", Describe(seen) + " Use Download again in Settings > Advanced, or run the latest OBSReplayKit.exe from GitHub once.");
            Repair();
            if (!Inspect().Valid) throw new SetupExeException("setup-missing", "The setup file is still not usable after downloading it again.");
            return ExePath;
        }

        private static string Describe(Inspection seen) =>
            seen.State == "missing" ? "ReplayKit's setup file is missing."
            : seen.State == "incomplete" ? "ReplayKit's setup file is incomplete (" + (seen.Size / (1024 * 1024)) + " MB)."
            : "ReplayKit's setup file does not match the copy that was cached, so it is not run.";

        // fetches the setup exe of the latest release into the cache. a second caller waits at the gate and then finds the file in place
        public static JObject Repair()
        {
            lock (RepairGate)
            {
                var before = Inspect();
                if (before.Valid) return Done("The setup file is already in place.");

                Update.ReleaseInfo release;
                try { release = Update.GetLatestRelease(true); }
                catch (Exception ex)
                {
                    throw new SetupExeException("setup-download-failed", "ReplayKit's setup file could not be downloaded again: GitHub could not be reached (" + ex.Message + "). Check the connection and try again, or run the latest OBSReplayKit.exe from GitHub once.", ex);
                }

                string tempDir = null;
                try
                {
                    Update.SetProgressStage("download");
                    tempDir = Update.CreateUpdateTemp();
                    string verified = Update.DownloadVerifiedInstaller(release, tempDir);
                    Store(verified, release.LatestVersion);
                }
                catch (SetupExeException) { throw; }
                catch (Exception ex)
                {
                    Log.Write("setup file repair failed: " + ex);
                    throw new SetupExeException("setup-download-failed", "ReplayKit's setup file could not be downloaded again: " + ex.Message, ex);
                }
                finally
                {
                    Update.SetProgressStage("");
                    try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }

                Log.Write("setup file downloaded again from release " + release.TagName + " and cached at " + ExePath);
                try { Notifications.DismissKey(NoticeKey); } catch (Exception ex) { Log.Write("setup file notice: " + ex.Message); }
                try { Notifications.Add("info", "setup-file-restored", "ReplayKit setup file restored", "The setup file that Install and Uninstall in Settings use was missing, so it was downloaded again from GitHub release " + release.LatestVersion + " and checked against its published hash."); }
                catch (Exception ex) { Log.Write("setup file notice: " + ex.Message); }
                return Done("The setup file was downloaded again from release " + release.LatestVersion + ".");
            }
        }

        private static JObject Done(string message)
        {
            var result = GetStatus();
            result["message"] = message;
            return result;
        }

        // the verified download goes in beside the target first and is swapped in only once the copy matches, so a copy that stops half way never replaces a good file
        private static void Store(string verifiedPath, string version)
        {
            string target = ExePath;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            long size = new FileInfo(verifiedPath).Length;
            string hash = Sha256(verifiedPath);
            string temp = target + ".new";
            try
            {
                File.Copy(verifiedPath, temp, true);
                if (new FileInfo(temp).Length != size || !string.Equals(Sha256(temp), hash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("the copy of the setup file did not match the download");
                SwapIn(temp, target);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            var meta = new JObject { ["sha256"] = hash, ["size"] = size, ["version"] = version, ["cachedAt"] = DateTime.UtcNow.ToString("o") };
            string metaTemp = MetaPath + ".new";
            File.WriteAllText(metaTemp, meta.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
            SwapIn(metaTemp, MetaPath);
        }

        // a file that is still being scanned or closed by another process lets go within a few seconds; one that is still in use after that is a setup or uninstall window that is open
        private static void SwapIn(string temp, string target)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Replace(temp, target, null, true);
                    else File.Move(temp, target);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt >= 20) throw new SetupExeException("setup-locked", "The setup file is in use. Close the ReplayKit setup or uninstall window and try again.", ex);
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        // on helper start: a missing or damaged setup file is logged and put on the bell, so it is seen before a button needs it; a good one clears an old notice. nothing is downloaded here
        public static void CheckAtStartup()
        {
            Task.Run(() =>
            {
                try
                {
                    var seen = Inspect();
                    if (seen.Valid)
                    {
                        Notifications.DismissKey(NoticeKey);
                        return;
                    }
                    Log.Write("setup file check: " + seen.State);
                    Notifications.Add("warn", NoticeKey, "ReplayKit setup file is " + (seen.State == "missing" ? "missing" : "damaged"), "Install and Uninstall in Settings > Advanced need it. They download it again by themselves, or use Download again there.");
                }
                catch (Exception ex) { Log.Write("setup file check: " + ex.Message); }
            });
        }
    }
}
