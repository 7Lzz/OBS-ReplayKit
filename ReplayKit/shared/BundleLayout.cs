namespace ReplayKitShared
{
    // the plugin zips of the release bundle and the folder each keeps its files under; the setup csproj zips the loose repo folders by these (scripts/sync_shared.ps1 writes them into Bundle.props for it) and the installer opens the zips by the same names, which every earlier release reads too, so they never change
    public static class BundleLayout
    {
        public const string BangoCatZip = "Bango Cat.zip";
        // BongoCat.cs strips the root, and what follows it is where the files go under the obs folder
        public const string BangoCatZipRoot = "Bango Cat/";
        public const string BangoCatPluginDir = "bin/64bit/Bango Cat/";
        public const string PresetsZip = "input-overlay-presets.zip";
        public const string PresetsZipRoot = "input-overlay-presets/";
        public const string InputOverlayZip = "input-overlay-plugin.zip";
        public const string InputOverlayZipRoot = "input-overlay/";
        // win-capture-audio.zip mirrors the obs folder, so WinCapture.cs extracts it as it is
        public const string WinCaptureAudioZip = "win-capture-audio.zip";
        public const string WinCaptureAudioDllDir = "obs-plugins/64bit/";
        public const string WinCaptureAudioDll = "win-capture-audio.dll";
        public const string WinCaptureAudioDataDir = "data/obs-plugins/win-capture-audio/";
        public const string InstallersZip = "installers.zip";
        public const string VbCablePack = "VBCABLE_Driver_Pack45.zip";
        public const string ShaderfilterZip = "obs-shaderfilter.zip";
    }
}
