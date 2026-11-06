using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;

namespace ReplayKitShared
{
    internal static class VideoTools
    {
        // Pin the archive so later changes to the repository layout cannot break released installers.
        public const string ArchiveUrl = "https://raw.githubusercontent.com/7Lzz/OBS-ReplayKit/dad5c037106ce8b6429e9b8a4da9ed31f323ac07/ReplayKit/downloads/ffmpeg-tools.zip";
        public const string FfmpegSha256 = "3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec";
        public const string FfprobeSha256 = "f0d36ecbbdd3bcfac3efa078c96c7271c2e68b3810595552ac3b7f17e9a65c52";

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
