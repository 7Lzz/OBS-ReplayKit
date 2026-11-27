namespace ReplayKitShared
{
    // the powercfg request override that lets windows sleep while obs runs: the process it names and the requests it drops; the installer sets it and the helper changes it with the setting
    internal static class SleepOverrideRequest
    {
        internal const string Process = "obs64.exe";
        internal static readonly string[] Requests = { "DISPLAY", "SYSTEM", "AWAYMODE" };
    }
}
