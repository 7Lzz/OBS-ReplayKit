namespace ReplayKitShared
{
    // the command line every launch of obs by replaykit carries: the installer, the restart, update and elevation relaunch of the helper and the startup shortcut; the elevation of the plugin tells by it whether obs was started that way (scripts/sync_shared.ps1 writes it into shared_values.hpp)
    public static class ObsLaunch
    {
        // a background colour so the window is never white while it starts, and one renderer for every page of the same site with a v8 that favours size over speed, to keep the browser of the window small; obs passes its launch flags on to its browser
        public const string Arguments = "--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays --process-per-site --js-flags=--optimize-for-size";
        // the shortcut in the startup folder of the user that starts obs at sign in, which the installer and the Windows startup setting both write, and the run key value older releases used in its place
        public const string StartupShortcutName = "OBS ReplayKit.lnk";
        public const string LegacyRunValueName = "OBS ReplayKit";
    }
}
