using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;

namespace ReplayKitShared
{
    internal static class VideoTools
    {
        // Pin the archive so later changes to the repository layout cannot break released installers.
        public const string ArchiveUrl = "https://raw.githubusercontent.com/7Lzz/OBS-ReplayKit/db28854474acd5eee4195c8fbb2e93e72d485403/ReplayKit/utils/downloads/ffmpeg-tools.zip";
        public const string FfmpegSha256 = "228d7a8556258de907fdb55f36850078ebc7680b84ec30d84ea02e99bec1d1eb";
        public const string FfprobeSha256 = "0fde260f5abd35c9cafd96f594cc76365a780c1b73a90e35b6a3409ea1db1bf0";

        public static bool IsInstalled(string directory) =>
            IsValid(Path.Combine(directory, "ffmpeg.exe"), FfmpegSha256) &&
            IsValid(Path.Combine(directory, "ffprobe.exe"), FfprobeSha256);

        public static bool IsValid(string path, string expectedSha256)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var sha = SHA256.Create())
                using (var file = File.OpenRead(path))
                    return string.Equals(BitConverter.ToString(sha.ComputeHash(file)).Replace("-", ""), expectedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                return false;
            }
        }
    }
}
