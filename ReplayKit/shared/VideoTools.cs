using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;

namespace ReplayKitShared
{
    internal static class VideoTools
    {
        // Pin the archive so later changes to the repository layout cannot break released installers.
        public const string ArchiveUrl = "https://raw.githubusercontent.com/7Lzz/OBS-ReplayKit/cba0a30cb3112f649dbb51dcef987ca3189654f5/ReplayKit/downloads/ffmpeg-tools.zip";
        public const string FfmpegSha256 = "ed8f66cb877087a96f5bbb40a2944f0e764f2ad8870d5cb647360d912e62bc07";
        public const string FfprobeSha256 = "df0199f606faa88e9ad192732a7c3987acfb647663534c297772e17730611a08";

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
