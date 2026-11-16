using System.Linq;

namespace ReplayKitShared
{
    // the programs the desktop audio capture never records, whatever the recording audio tab says; the installer writes them into the scene it lays out and the helper into the live source, so a program added here reaches both
    public static class DesktopAudioExclusions
    {
        // every discord build, which the record-only discord source takes instead
        public static readonly string[] DiscordPrograms = { "Discord.exe", "DiscordSystemHelper.exe", "DiscordCanary.exe", "DiscordPTB.exe", "DiscordDevelopment.exe" };

        public static readonly string[] Programs = DiscordPrograms.Concat(new[]
        {
            "obs64.exe", "obs32.exe", "obs.exe",
            // the cef subprocess of obs, so clip playback in the docks is not recorded and monitored back into the share
            "obs-browser-page.exe",
            // the helper plays the mic test back thru the speakers
            "OBSReplayKit.exe",
            // the share window plays the share audio into the cable, and once obs restarts it is no longer inside the obs process tree the capture leaves out by itself
            ShareWindowLayout.ExeName,
        }).ToArray();
    }
}
