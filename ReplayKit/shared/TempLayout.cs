using System;
using System.IO;

namespace ReplayKitShared
{
    // what replaykit can lose at any time lives in its own folder under %TEMP%: logs, scratch files, caches. only the names two programs have to agree on are here (the installer, the helper and the plugin each read or write them); a folder only one of them uses is named by that one
    public static class TempLayout
    {
        // plain literals, since scripts/sync_shared.ps1 reads them to give the plugin the same folders
        public const string Folder = "ReplayKit";
        public const string LogsFolder = "logs";
        public const string ScratchFolder = "scratch";
        // inside the scratch folder: the pieces of the clip buffer on disk, written by the plugin and joined by the helper
        public const string ClipBufferFolder = "clip-buffer";

        // inside the logs folder: the installer writes how an update ended and its own pid, and the helper reads both
        public const string UpdateResultFile = "update_result.json";
        public const string UpdatePidFile = "update_pid";
        public const string UpdateAttemptFile = "update_attempt";

        public static bool ValidAttempt(string id) => Guid.TryParseExact(id, "N", out _);

        public static string UpdateFile(string filename, string attemptId)
        {
            if (string.IsNullOrEmpty(attemptId)) return filename;
            if (!ValidAttempt(attemptId)) throw new ArgumentException("Invalid update attempt ID.", nameof(attemptId));
            return attemptId + "-" + filename;
        }
        // inside the logs folder: the share window writes its log there and the helper reads it for the sdk
        public const string ShareWindowLogFile = "share-window.log";

        // the helper downloads an update into a folder of this prefix and a guid, and the installer deletes only a folder of that shape
        public const string UpdateFolderPrefix = "update-";

        public static string UpdateDirectory(string attemptId)
        {
            if (!ValidAttempt(attemptId)) throw new ArgumentException("Invalid update attempt ID.", nameof(attemptId));
            return Path.Combine(Root, UpdateFolderPrefix + attemptId);
        }

        public static string UpdateProgressPath(string attemptId) => Path.Combine(UpdateDirectory(attemptId), "progress.json");

        public static string Root => Path.Combine(Path.GetTempPath(), Folder);
        public static string Logs => Path.Combine(Root, LogsFolder);
        public static string Scratch => Path.Combine(Root, ScratchFolder);
    }
}
