using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReplayKitHelper
{
    internal static class ClipRenames
    {
        private const string FilesKey = "@files";
        private const uint ReadAttributes = 0x80;
        private const uint OpenReparsePoint = 0x00200000;
        private const int FileIdInfo = 18;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileId
        {
            public ulong Volume, Low, High;
        }
        private static readonly uint FileIdSize = (uint)Marshal.SizeOf<FileId>();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out FileId id, uint size);

        private sealed class Observed
        {
            public long Size, Written, Created;
            public string Id;
        }

        // guarded by ClipsMetaLock; a clip whose file attributes stayed put needs no new handle
        private static Dictionary<string, Observed> observed = new Dictionary<string, Observed>(StringComparer.Ordinal);
        private static string savedRoot, savedDb;
        private static JObject savedFiles;

        internal static string Identity(FileInfo file)
        {
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var handle = CreateFileW(file.FullName, ReadAttributes, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, FileMode.Open, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, FileIdInfo, out FileId id, FileIdSize) ||
                (id.Low == 0 && id.High == 0)) return null;
            return string.Format(CultureInfo.InvariantCulture, "{0:x16}:{1:x16}{2:x16}:{3:x16}",
                id.Volume, id.High, id.Low, file.CreationTimeUtc.Ticks);
        }

        private static string Resolve(string root, string relative)
        {
            string[] parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Length > 2 || Clips.GetSafeFilename(parts.Last()) != parts.Last() ||
                (parts.Length == 2 && !Clips.IsGameFolderName(parts[0]))) return null;
            return Path.Combine(root, relative);
        }

        private static string Prefix(string root) => root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root : root + Path.DirectorySeparatorChar;

        private static JObject Scan(string root, IEnumerable<FileInfo> files)
        {
            string prefix = Prefix(root);
            var current = new JObject();
            var next = new Dictionary<string, Observed>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                try
                {
                    if (!Clips.IsLibraryClip(file) || !file.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                        !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    string relative = file.FullName.Substring(prefix.Length);
                    if (Resolve(root, relative) == null) continue;
                    long size = file.Length, written = file.LastWriteTimeUtc.Ticks, created = file.CreationTimeUtc.Ticks;
                    if (!observed.TryGetValue(file.FullName, out var item) ||
                        item.Size != size || item.Written != written || item.Created != created)
                    {
                        string id = Identity(file);
                        if (id == null) continue;
                        item = new Observed { Size = size, Written = written, Created = created, Id = id };
                    }
                    current[relative] = item.Id;
                    next[file.FullName] = item;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            observed = next;
            return current;
        }

        private static Dictionary<string, string> UniqueIds(JObject files) => files.Properties()
            .Where(property => property.Value.Type == JTokenType.String)
            .GroupBy(property => property.Value.Value<string>(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);

        internal static bool Reconcile(string root, List<FileInfo> files)
        {
            lock (Server.State.ClipsMetaLock)
            {
                try
                {
                    root = Path.GetFullPath(root);
                    string dbPath = AppConfig.GetDbPath();
                    var filesOnDisk = Scan(root, files);
                    if (root == savedRoot && dbPath == savedDb && File.Exists(dbPath) && JToken.DeepEquals(savedFiles, filesOnDisk))
                        return true;
                    var entries = ClipStore.ReadFolder(dbPath, root, db => db);
                    var previous = entries[FilesKey] as JObject ?? new JObject();
                    var newIds = UniqueIds(filesOnDisk);
                    var names = new HashSet<string>(files.Where(Clips.IsLibraryClip).Select(file => file.Name), StringComparer.OrdinalIgnoreCase);
                    var moved = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var pair in UniqueIds(previous))
                    {
                        if (!newIds.TryGetValue(pair.Key, out string relative) || relative == pair.Value) continue;
                        string from = Resolve(root, pair.Value), to = Resolve(root, relative);
                        if (from == null || to == null ||
                            !string.Equals(Path.GetExtension(from), Path.GetExtension(to), StringComparison.OrdinalIgnoreCase)) continue;
                        string oldName = Path.GetFileName(from), newName = Path.GetFileName(to);
                        if ((!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && File.Exists(from)) ||
                            (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && names.Contains(oldName))) continue;
                        if (Identity(new FileInfo(to)) != pair.Key) continue;
                        if (!MoveMetadata(root, from, to, pair.Key)) return false;
                        moved.Add(pair.Value);
                    }

                    // keep the identity of a missing clip with metadata so it can be found if its folder is adopted later
                    var current = (JObject)filesOnDisk.DeepClone();
                    foreach (var old in previous.Properties())
                    {
                        if (moved.Contains(old.Name) || current[old.Name] != null || Resolve(root, old.Name) == null) continue;
                        if (entries[Path.GetFileName(old.Name)] is JObject) current[old.Name] = old.Value.DeepClone();
                    }
                    if (!JToken.DeepEquals(previous, current))
                        Clips.UpdateDb(db => { db[FilesKey] = current; return true; }, root);
                    savedRoot = root;
                    savedDb = dbPath;
                    savedFiles = filesOnDisk;
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
                {
                    Log.Write("Clip rename recovery: " + ex.Message);
                    return false;
                }
            }
        }

        internal static bool MoveMetadata(string root, string from, string to, string id)
        {
            string oldName = Path.GetFileName(from), newName = Path.GetFileName(to);
            string prefix = Prefix(root);
            lock (Server.State.ClipsMetaLock)
            {
                try
                {
                    if (!EditDrafts.Rename(from, to)) return false;
                    if (oldName != newName) Clips.RenameFavorite(root, oldName, newName);
                    Clips.UpdateDb(db =>
                    {
                        bool changed = false;
                        if (oldName != newName && db[oldName] is JObject source)
                        {
                            db.Remove(oldName);
                            db[newName] = source;
                            changed = true;
                        }
                        // record the new path in the same write so another rename can follow it immediately
                        if (id == null || !from.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                            !to.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return changed;
                        var files = db[FilesKey] as JObject ?? new JObject();
                        string before = from.Substring(prefix.Length), after = to.Substring(prefix.Length);
                        if (files[before]?.Value<string>() == id) changed |= files.Remove(before);
                        if (files[after]?.Value<string>() == id) return changed;
                        files[after] = id;
                        db[FilesKey] = files;
                        return true;
                    }, root);
                    savedFiles = null;
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    Log.Write("Clip rename metadata: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
