namespace ReplayKitShared
{
    // the stylesheet with the tokens of the active theme that the helper writes into the dock folder on every theme change and at its start (Themes.WriteDockThemeCss); the file:// controls dock links it, and the setup windows, which run without the helper, read it to look like the rest of replaykit
    public static class ThemeSheet
    {
        public const string FileName = "rk-theme.css";
    }
}
