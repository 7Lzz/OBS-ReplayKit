namespace ReplayKitShared
{
    // the windows mixer shows audio sessions that share a grouping id as one slider; obs (the plugin) and its page host (the browser and helper roles) put their sessions under this one, and scripts/sync_shared.ps1 writes it into obs_audio_group.hpp for both
    public static class AudioGroup
    {
        public const string ObsSessionGroup = "3c7e91a4-0b2d-4f6e-8a15-7d9c2e4b6f02";
    }
}
