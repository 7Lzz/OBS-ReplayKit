namespace ReplayKitShared
{
    // the windows replaykit opens beside obs: the titles the helper finds them by and the smallest size each may be resized to, in logical pixels; scripts/sync_shared.ps1 writes them into shared_values.hpp for the plugin, which makes the windows
    internal static class WindowLayout
    {
        internal const string SettingsTitle = "ReplayKit Settings";
        internal const int SettingsMinWidth = 700;
        internal const int SettingsMinHeight = 500;
        internal const string ClipsTitle = "Clips";
        internal const int ClipsMinWidth = 850;
        internal const int ClipsMinHeight = 620;
        internal const string SetupTitle = "ReplayKit Setup";
        internal const int SetupMinWidth = 640;
        internal const int SetupMinHeight = 520;
        internal const string UpdateTitle = "ReplayKit Update";
        internal const int UpdateMinWidth = 480;
        internal const int UpdateMinHeight = 400;
    }
}
