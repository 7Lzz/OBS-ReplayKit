namespace ReplayKitShared
{
    // the AppUserModelID the clip notifications are shown under: the helper registers it for its toasts, and the setup stamps it on the obs start menu shortcut, which windows needs before an unpackaged app may show a toast, and takes it away on uninstall
    public static class ToastIdentity
    {
        public const string AppId = "OBS.ReplayKit.Clips";
    }
}
