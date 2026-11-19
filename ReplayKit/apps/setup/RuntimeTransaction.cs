using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitSetup
{
    internal sealed class RuntimeTransaction : IDisposable
    {
        private readonly string runtime;
        private readonly string work;
        private readonly FileStream gate;

        public static RuntimeTransaction Open()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!Path.IsPathFullyQualified(programFiles)) throw new IOException("Windows Program Files was not found.");
            string runtime = Path.GetFullPath(Config.REPLAYKIT_CONFIG);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtime.ToUpperInvariant())));
            return new RuntimeTransaction(runtime, PathAt(programFiles, Path.Combine("OBS ReplayKit Recovery", key)));
        }

        internal RuntimeTransaction(string runtime, string directory)
        {
            this.runtime = runtime;
            Directory.CreateDirectory(directory);
            work = PathAt(directory, "runtime");
            gate = new FileStream(PathAt(directory, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        public void Dispose() => gate.Dispose();

        public string Target(string relative) => PathAt(runtime, relative);

        public string Stage(string relative) => PathAt(work, Path.Combine("stage", relative));

        private string Backup(string relative) => PathAt(work, Path.Combine("backup", relative));
        private string Journal => PathAt(work, "changes.json");
        public bool Pending => File.Exists(Journal);

        public void Prepare(IEnumerable<string> paths)
        {
            var entries = new JArray();
            foreach (string relative in paths)
            {
                string staged = Stage(relative);
                Installer.FlushFile(staged);
                string target = Target(relative);
                bool existed = File.Exists(target);
                if (existed)
                {
                    string backup = Backup(relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(target, backup);
                    Installer.VerifyFile(target, backup);
                    Installer.FlushFile(backup);
                }
                entries.Add(new JObject { ["path"] = relative, ["existed"] = existed });
            }
            string pending = PathAt(work, "preparing.json");
            File.WriteAllText(pending, entries.ToString(Formatting.None));
            Installer.FlushFile(pending);
            File.Move(pending, Journal);
        }

        public void Recover(Action<string> log)
        {
            if (!Directory.Exists(work)) return;
            if (Pending)
            {
                var entries = JArray.Parse(File.ReadAllText(Journal));
                foreach (var entry in entries.Reverse())
                {
                    if (entry is not JObject item || item["path"]?.Type != JTokenType.String || item["existed"]?.Type != JTokenType.Boolean)
                        throw new InvalidDataException("The ReplayKit runtime recovery journal is invalid.");
                    string relative = (string)item["path"];
                    string target = Target(relative);
                    bool exists = File.Exists(target);
                    bool existed = (bool)item["existed"];
                    string backup = existed ? Backup(relative) : null;
                    if (exists && existed && Installer.FilesMatch(backup, target)) continue;
                    if (exists && !Installer.FilesMatch(Stage(relative), target))
                        throw new IOException("A ReplayKit runtime file changed outside the update: " + relative);
                    if (existed) Installer.ReplaceFile(backup, target, Guid.NewGuid().ToString("N"));
                    else if (exists) File.Delete(target);
                }
                log?.Invoke("recovered the previous ReplayKit runtime update");
            }
            Complete(log);
            if (Directory.Exists(work)) throw new IOException("Could not clear the previous ReplayKit update staging directory.");
        }

        public void Complete(Action<string> log)
        {
            if (Pending) File.Move(Journal, PathAt(work, "complete.json"));
            try { Directory.Delete(work, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("warn: could not remove completed runtime update staging: " + ex.Message);
            }
        }

        private static string PathAt(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Split('\\', '/').Any(part =>
                part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidDataException("Invalid ReplayKit runtime path.");
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A ReplayKit runtime path escaped its directory.");
            for (string parent = path; parent != null; parent = Path.GetDirectoryName(parent))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(parent); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("ReplayKit runtime recovery cannot use reparse points: " + parent);
            }
            return path;
        }
    }
}
