namespace ReplayKitShared
{
    // the command line every launch of obs by replaykit carries: the installer, the restart, update and elevation relaunch of the helper and the startup shortcut; the elevation module of lua tells by it whether obs was started that way (scripts/sync_shared.ps1 writes it into lua/shared_values.lua)
    public static class ObsLaunch
    {
        // a background colour so the window is never white while it starts, and one renderer for every page of the same site with a v8 that favours size over speed, to keep the browser of the window small; obs passes its launch flags on to its browser
        public const string Arguments = "--background-color=ff272a33 --default-background-color=ff272a33 --disable-direct-composition-video-overlays --process-per-site --js-flags=--optimize-for-size";
    }
}
