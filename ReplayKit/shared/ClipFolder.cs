using System;
using System.IO;
using System.Linq;

namespace ReplayKitShared
{
    // the default clip folder: the videos known folder of windows, which obs also defaults its recording path to and which follows the folder when it is moved or backed up to onedrive
    public static class ClipFolder
    {
        private static readonly string[] ClipExtensions = { ".mp4", ".mkv", ".mov" };

        public static string Default()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
            return string.IsNullOrEmpty(videos) ? Legacy() : videos;
        }

        // the default before it followed the known folder
        private static string Legacy() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");

        // the old default when it is not the known folder and still holds clips, so an install whose clips were saved there keeps that folder; null otherwise
        public static string LegacyWithClips()
        {
            try
            {
                string legacy = Path.GetFullPath(Legacy()).TrimEnd('\\');
                if (string.Equals(legacy, Path.GetFullPath(Default()).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(legacy)) return null;
                bool clips = Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories)
                    .Any(file => ClipExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
                return clips ? legacy : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
