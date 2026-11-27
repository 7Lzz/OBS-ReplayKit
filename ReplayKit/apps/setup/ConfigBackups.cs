using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.Win32.SafeHandles;
using ReplayKitShared;

namespace ReplayKitSetup
{
    internal static class ConfigBackups
    {
        internal const string ClearFlag = "--clear-old-backups";
        private const string StampFormat = "yyyyMMdd-HHmmss";
        private const string StampPattern = @"[0-9]{8}-[0-9]{6}";
        private const string Reasons = "install|reinstall|update|app-update|imported";
        private const string ManifestName = "manifest.json";
        private const string PartialSuffix = ".partial";
        private const int Retained = 3;
        private const long FileLimit = 50L * 1024 * 1024;
        private const long ManifestLimit = 16L * 1024 * 1024;
        private const string Scenes = "basic/scenes";
        private const string InstallerPrefs = "installer-prefs.json";
        private const string CachedPrefs = "cached-installer-prefs.json";
        private const string DeletingSuffix = ".deleting";
        private const string ImportReason = "imported";
        private const uint ReadAttributes = 0x80;
        private const uint WriteAttributes = 0x100;
        private const uint DeleteAccess = 0x10000;
        private const uint ReadControl = 0x20000;
        private const uint WriteDac = 0x40000;
        private const uint ListDirectory = 0x1;
        private const uint ReadAccess = 0x80000000;
        private const uint WriteAccess = 0x40000000;
        private const uint OpenReparsePoint = 0x00200000;
        private const uint BackupSemantics = 0x02000000;
        private const uint Synchronize = 0x00100000;
        private const uint DirectoryFile = 0x1;
        private const uint SynchronousIoNonAlert = 0x20;
        private const uint NonDirectoryFile = 0x40;
        private const uint CaseInsensitive = 0x40;
        private const uint DontReparse = 0x1000;
        private const int FileBasicInfo = 0;
        private const int FileStandardInfo = 1;
        private const int FileRenameInfo = 3;
        private const int FileDispositionInfo = 4;
        private const int FileAttributeTagInfo = 9;
        private const int FileIdInfo = 18;
        private static readonly string State = Path.GetFileName(Config.REPLAYKIT_CONFIG) + "/" + StateLayout.Folder;
        private static readonly string[] Trees = { "basic/profiles", "plugin_manager", "plugin_config/obs-websocket", State };
        private static readonly string[] RootFiles = { "user.ini", "global.ini" };
        private static readonly Regex ArchiveName = new Regex(@"\A(?<stamp>" + StampPattern + @")(?<fraction>-[0-9]{7}(?=-imported))?-(?<reason>" + Reasons + @")\.zip\z");
        private static readonly Regex LegacyName = new Regex(@"\A" + Regex.Escape(Path.GetFileName(Config.OBS_CONFIG)) +
            @"\.bak\.(?<stamp>" + StampPattern + @")(?<fraction>-[0-9]{7})?(?<deleting>\.deleting)?\z");
        private static readonly Regex UninstallName = new Regex(@"\A(?<original>.+)\.replaykit-uninstall(?:-" + StampPattern + @")?\.bak\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"\A[0-9a-fA-F]{64}\z");
        private static readonly Regex DeviceName = new Regex(@"\A(?:CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]) *(?:\.|\z)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> LegacyTop = new HashSet<string>(
            Trees.Append(Scenes).Select(path => path.Split('/')[0]).Concat(RootFiles).Concat(new[] { InstallerPrefs, CachedPrefs }), Paths);

        private static string Folder => Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "backups");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes, out IoStatus status,
            IntPtr size, uint fileAttributes, FileShare share, uint disposition, uint options, IntPtr ea, uint eaLength);

