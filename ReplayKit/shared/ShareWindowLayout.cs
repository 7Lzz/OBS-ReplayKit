namespace ReplayKitShared
{
    // the names the share window, the plugin, the helper and the installer have to agree on; plain literals, since scripts/sync_shared.ps1 writes them into shared_values.hpp for the plugin and the window
    public static class ShareWindowLayout
    {
        // the program that owns the window discord streams for share preview and outlives obs
        public const string ExeName = "ReplayKitShare.exe";
        // the folder under obs-replayKit it installs to
        public const string Folder = "share-window";
        // the kernel object names, all in the session local namespace
        public const string PipeName = "ReplayKitShareWindow";
        public const string StateMappingName = "ReplayKitShareWindow.State";
        public const string FrameEventName = "ReplayKitShareWindow.Frame";
        public const string AudioEventName = "ReplayKitShareWindow.Audio";
        public const string InstanceMutexName = "ReplayKitShareWindow.Instance";
        public const string WindowClass = "ReplayKitShareWindow";
        // bumps whenever the shared state, the pipe commands or what they mean change, so an older window still running after an update is replaced instead of misread
        public const int ProtocolVersion = 2;
    }
}
