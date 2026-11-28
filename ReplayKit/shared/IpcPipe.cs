namespace ReplayKitShared
{
    // the named pipe between the native plugin, which serves it, and the helper: scripts/sync_shared.ps1 writes both values into shared_values.hpp for the plugin
    public static class IpcPipe
    {
        public const string Name = "OBSReplayKitIpc";
        // the longest line either side takes, in utf8 bytes without the newline; the plugin drops the connection on a line that runs past it
        public const int MaxLineBytes = 61440;
    }
}
