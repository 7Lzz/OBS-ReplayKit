namespace ReplayKitShared
{
    // where the helper listens, the one place that address is set: compiled into the setup and the helper, and scripts/sync_shared.ps1 writes it out for the native plugin, the lua scripts and the controls dock on every build. pages served by the helper take it from their own origin
    public static class HelperEndpoint
    {
        public const string Host = "127.0.0.1";
        public const int Port = 8767;
        public static string Origin => "http://" + Host + ":" + Port;
    }
}
