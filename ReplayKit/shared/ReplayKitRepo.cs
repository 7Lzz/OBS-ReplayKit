namespace ReplayKitShared
{
    // the github repository replaykit is released from: the helper checks its releases, the setup names it in its downloads and points to its release page, and scripts/sync_shared.ps1 writes the page into shared_values.hpp for the link in the obs settings column of the plugin
    public static class ReplayKitRepo
    {
        public const string Owner = "7Lzz";
        public const string Name = "OBS-ReplayKit";
        public const string Page = "https://github.com/" + Owner + "/" + Name;
        public const string LatestReleasePage = Page + "/releases/latest";
    }
}