        [DllImport("ntdll.dll")]
        private static extern uint RtlNtStatusToDosError(int status);

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeName
        {
            public ushort Length, Capacity;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr Root, Name;
            public uint Flags;
            public IntPtr Security, QualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatus
        {
            public IntPtr Status, Information;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out AttributeTag info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out StandardInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileIdentity info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref BasicInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte delete, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern IntPtr FindFirstFileW(string path, out FindData data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool FindNextFileW(IntPtr find, out FindData data);

        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr find);

        [DllImport("advapi32.dll")]
        private static extern int GetSecurityInfo(SafeFileHandle file, int objectType, uint requested,
            IntPtr owner, IntPtr group, out IntPtr access, IntPtr audit, out IntPtr descriptor);

        [DllImport("advapi32.dll")]
        private static extern int SetSecurityInfo(SafeFileHandle file, int objectType, uint requested,
            IntPtr owner, IntPtr group, IntPtr access, IntPtr audit);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag
        {
            public FileAttributes Attributes;
            public uint Tag;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StandardInfo
        {
            public long AllocationSize;
            public long EndOfFile;
            public uint Links;
            public byte DeletePending;
            public byte Directory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdentity
        {
            public ulong Volume;
            public Guid Id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo
        {
            public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
            public FileAttributes Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public FileAttributes Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint SizeHigh, SizeLow, Reserved, Reserved2;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string ShortName;
        }

        private sealed class Manifest
        {
            [JsonProperty("format", Required = Required.Always)] public int Format = 1;
            [JsonProperty("created", Required = Required.Always)] public string Created;
            [JsonProperty("reason", Required = Required.Always)] public string Reason;
            [JsonProperty("replaykitVersion", Required = Required.Always)] public string ReplayKitVersion;
            [JsonProperty("obsVersion", Required = Required.AllowNull)] public string ObsVersion;
            [JsonProperty("files", Required = Required.Always)] public List<ArchivedFile> Files = new List<ArchivedFile>();
            [JsonProperty("omitted", Required = Required.Always)] public List<string> Omitted = new List<string>();
        }

        private sealed class ArchivedFile
        {
            [JsonProperty("path", Required = Required.Always)] public string Path;
            [JsonProperty("size", Required = Required.Always)] public long Size;
            [JsonProperty("sha256", Required = Required.Always)] public string Sha256;
        }

        internal sealed class Snapshot : IDisposable
        {
            internal readonly string Path;
            internal readonly string Reason;
            internal readonly FileStream Stream;

            internal Snapshot(string path, string reason, FileStream stream)
            {
                Path = path;
                Reason = reason;
                Stream = stream;
            }

            public void Dispose() => Stream.Dispose();
        }

        internal static Snapshot Create(string reason, Action<string> log)
        {
            try { return Capture(reason, log); }
            catch (Exception ex)
            {
                SetupLog.Line("could not back up OBS settings: " + ex.Message);
                var message = SetupLocale.Pair("setup.backupObsSettingsFailed");
                throw new IOException(message.Shown, ex).WithEnglish(message);
            }
        }

        private static Snapshot Capture(string reason, Action<string> log)
        {
            using var pinned = new PinnedPath(Folder, create: true);
            RemovePartials(imported: false);
            var archives = Archives().ToArray();
            DateTime now = DateTime.UtcNow;
            DateTime stamp = now;
            if (archives.Length > 0 && TryStamp(ArchiveName.Match(Path.GetFileName(archives[0])).Groups["stamp"].Value, out var newest) &&
                newest >= now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)))
                stamp = newest.AddSeconds(1);

            string path = Path.Combine(Folder, stamp.ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + reason + ".zip");
            var saved = Save(path, reason, now, Config.OBS_CONFIG, null, archives.FirstOrDefault(), log);
            try
            {
                if (saved.Path == path) Prune(log);
                return saved;
            }
            catch
            {
                saved.Dispose();
                throw;
            }
        }

        private static Snapshot Save(string path, string reason, DateTime created, string root, string legacy, string compare, Action<string> log)
        {
            var name = ArchiveName.Match(Path.GetFileName(path));
            if (!name.Success || name.Groups["reason"].Value != reason)
                throw new InvalidDataException(SetupLocale.Message("setup.anObsConfigBackupNameIsInvalid"));

            string partial = path + PartialSuffix;
            var manifest = new Manifest
            {
                Created = created.ToString("o", CultureInfo.InvariantCulture),
                Reason = reason,
                ReplayKitVersion = VersionInfo.Version,
                ObsVersion = ObsVersions.InstalledVersion()?.ToString()
            };
            var sources = ConfigFiles(root, legacy, includeOldState: true).OrderBy(file => file.Key, Paths).ToArray();
            if (compare != null)
            {
                CaptureFiles(manifest, sources, null);
                var duplicate = OpenMatchingBackup(manifest, compare);
                if (duplicate != null)
                {
                    try
                    {
                        LogOmissions(manifest, log);
                        log?.Invoke("OBS config already backed up in " + Path.GetFileName(compare));
                        return duplicate;
                    }
                    catch
                    {
                        duplicate.Dispose();
                        throw;
                    }
                }
                manifest.Files.Clear();
                manifest.Omitted.Clear();
            }
            FileStream output = null;
            bool published = false;
            try
            {
                output = OpenStream(partial, FileAccess.ReadWrite, FileShare.None, FileMode.CreateNew, extraAccess: DeleteAccess);
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    CaptureFiles(manifest, sources, zip);
                    LogOmissions(manifest, log);
                    using var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open(), new UTF8Encoding(false));
                    writer.Write(JsonConvert.SerializeObject(manifest));
                }
                output.Flush(true);
                var verified = ReadArchive(output, reason);
                if (!SameFiles(manifest, verified))
                    throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupChangedWhileChecked"));
                Rename(output.SafeFileHandle, path);
                published = true;
                log?.Invoke("verified OBS config backup " + Path.GetFileName(path));
                var snapshot = new Snapshot(path, reason, output);
                output = null;
                return snapshot;
            }
            finally
            {
                if (output != null)
                {
                    if (!published) Discard(output.SafeFileHandle, partial);
                    output.Dispose();
                }
            }
        }

        private static void CaptureFiles(Manifest manifest, IEnumerable<KeyValuePair<string, string>> sources, ZipArchive zip)
        {
            foreach (var source in sources)
            {
                using var input = OpenSource(source.Value);
                if (input.Length > FileLimit)
                {
                    manifest.Omitted.Add(source.Key);
                    continue;
                }
                using var entry = zip?.CreateEntry(source.Key, CompressionLevel.Optimal).Open();
                manifest.Files.Add(HashFile(source.Key, input, entry, FileLimit));
            }
        }

        private static void LogOmissions(Manifest manifest, Action<string> log)
        {
            foreach (string path in manifest.Omitted)
                log?.Invoke("OBS config backup excludes a file over " + FileLimit / (1024 * 1024) + " MB: " + path);
        }

        private static Snapshot OpenMatchingBackup(Manifest manifest, string path)
        {
            FileStream stream = null;
            try
            {
                string reason = ArchiveName.Match(Path.GetFileName(path)).Groups["reason"].Value;
                stream = OpenStream(path, FileAccess.Read, FileShare.Read);
                if (!SameFiles(manifest, ReadArchive(stream, reason))) return null;
                var snapshot = new Snapshot(path, reason, stream);
                stream = null;
                return snapshot;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is JsonException)
            {
                SetupLog.Line("could not reuse OBS config backup " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
            finally { stream?.Dispose(); }
        }

        private static IEnumerable<KeyValuePair<string, string>> ConfigFiles(string root, string legacy, bool includeOldState = false)
        {
            foreach (string name in RootFiles)
                foreach (var file in ExistingFile(name, Path.Combine(root, name)))
                    yield return file;

            foreach (string relative in Trees.Concat(new[] { Scenes }))
            {
                string directory = Path.Combine(root, relative);
                var attributes = PlainPath(directory);
                if (attributes == null) continue;
                if (!IsDirectory(attributes.Value)) throw SetupLocale.Error("setup.anObsConfigPathShouldBeAFolder", m => new IOException(m), "path", relative);
                foreach (var file in FolderFiles(directory, relative))
                    yield return file;
            }

            if (includeOldState)
            {
                // capture older state under its current name so rollback preserves data moved by the installer
                string replayKitRoot = Path.Combine(root, Path.GetFileName(Config.REPLAYKIT_CONFIG));
                foreach (string name in StateLayout.Files())
                {
                    string relative = State + "/" + name;
                    if (!InScope(relative)) continue;
                    var targets = StateLayout.Paths(replayKitRoot, name).ToArray();
                    if (Attributes(targets[0]) != null) continue;
                    foreach (string candidate in targets.Skip(1).Append(Installer.LegacyStreamableStatePath(root, name)))
                    {
                        if (candidate == null) continue;
                        var file = ExistingFile(relative, candidate).FirstOrDefault();
                        if (file.Key == null) continue;
                        yield return file;
                        break;
                    }
                }
            }

            string prefs = legacy == null ? Prefs.PREFS_FILE : Path.Combine(legacy, InstallerPrefs);
            string cached = legacy == null ? Prefs.SETUP_CACHE_PREFS_FILE : Path.Combine(legacy, CachedPrefs);
            if (!Paths.Equals(Path.GetFullPath(prefs), Path.GetFullPath(cached)))
                foreach (var file in ExistingFile("prefs/" + InstallerPrefs, prefs))
                    yield return file;
            foreach (var file in ExistingFile("prefs/" + CachedPrefs, cached))
                yield return file;
        }

        private static IEnumerable<KeyValuePair<string, string>> ExistingFile(string relative, string path)
        {
            var attributes = PlainPath(path);
            if (attributes == null) yield break;
            if (IsDirectory(attributes.Value)) throw SetupLocale.Error("setup.anObsConfigPathShouldBeAFile", m => new IOException(m), "path", relative);
            yield return new KeyValuePair<string, string>(relative, path);
        }

        private static IEnumerable<KeyValuePair<string, string>> FolderFiles(string directory, string relative)
        {
            using var pinned = new PinnedPath(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string name = Path.GetFileName(path);
                string child = relative + "/" + name;
                var attributes = File.GetAttributes(path);
                if (IsDirectory(attributes))
                {
                    if (Paths.Equals(relative, Scenes) || IsCache(name)) continue;
                    RequirePlain(path, attributes);
                    foreach (var file in FolderFiles(path, child))
                        yield return file;
                }
                else if (InScope(child))
                {
                    RequirePlain(path, attributes);
                    yield return new KeyValuePair<string, string>(child, path);
                }
            }
        }

        private static bool IsDirectory(FileAttributes attributes) => (attributes & FileAttributes.Directory) != 0;

        private static bool IsCache(string name) => Paths.Equals(name, "cache") || Paths.Equals(name, "caches");

        private static bool Under(string path, string directory) => path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

        private static bool InScope(string path)
        {
            if (RootFiles.Contains(path, Paths) || Paths.Equals(path, "prefs/" + InstallerPrefs) || Paths.Equals(path, "prefs/" + CachedPrefs))
                return true;
            if (Under(path, Scenes))
                return Paths.Equals(Path.GetExtension(path), ".json") && path.LastIndexOf('/') == Scenes.Length;
            if (!Trees.Any(root => Under(path, root)) || Paths.Equals(path, State + "/" + StateLayout.ShareCoverDefaultSoundFile))
                return false;
            if (!Under(path, State) && Paths.Equals(Path.GetExtension(path), ".bak")) return false;
            return !path.Split('/').Reverse().Skip(1).Any(IsCache);
        }

        private static FileAttributes? Attributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static void RequirePlain(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw SetupLocale.Error("setup.obsConfigBackupsCannotFollowALink", m => new IOException(m), "path", path);
        }

        private static FileAttributes? PlainPath(string path)
        {
            string full = Path.GetFullPath(path);
            var leaf = Attributes(full);
            for (string current = full; current != null; current = Path.GetDirectoryName(current))
            {
                var attributes = Paths.Equals(current, full) ? leaf : Attributes(current);
                if (attributes.HasValue) RequirePlain(current, attributes.Value);
            }
            return leaf;
        }

        private static FileStream OpenSource(string path) => OpenStream(path, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        private static ArchivedFile HashFile(string path, Stream input, Stream output, long limit)
        {
            using var hash = SHA256.Create();
            var buffer = new byte[64 * 1024];
            long length = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                length += read;
                if (length > limit) throw SetupLocale.Error("setup.anObsConfigBackupEntryIsTooLarge", m => new InvalidDataException(m), "path", path);
                hash.TransformBlock(buffer, 0, read, null, 0);
                output?.Write(buffer, 0, read);
            }
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return new ArchivedFile { Path = path, Size = length, Sha256 = NetCompat.ToHex(hash.Hash) };
        }

        private static bool SafeEntry(string path) =>
            !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && path.Split('/').All(part =>
                part.Length > 0 && !part.EndsWith(".", StringComparison.Ordinal) && !part.EndsWith(" ", StringComparison.Ordinal) &&
                part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !DeviceName.IsMatch(part)) && InScope(path);

        private static Manifest ReadArchive(FileStream file, string reason, string staging = null)
        {
            file.Position = 0;
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, true);
            var entries = new Dictionary<string, ZipArchiveEntry>(Paths);
            foreach (var entry in zip.Entries)
            {
                if ((entry.FullName != ManifestName && !SafeEntry(entry.FullName)) || entries.ContainsKey(entry.FullName))
                    throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupHasABadEntry"));
                entries.Add(entry.FullName, entry);
            }
            if (!entries.TryGetValue(ManifestName, out var manifestEntry) || manifestEntry.Length > ManifestLimit)
                throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupListIsMissing"));

            Manifest manifest;
            using (var input = manifestEntry.Open())
            using (var buffer = new MemoryStream())
            {
                HashFile(ManifestName, input, buffer, manifestEntry.Length);
                buffer.Position = 0;
                using var reader = new JsonTextReader(new StreamReader(buffer, new UTF8Encoding(false, true))) { DateParseHandling = DateParseHandling.None };
                manifest = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error })
                    .ToObject<Manifest>();
                if (reader.Read()) throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupListHasExtraData"));
            }

            if (manifest.Format != 1 || manifest.Reason != reason || string.IsNullOrWhiteSpace(manifest.ReplayKitVersion) ||
                !DateTimeOffset.TryParseExact(manifest.Created, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out var created) ||
                created.Offset != TimeSpan.Zero || manifest.Files.Count != entries.Count - 1)
                throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupListIsInvalid"));
            var seen = new HashSet<string>(Paths);
            foreach (string omitted in manifest.Omitted)
                if (!SafeEntry(omitted) || !seen.Add(omitted))
                    throw new InvalidDataException(SetupLocale.Message("setup.theObsConfigBackupListHasABadSkippedFile"));
            foreach (var item in manifest.Files)
            {
                if (item == null || !SafeEntry(item.Path) || !seen.Add(item.Path) || item.Size < 0 || item.Size > FileLimit ||
                    item.Sha256 == null || !HashPattern.IsMatch(item.Sha256) || !entries.TryGetValue(item.Path, out var entry) ||
                    item.Path != entry.FullName || item.Size != entry.Length)
                    throw new InvalidDataException(SetupLocale.Message("setup.anObsConfigBackupEntryDoesNotMatchItsList"));
                using var input = entry.Open();
                using var output = staging == null ? null : StageFile(staging, item.Path);
                if (!SameFile(item, HashFile(item.Path, input, output, item.Size)))
                    throw SetupLocale.Error("setup.anObsConfigBackupFileFailedItsCheck", m => new InvalidDataException(m), "path", item.Path);
                output?.Flush(true);
            }
            return manifest;
        }

