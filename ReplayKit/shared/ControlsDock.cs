namespace ReplayKitShared
{
    // the browser dock of obs that carries the controls page: the setup writes its entry into ExtraBrowserDocks of user.ini, and scripts/sync_shared.ps1 writes these into shared_values.hpp for the plugin, which finds the dock by them and keeps its title; installs before 2026-09-08 named it LegacyTitle
    public static class ControlsDock
    {
        public const string Uuid = "a59ce0ef-5d6f-4a4f-91d9-c7c3c1d4e2b0";
        public const string Title = "Controls";
        public const string LegacyTitle = "Custom Controls";
    }
}
