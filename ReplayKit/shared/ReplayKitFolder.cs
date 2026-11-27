namespace ReplayKitShared
{
    // the folder of replaykit inside the obs config folder (%APPDATA%\obs-studio\obs-replayKit), where everything it installs and keeps lives, and the helper exe in it; every release installs there and the release bundle keeps it under obs-studio, so neither ever changes. scripts/sync_shared.ps1 writes them out for the plugin, the process host and the setup csproj
    public static class ReplayKitFolder
    {
        public const string Name = "obs-replayKit";
        public const string HelperExe = @"scripts\helper\OBSReplayKit.exe";
        // the folder under %LOCALAPPDATA% with the cached setup exe the in-app uninstall starts, the streamable sign-in profile and the speech engine of auto subtitles, which an uninstall clears
        public const string LocalName = "OBS ReplayKit";
        public const string SetupExe = "OBSReplayKitSetup.exe";
        public const string StreamableProfileFolder = "StreamableProfile";
        public const string SpeechFolder = "speech";
    }
}