        private static FileStream StageFile(string staging, string relative)
        {
            string path = Path.Combine(staging, relative);
            using var pinned = new PinnedPath(Path.GetDirectoryName(path), create: true);
            return OpenStream(path, FileAccess.Write, FileShare.None, FileMode.CreateNew);
        }

        private sealed class Change
        {
            public string Target;
            public ArchivedFile Source;
            public FileStream Incoming;
            public SafeFileHandle Previous;
            public bool MovedPrevious;
            public bool Installed;
        }

        private static string Target(string relative)
        {
            if (Paths.Equals(relative, "prefs/" + InstallerPrefs)) return Prefs.PREFS_FILE;
            if (Paths.Equals(relative, "prefs/" + CachedPrefs)) return Prefs.SETUP_CACHE_PREFS_FILE;
            return Path.Combine(Config.OBS_CONFIG, relative);
        }

        internal static IOException Rollback(Snapshot snapshot, Exception error, Action<string> log, out bool restored)
        {
            log?.Invoke("restoring OBS config after setup failed: " + SetupLocale.EnglishOf(error));
            try { Restore(snapshot, log); }
            catch (Exception rollbackError)
            {
                restored = false;
                log?.Invoke("OBS config rollback failed: " + rollbackError.Message);
                return new IOException(SetupLocale.Message("setup.theUpdateFailedAndCouldNotRollBack",
                    "error", error.Message, "rollbackError", SetupLocale.Message("setup.restoreObsSettingsFailed")), error);
            }
            restored = true;
            return new IOException(SetupLocale.Message("setup.theUpdateFailedAndObsSettingsWereRestored", "error", error.Message), error);
        }

        private static void Restore(Snapshot snapshot, Action<string> log)
        {
            string id = Guid.NewGuid().ToString("N");
            string staging = Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "config-restore-" + id);
            var changes = new List<Change>();
            using var pinnedTargets = new PinnedPath();
            FileIdentity? stagingIdentity = null;
            try
            {
                using var pinnedStaging = new PinnedPath(staging, create: true);
                stagingIdentity = Identity(pinnedStaging.Directory(staging));
                var manifest = ReadArchive(snapshot.Stream, snapshot.Reason, staging);
                var included = new HashSet<string>(manifest.Files.Select(file => file.Path), Paths);
                included.UnionWith(manifest.Omitted);
                foreach (var file in manifest.Files)
                    changes.Add(new Change { Target = Target(file.Path), Source = file });
                foreach (var file in ConfigFiles(Config.OBS_CONFIG, null))
                    if (!included.Contains(file.Key))
                        changes.Add(new Change { Target = file.Value });

                var targets = new HashSet<string>(Paths);
                changes.Sort((left, right) => Paths.Compare(left.Target, right.Target));
                foreach (var change in changes)
                {
                    if (!targets.Add(Path.GetFullPath(change.Target)))
                        throw SetupLocale.Error("setup.twoObsConfigRestoresTargetOnePlace", m => new InvalidDataException(m), "path", change.Target);
                    pinnedTargets.Pin(Path.GetDirectoryName(change.Target), create: change.Source != null);
                    if (change.Source == null) continue;
                    using (var current = OpenStream(change.Target, FileAccess.Read, FileShare.Read, missingOkay: true))
                    {
                        if (current != null && current.Length == change.Source.Size &&
                            SameFile(change.Source, HashFile(change.Source.Path, current, null, change.Source.Size)))
                            continue;
                    }
                    using var input = OpenStream(Path.Combine(staging, change.Source.Path), FileAccess.Read, FileShare.Read);
                    string incoming = change.Target + ".restore-" + id + ".tmp";
                    change.Incoming = OpenStream(incoming, FileAccess.Write, FileShare.None, FileMode.CreateNew,
                        extraAccess: DeleteAccess | WriteDac);
                    if (!SameFile(change.Source, HashFile(change.Source.Path, input, change.Incoming, change.Source.Size)))
                        throw SetupLocale.Error("setup.aPreparedObsConfigFileFailedItsCheck", m => new InvalidDataException(m), "path", change.Source.Path);
                    change.Incoming.Flush(true);
                }

                try
                {
                    foreach (var change in changes)
                    {
                        if (change.Source != null && change.Incoming == null) continue;
                        change.Previous = OpenRegular(change.Target, DeleteAccess | WriteAttributes | ReadControl,
                            FileShare.None, FileMode.Open, missingOkay: true);
                        if (change.Previous != null)
                        {
                            if (change.Incoming != null) CopyPermissions(change.Previous, change.Incoming.SafeFileHandle);
                            Rename(change.Previous, change.Target + ".restore-" + id + ".previous");
                            change.MovedPrevious = true;
                        }
                        if (change.Incoming == null) continue;
                        Rename(change.Incoming.SafeFileHandle, change.Target);
                        change.Installed = true;
                    }
                }
                catch
                {
                    foreach (var change in changes.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (change.Installed)
                            {
                                var incomingHandle = change.Incoming.SafeFileHandle;
                                DeleteFile(incomingHandle, FileAttributesOf(incomingHandle));
                                change.Incoming.Dispose();
                                change.Incoming = null;
                            }
                            if (change.MovedPrevious)
                                Rename(change.Previous, change.Target);
                        }
                        catch (Exception undoError) when (undoError is IOException || undoError is UnauthorizedAccessException)
                        {
                            SetupLog.Line("could not undo OBS config rollback at " + change.Target + ": " + undoError.Message);
                        }
                    }
                    throw;
                }

                foreach (var change in changes.Where(change => change.MovedPrevious))
                    Discard(change.Previous, change.Target + ".restore-" + id + ".previous");
                log?.Invoke("restored OBS config from " + Path.GetFileName(snapshot.Path));
            }
            finally
            {
                foreach (var change in changes)
                {
                    if (change.Incoming != null)
                    {
                        if (!change.Installed) Discard(change.Incoming.SafeFileHandle, change.Target + ".restore-" + id + ".tmp");
                        change.Incoming.Dispose();
                    }
                    change.Previous?.Dispose();
                }
                if (stagingIdentity.HasValue) DeleteStaging(staging, stagingIdentity.Value);
            }
        }

        private static void DeleteStaging(string path, FileIdentity expected)
        {
            try
            {
                string parent = Path.GetDirectoryName(path);
                using var pinned = new PinnedPath(parent);
                using var directory = OpenChild(pinned.Directory(parent), Path.GetFileName(path), DeleteAccess,
                    FileShare.ReadWrite, FileMode.Open, DirectoryFile, missingOkay: true);
                if (directory == null) return;
                RequirePlain(path, FileAttributesOf(directory));
                if (!MatchesIdentity(directory, expected))
                    throw new IOException(SetupLocale.Message("setup.theObsConfigRestoreFolderWasReplaced"));
                long bytes = 0;
                if (DeleteChildren(directory, path, Children(path).Select(Path.GetFileName), SetupLog.Line, ref bytes))
                    MarkForDeletion(directory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetupLog.Line("could not remove OBS config rollback temporary path " + path + ": " + ex.Message);
            }
        }

        private static bool SameFiles(Manifest left, Manifest right) =>
            left.Omitted.OrderBy(path => path, Paths).SequenceEqual(right.Omitted.OrderBy(path => path, Paths)) &&
            left.Files.Count == right.Files.Count && left.Files.OrderBy(item => item.Path, Paths)
                .Zip(right.Files.OrderBy(item => item.Path, Paths), SameFile).All(same => same);

        private static bool SameFile(ArchivedFile left, ArchivedFile right) =>
            left.Path == right.Path && left.Size == right.Size && Paths.Equals(left.Sha256, right.Sha256);

        internal static void StartCleanup()
        {
            try
            {
                if (DetachedSpawn.Start(NetCompat.ProcessPath, ClearFlag, belowNormal: true) == 0)
                    SetupLog.Line("could not start old OBS backup cleanup");
            }
            catch (Exception ex) { SetupLog.Line("could not start old OBS backup cleanup: " + ex.Message); }
        }

        internal static int ClearOld()
        {
            try
            {
                using var pinnedCache = new PinnedPath(Config.REPLAYKIT_SETUP_CACHE, create: true);
                string lockPath = Path.Combine(Config.REPLAYKIT_SETUP_CACHE, "old-backups.lock");
                using var gate = CleanupLock(lockPath);
                if (gate == null) return 0;

                SetupLog.Line("old OBS backup cleanup started");
                string parent = Path.GetDirectoryName(Config.OBS_CONFIG);
                long bytes = 0;
                int folders = 0;
                if (PlainPath(parent) != null)
                {
                    using var pinned = new PinnedPath(parent);
                    var candidates = Directory.EnumerateDirectories(parent)
                        .Select(path => (Path: path, Name: LegacyName.Match(Path.GetFileName(path))))
                        .Where(item => item.Name.Success)
                        .OrderByDescending(item => Path.GetFileName(item.Path), StringComparer.Ordinal)
                        .ToArray();
                    string newest = candidates.FirstOrDefault(item => !item.Name.Groups["deleting"].Success).Path;
                    var imported = ImportLegacy(newest);
                    foreach (var candidate in candidates.Reverse())
                    {
                        bool wasImported = candidate.Path == newest;
                        if (wasImported && !imported.HasValue) continue;
                        if (DeleteLegacy(candidate.Path, candidate.Name.Groups["deleting"].Success,
                            wasImported ? imported : null, ref bytes)) folders++;
                    }
                }
                int uninstallCopies = ClearUninstallCopies(ref bytes);
                SetupLog.Line($"old OBS backup cleanup removed {folders} folder(s) and {uninstallCopies} uninstall copy/copies, freeing {bytes} bytes");
                return 0;
            }
            catch (Exception ex)
            {
                SetupLog.Line("old OBS backup cleanup stopped: " + ex.Message);
                return 1;
            }
        }

        private static FileStream CleanupLock(string path)
        {
            try { return OpenStream(path, FileAccess.ReadWrite, FileShare.None, FileMode.OpenOrCreate); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33)
            {
                SetupLog.Line("old OBS backup cleanup is already running");
                return null;
            }
        }

        private static FileIdentity? ImportLegacy(string path)
        {
            try
            {
                using var pinned = new PinnedPath(Folder, create: true);
                RemovePartials(imported: true);
                if (path == null) return null;
                using var source = new PinnedPath(path);
                if (!CheckLegacy(path, Children(path))) return null;
                var identity = Identity(source.Directory(path));
                var name = LegacyName.Match(Path.GetFileName(path));
                string stamp = name.Groups["stamp"].Value;
                if (!DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var created))
                    throw new InvalidDataException("Invalid legacy OBS backup timestamp.");
                // the old folders were stamped in local time while every archive name is utc, so the import takes its utc time to keep the newest-first order that prune and the duplicate check read
                DateTime createdUtc = created.ToUniversalTime();
                string archive = Path.Combine(Folder, createdUtc.ToString(StampFormat, CultureInfo.InvariantCulture) +
                    name.Groups["fraction"].Value + "-" + ImportReason + ".zip");
                string compare = Attributes(archive) == null ? null : archive;
                using (Save(archive, ImportReason, createdUtc, path, path, compare, SetupLog.Line)) { }
                SetupLog.Line("imported legacy OBS backup " + Path.GetFileName(path));
                Prune(SetupLog.Line);
                return identity;
            }
            catch (Exception ex)
            {
                SetupLog.Line("could not import legacy OBS backup " + Path.GetFileName(path) + ": " + ex.Message);
                return null;
            }
        }

        private static bool CheckLegacy(string path, IEnumerable<string> children)
        {
            foreach (string entry in children)
            {
                if (LegacyTop.Contains(Path.GetFileName(entry))) continue;
                SetupLog.Line("left legacy OBS backup with an unexpected top-level entry: " + Path.GetFileName(path) + "/" + Path.GetFileName(entry));
                return false;
            }
            return true;
        }

        private static bool DeleteLegacy(string path, bool deleting, FileIdentity? expected, ref long bytes)
        {
            try
            {
                string parent = Path.GetDirectoryName(path);
                using var pinned = new PinnedPath(parent);
                using var directory = OpenChild(pinned.Directory(parent), Path.GetFileName(path), DeleteAccess,
                    FileShare.ReadWrite, FileMode.Open, DirectoryFile, missingOkay: true);
                if (directory == null) return false;
                RequirePlain(path, FileAttributesOf(directory));
                if (expected.HasValue && !MatchesIdentity(directory, expected.Value))
                    throw new IOException("Legacy OBS backup changed after its import.");
                var children = Children(path);
                if (!CheckLegacy(path, children)) return false;
                string target = deleting ? path : path + DeletingSuffix;
                if (!deleting) Rename(directory, target);
                long before = bytes;
                bool done = DeleteChildren(directory, target, children.Select(Path.GetFileName), SetupLog.Line, ref bytes);
                if (done) MarkForDeletion(directory);
                SetupLog.Line((done ? "removed " : "partially removed ") + Path.GetFileName(target) + " (" + (bytes - before) + " bytes)");
                return done;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SetupLog.Line("could not remove legacy OBS backup " + Path.GetFileName(path) + ": " + ex.Message);
                return false;
            }
        }

        private static SafeFileHandle PinDirectory(string path, out bool link)
        {
            // deny rename or replacement while a directory is being walked
            var handle = CreateFileW(NativePath(path), ReadAttributes | ListDirectory, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
                OpenReparsePoint | BackupSemantics, IntPtr.Zero);
            if (!handle.IsInvalid && GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out AttributeTag info,
                (uint)Marshal.SizeOf<AttributeTag>()) && IsDirectory(info.Attributes))
            {
                link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
                return handle;
            }
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw SetupLocale.Error("setup.anObsConfigBackupFolderCouldNotBeOpened", m => new IOException(m, new Win32Exception(error)), "path", path);
        }

        private static IOException FileError(int error)
        {
            var cause = new Win32Exception(error);
            return new IOException(cause.Message, unchecked((int)(0x80070000u | (uint)error)));
        }

        // resolve one name from the directory handle, without reopening its pathname
        private static SafeFileHandle OpenChild(SafeFileHandle parent, string name, uint access, FileShare share,
            FileMode mode, uint type, bool missingOkay = false, bool allowLink = false)
        {
            if (name.Length == 0 || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException(SetupLocale.Message("setup.anObsConfigFileNameIsInvalid"));
            uint disposition = mode == FileMode.Open ? 1u : mode == FileMode.CreateNew ? 2u : 3u;
            IntPtr text = Marshal.StringToHGlobalUni(name);
            IntPtr unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
            try
            {
                var description = new UnicodeName
                {
                    Length = checked((ushort)(name.Length * sizeof(char))),
                    Capacity = checked((ushort)((name.Length + 1) * sizeof(char))),
                    Buffer = text
                };
                Marshal.StructureToPtr(description, unicode, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    Root = parent.DangerousGetHandle(),
                    Name = unicode,
                    Flags = CaseInsensitive | (allowLink ? 0 : DontReparse)
                };
                int status = NtCreateFile(out var handle, access | ReadAttributes | Synchronize, ref attributes, out _,
                    IntPtr.Zero, 0, share, disposition, type | OpenReparsePoint | SynchronousIoNonAlert, IntPtr.Zero, 0);
                GC.KeepAlive(parent);
                if (status >= 0 && !handle.IsInvalid) return handle;
                handle?.Dispose();
                int error = (int)RtlNtStatusToDosError(status);
                if (missingOkay && (error == 2 || error == 3)) return null;
                throw FileError(error);
            }
            finally
            {
                Marshal.FreeHGlobal(unicode);
                Marshal.FreeHGlobal(text);
            }
        }

        private static SafeFileHandle OpenRegular(string path, uint access, FileShare share, FileMode mode, bool missingOkay)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            using var pinned = new PinnedPath(parent);
            var handle = OpenChild(pinned.Directory(parent), Path.GetFileName(path), access, share, mode, NonDirectoryFile, missingOkay);
            if (handle == null) return null;
            try
            {
                RequirePlain(path, FileAttributesOf(handle));
                if (!GetFileInformationByHandleEx(handle, FileStandardInfo, out StandardInfo standard, (uint)Marshal.SizeOf<StandardInfo>()))
                    throw FileError(Marshal.GetLastWin32Error());
                if (standard.Links != 1) throw SetupLocale.Error("setup.obsConfigBackupsCannotUseAHardLink", m => new IOException(m), "path", path);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static FileStream OpenStream(string path, FileAccess access, FileShare share,
            FileMode mode = FileMode.Open, uint extraAccess = 0, bool missingOkay = false)
        {
            string full = Path.GetFullPath(path);
            uint rights = extraAccess;
            if ((access & FileAccess.Read) != 0) rights |= ReadAccess;
            if ((access & FileAccess.Write) != 0) rights |= WriteAccess;
            var handle = OpenRegular(full, rights, share, mode, missingOkay);
            if (handle == null) return null;
            try { return new FileStream(handle, access); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void Rename(SafeFileHandle file, string path)
        {
            byte[] name = Encoding.Unicode.GetBytes(NativePath(path) + "\0");
            int lengthOffset = 2 * IntPtr.Size;
            int nameOffset = lengthOffset + sizeof(int);
            int size = nameOffset + name.Length;
            IntPtr info = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteIntPtr(info, 0, IntPtr.Zero);
                Marshal.WriteIntPtr(info, IntPtr.Size, IntPtr.Zero);
                Marshal.WriteInt32(info, lengthOffset, name.Length - sizeof(char));
                Marshal.Copy(name, 0, IntPtr.Add(info, nameOffset), name.Length);
                if (!SetFileInformationByHandle(file, FileRenameInfo, info, (uint)size))
                    throw FileError(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        private static void MarkForDeletion(SafeFileHandle file)
        {
            byte delete = 1;
            if (!SetFileInformationByHandle(file, FileDispositionInfo, ref delete, 1))
                throw FileError(Marshal.GetLastWin32Error());
        }

        private static FileAttributes FileAttributesOf(SafeFileHandle file)
        {
            if (!GetFileInformationByHandleEx(file, FileAttributeTagInfo, out AttributeTag info, (uint)Marshal.SizeOf<AttributeTag>()))
                throw FileError(Marshal.GetLastWin32Error());
            return info.Attributes;
        }

        private static FileIdentity Identity(SafeFileHandle file)
        {
            if (!GetFileInformationByHandleEx(file, FileIdInfo, out FileIdentity info, (uint)Marshal.SizeOf<FileIdentity>()))
                throw FileError(Marshal.GetLastWin32Error());
            return info;
        }

        private static bool MatchesIdentity(SafeFileHandle file, FileIdentity expected)
        {
            var actual = Identity(file);
            return actual.Volume == expected.Volume && actual.Id == expected.Id;
        }

        private static void CopyPermissions(SafeFileHandle source, SafeFileHandle destination)
        {
            const int FileObject = 1;
            const uint AccessList = 4;
            const ushort AccessProtected = 0x1000;
            const uint ProtectAccess = 0x80000000;
            const uint InheritAccess = 0x20000000;
            int error = GetSecurityInfo(source, FileObject, AccessList, IntPtr.Zero, IntPtr.Zero, out var access, IntPtr.Zero, out var descriptor);
            if (error != 0) throw FileError(error);
            try
            {
                if (!GetSecurityDescriptorControl(descriptor, out ushort control, out _))
                    throw FileError(Marshal.GetLastWin32Error());
                uint inheritance = (control & AccessProtected) != 0 ? ProtectAccess : InheritAccess;
                error = SetSecurityInfo(destination, FileObject, AccessList | inheritance,
                    IntPtr.Zero, IntPtr.Zero, access, IntPtr.Zero);
                if (error != 0) throw FileError(error);
            }
            finally { LocalFree(descriptor); }
        }

        private static void Discard(SafeFileHandle file, string path)
        {
            try { MarkForDeletion(file); }
            catch (IOException ex) { SetupLog.Line("could not remove temporary OBS config file " + path + ": " + ex.Message); }
        }

        private static string NativePath(string path)
        {
            string full = (NetCompat.IsPathFullyQualified(path) ? path : Path.GetFullPath(path)).Replace('/', '\\');
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full.Substring(2);
            return @"\\?\" + full;
        }

        private static List<string> Children(string path)
        {
            var children = new List<string>();
            IntPtr search = FindFirstFileW(NativePath(path) + @"\*", out var data);
            if (search == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2) return children;
                throw new IOException(new Win32Exception(error).Message);
            }
            try
            {
                do
                {
                    if (data.Name != "." && data.Name != "..") children.Add(path + "\\" + data.Name);
                }
                while (FindNextFileW(search, out data));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new IOException(new Win32Exception(error).Message);
                return children;
            }
            finally { FindClose(search); }
        }

        private sealed class PinnedPath : IDisposable
        {
            private readonly Dictionary<string, SafeFileHandle> directories = new Dictionary<string, SafeFileHandle>(Paths);

            public PinnedPath() { }

            public PinnedPath(string path, bool create = false)
            {
                try { Pin(path, create); }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Pin(string path, bool create)
            {
                var ancestors = new Stack<string>();
                for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                    ancestors.Push(current);
                foreach (string ancestor in ancestors)
                {
                    if (directories.ContainsKey(ancestor)) continue;
                    string parent = Path.GetDirectoryName(ancestor);
                    SafeFileHandle directory;
                    if (parent == null)
                    {
                        directory = PinDirectory(ancestor, out bool link);
                        directories.Add(ancestor, directory);
                        if (link) throw SetupLocale.Error("setup.obsConfigBackupsCannotFollowALink", m => new IOException(m), "path", ancestor);
                        continue;
                    }
                    directory = OpenChild(directories[parent], Path.GetFileName(ancestor), ListDirectory,
                        FileShare.ReadWrite, create ? FileMode.OpenOrCreate : FileMode.Open, DirectoryFile);
                    directories.Add(ancestor, directory);
                    if (!GetFileInformationByHandleEx(directory, FileAttributeTagInfo, out AttributeTag tag, (uint)Marshal.SizeOf<AttributeTag>()))
                        throw FileError(Marshal.GetLastWin32Error());
                    RequirePlain(ancestor, tag.Attributes);
                }
            }

            public SafeFileHandle Directory(string path) => directories[Path.GetFullPath(path)];

            public void Dispose()
            {
                foreach (var directory in directories.Values.Reverse()) directory.Dispose();
            }
        }

        private static bool DeleteTree(SafeFileHandle parent, string path, Action<string> log, ref long bytes)
        {
            try
            {
                using var node = OpenChild(parent, Path.GetFileName(path), DeleteAccess | WriteAttributes, FileShare.ReadWrite,
                    FileMode.Open, 0, missingOkay: true, allowLink: true);
                if (node == null) return true;
                var attributes = FileAttributesOf(node);
                if (IsDirectory(attributes))
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                        !DeleteChildren(node, path, Children(path).Select(Path.GetFileName), log, ref bytes))
                        return false;
                    MarkForDeletion(node);
                }
                else
                {
                    bytes += DeleteFile(node, attributes);
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log?.Invoke("could not delete " + path + ": " + ex.Message);
                return false;
            }
        }

        private static bool DeleteChildren(SafeFileHandle directory, string path, IEnumerable<string> children,
            Action<string> log, ref long bytes)
        {
            bool complete = true;
            foreach (string name in children)
                if (!DeleteTree(directory, path + "\\" + name, log, ref bytes)) complete = false;
            return complete;
        }

        private static long DeleteFile(SafeFileHandle file, FileAttributes attributes)
        {
            if (IsDirectory(attributes)) throw new IOException(SetupLocale.Message("setup.anObsConfigBackupFileIsAFolder"));
            long size = 0;
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                if (!GetFileInformationByHandleEx(file, FileStandardInfo, out StandardInfo standard, (uint)Marshal.SizeOf<StandardInfo>()))
                    throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                size = standard.Links == 1 ? standard.EndOfFile : 0;
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    if (standard.Links > 1) throw new IOException(SetupLocale.Message("setup.aReadOnlyHardLinkWasNotRemoved"));
                    var basic = new BasicInfo { Attributes = attributes & ~FileAttributes.ReadOnly };
                    if (basic.Attributes == 0) basic.Attributes = FileAttributes.Normal;
                    if (!SetFileInformationByHandle(file, FileBasicInfo, ref basic, (uint)Marshal.SizeOf<BasicInfo>()))
                        throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
            MarkForDeletion(file);
            return size;
        }

        private static int ClearUninstallCopies(ref long bytes)
        {
            int removed = 0;
            foreach (string directory in new[] { Config.OBS_CONFIG, Path.Combine(Config.OBS_CONFIG, Scenes) })
            {
                try
                {
                    if (PlainPath(directory) == null) continue;
                    using var pinned = new PinnedPath(directory);
                    foreach (string path in Directory.GetFiles(directory, "*.bak"))
                    {
                        var match = UninstallName.Match(Path.GetFileName(path));
                        if (!match.Success) continue;
                        string original = match.Groups["original"].Value;
                        bool expected = Paths.Equals(directory, Config.OBS_CONFIG) ? RootFiles.Contains(original, Paths) :
                            Paths.Equals(Path.GetExtension(original), ".json");
                        if (!expected) continue;
                        try
                        {
                            using var file = OpenChild(pinned.Directory(directory), Path.GetFileName(path),
                                DeleteAccess | WriteAttributes, FileShare.ReadWrite, FileMode.Open, NonDirectoryFile,
                                missingOkay: true, allowLink: true);
                            if (file == null) continue;
                            bytes += DeleteFile(file, FileAttributesOf(file));
                            removed++;
                            SetupLog.Line("removed legacy uninstall copy " + Path.GetFileName(path));
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            SetupLog.Line("could not delete legacy uninstall copy " + Path.GetFileName(path) + ": " + ex.Message);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    SetupLog.Line("could not scan for OBS uninstall copies: " + ex.Message);
                }
            }
            return removed;
        }

        private static bool TryStamp(string stamp, out DateTime time) =>
            DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

        private static IEnumerable<string> Archives() => Directory.EnumerateFiles(Folder)
            .Where(path =>
            {
                var match = ArchiveName.Match(Path.GetFileName(path));
                return match.Success && TryStamp(match.Groups["stamp"].Value, out _);
            })
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal);

        private static void RemovePartials(bool imported)
        {
            foreach (string path in Directory.EnumerateFiles(Folder, "*" + PartialSuffix))
            {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(PartialSuffix, StringComparison.Ordinal)) continue;
                var match = ArchiveName.Match(name.Substring(0, name.Length - PartialSuffix.Length));
                if (match.Success && (match.Groups["reason"].Value == ImportReason) == imported &&
                    TryStamp(match.Groups["stamp"].Value, out _))
                {
                    using var file = OpenRegular(path, DeleteAccess, FileShare.None, FileMode.Open, missingOkay: true);
                    if (file != null) MarkForDeletion(file);
                }
            }
        }

        private static void Prune(Action<string> log)
        {
            foreach (string path in Archives().Skip(Retained))
            {
                try
                {
                    using var file = OpenRegular(path, DeleteAccess, FileShare.None, FileMode.Open, missingOkay: true);
                    if (file != null) MarkForDeletion(file);
                    log?.Invoke("pruned OBS config backup " + Path.GetFileName(path));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    SetupLog.Line("could not prune OBS config backup " + Path.GetFileName(path) + ": " + ex.Message);
                }
            }
        }
    }
}
